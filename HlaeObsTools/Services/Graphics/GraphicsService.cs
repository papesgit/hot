using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.WebSocket;

namespace HlaeObsTools.Services.Graphics;

public sealed class GraphicsService : IDisposable
{
    private readonly HlaeWebSocketClient _webSocket;
    private readonly GraphicsProducerClient _producerClient;
    private readonly GsiServer _gsiServer;
    private readonly GraphicsProfileStorage _storage;
    private string _currentProfileName = GraphicsProfileStorage.EmptyProfileName;
    private GraphicsProfile _profile = new();
    private readonly int _targetFps;
    private readonly HashSet<string> _producerAtlases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appliedAtlasProducerState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appliedAtlasRegionState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _appliedAtlasRegionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appliedInstanceState = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _applySemaphore = new(1, 1);
    private string? _appliedProfileName;
    private readonly GsiExtrasTracker _gsiExtrasTracker = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IReadOnlyList<string>>> _pendingImageListRequests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<GraphicsCameraTransform?>> _pendingCameraRequests = new(StringComparer.Ordinal);

    public event EventHandler? ProfileChanged;
    public event EventHandler<GraphicsVisibilityEvent>? InstancesVisibilityChanged;

    public GraphicsService(HlaeWebSocketClient webSocket, GraphicsProducerClient producerClient, GsiServer gsiServer, GraphicsProfileStorage storage, int targetFps = 30)
    {
        _webSocket = webSocket;
        _producerClient = producerClient;
        _gsiServer = gsiServer;
        _storage = storage;
        _targetFps = targetFps;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _webSocket.MessageReceived += OnWebSocketMessageReceived;
        _producerClient.TriggerCompleted += OnProducerTriggerCompleted;
    }

    public GraphicsProfile Profile => _profile;
    public string CurrentProfileName => _currentProfileName;

    public void LoadProfile(string profileName)
    {
        _currentProfileName = string.IsNullOrWhiteSpace(profileName) ? GraphicsProfileStorage.EmptyProfileName : profileName.Trim();
        _profile = _storage.Load(_currentProfileName);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool SaveProfile(string profileName)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? GraphicsProfileStorage.EmptyProfileName : profileName.Trim();
        if (!_storage.Save(name, _profile))
            return false;

        _currentProfileName = name;
        ProfileChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void DeleteProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return;
        if (!_storage.Delete(profileName))
            return;
        if (string.Equals(_currentProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            _currentProfileName = GraphicsProfileStorage.EmptyProfileName;
            _profile = _storage.Load(_currentProfileName);
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string[] ListProfiles()
    {
        return _storage.ListProfiles();
    }

    public Task<ProducerCommandResult> ReloadAtlasAsync(string atlasName)
    {
        return _producerClient.ReloadAtlasAsync(atlasName);
    }

    public async Task<IReadOnlyList<string>> ListAvailableImagesAsync()
    {
        if (!_webSocket.IsConnected)
            return Array.Empty<string>();

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingImageListRequests[requestId] = tcs;

        try
        {
            await _webSocket.SendCommandAsync("gfx.image.list", new { requestId });
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
            if (!completed)
                return Array.Empty<string>();

            return await tcs.Task;
        }
        finally
        {
            _pendingImageListRequests.TryRemove(requestId, out _);
        }
    }

    public async Task<GraphicsCameraTransform?> GetCurrentCameraTransformAsync()
    {
        if (!_webSocket.IsConnected)
            return null;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<GraphicsCameraTransform?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingCameraRequests[requestId] = tcs;

        try
        {
            await _webSocket.SendCommandAsync("gfx.camera.get", new { requestId });
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000)) == tcs.Task;
            if (!completed)
                return null;

            return await tcs.Task;
        }
        finally
        {
            _pendingCameraRequests.TryRemove(requestId, out _);
        }
    }

    public async Task<GraphicsApplyResponse> ApplyProfileAsync()
    {
        await _applySemaphore.WaitAsync();
        try
        {
            if (!_webSocket.IsConnected || !_producerClient.IsConnected)
            {
                if (!_webSocket.IsConnected)
                    return new GraphicsApplyResponse(GraphicsApplyResult.HlaeDisconnected);
                return new GraphicsApplyResponse(GraphicsApplyResult.ProducerDisconnected);
            }

            var desiredAtlases = GetDistinctByName(_profile.Atlases.Where(IsValidAtlas), a => a.Name);
            var desiredAtlasProducerState = desiredAtlases.ToDictionary(a => a.Name, GetAtlasProducerStateKey, StringComparer.OrdinalIgnoreCase);
            var desiredAtlasRegionState = desiredAtlases.ToDictionary(a => a.Name, GetAtlasRegionStateKey, StringComparer.OrdinalIgnoreCase);
            var desiredAtlasRegionIds = desiredAtlases.ToDictionary(a => a.Name, GetAtlasRegionIds, StringComparer.OrdinalIgnoreCase);

            var desiredInstances = GetDistinctByName(_profile.Instances.Where(IsValidInstance), i => i.Name);
            var desiredInstanceState = desiredInstances.ToDictionary(i => i.Name, GetInstanceStateKey, StringComparer.OrdinalIgnoreCase);

            if (_appliedProfileName != null && !string.Equals(_appliedProfileName, _currentProfileName, StringComparison.OrdinalIgnoreCase))
            {
                await ClearRemoteCoreAsync();
            }

            var instancesToDestroy = _appliedInstanceState
                .Where(pair => !desiredInstanceState.ContainsKey(pair.Key))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var name in instancesToDestroy)
            {
                await DestroyRemoteInstanceAsync(name);
                _appliedInstanceState.Remove(name);
            }

            var atlasesToDestroy = _appliedAtlasProducerState
                .Where(pair => !desiredAtlasProducerState.ContainsKey(pair.Key))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var name in atlasesToDestroy)
            {
                await DestroyRemoteAtlasAsync(name);
                RemoveAppliedAtlasState(name);
            }

            var producerCreateFailed = false;
            var producerCreateNoResponse = false;
            ProducerAtlasCreateResult? firstCreateFailure = null;
            foreach (var atlas in desiredAtlases)
            {
                var desiredProducerState = desiredAtlasProducerState[atlas.Name];
                var producerChanged = !_appliedAtlasProducerState.TryGetValue(atlas.Name, out var appliedProducerState) ||
                    !string.Equals(appliedProducerState, desiredProducerState, StringComparison.Ordinal);

                if (producerChanged)
                {
                    var createResult = await _producerClient.CreateAtlasAsync(new ProducerAtlasRequest
                    {
                        Name = atlas.Name,
                        Width = atlas.Width,
                        Height = atlas.Height,
                        Format = atlas.Format == GraphicsAtlasFormat.Rgba8 ? "RGBA8" : "BGRA8",
                        AlphaMode = atlas.AlphaMode == GraphicsAlphaMode.Straight ? "straight" : "premultiplied",
                        KeyedMutex = atlas.KeyedMutex,
                        HtmlPath = atlas.HtmlPath,
                        TargetFps = _targetFps
                    });

                    if (createResult.Result == ProducerCommandResult.NoResponse)
                    {
                        producerCreateNoResponse = true;
                        continue;
                    }

                    var info = createResult.Info;
                    if (info == null || string.IsNullOrWhiteSpace(info.Handle))
                    {
                        producerCreateFailed = true;
                        firstCreateFailure ??= createResult;
                        continue;
                    }

                    _producerAtlases.Add(atlas.Name);

                    await _webSocket.SendCommandAsync("gfx.atlas.create", new
                    {
                        name = atlas.Name,
                        handle = info.Handle,
                        width = info.Width,
                        height = info.Height,
                        format = info.Format,
                        alphaMode = info.AlphaMode,
                        keyedMutex = info.KeyedMutex
                    });

                    _appliedAtlasProducerState[atlas.Name] = desiredProducerState;
                }

                var desiredRegionIds = desiredAtlasRegionIds[atlas.Name];
                if (_appliedAtlasRegionIds.TryGetValue(atlas.Name, out var appliedRegionIds))
                {
                    foreach (var regionId in appliedRegionIds.Where(id => !desiredRegionIds.Contains(id)).ToList())
                    {
                        await _webSocket.SendCommandAsync("gfx.atlas.region.remove", new
                        {
                            atlas = atlas.Name,
                            id = regionId
                        });
                    }
                }

                var desiredRegionState = desiredAtlasRegionState[atlas.Name];
                var regionsChanged = !_appliedAtlasRegionState.TryGetValue(atlas.Name, out var appliedRegionState) ||
                    !string.Equals(appliedRegionState, desiredRegionState, StringComparison.Ordinal);

                if (regionsChanged || producerChanged)
                {
                    foreach (var region in atlas.Regions)
                    {
                        await _webSocket.SendCommandAsync("gfx.atlas.region.set", new
                        {
                            atlas = atlas.Name,
                            id = region.Id,
                            u0 = region.U0,
                            v0 = region.V0,
                            u1 = region.U1,
                            v1 = region.V1,
                            defaultSize = new[] { region.DefaultWidth, region.DefaultHeight }
                        });
                    }

                    _appliedAtlasRegionState[atlas.Name] = desiredRegionState;
                    _appliedAtlasRegionIds[atlas.Name] = desiredRegionIds;
                }
            }

            foreach (var inst in desiredInstances)
            {
                var exists = _appliedInstanceState.TryGetValue(inst.Name, out var appliedState);
                var desiredState = desiredInstanceState[inst.Name];
                if (exists && string.Equals(appliedState, desiredState, StringComparison.Ordinal))
                    continue;

                await _webSocket.SendCommandAsync(exists ? "gfx.instance.update" : "gfx.instance.create", BuildInstancePayload(inst));

                _appliedInstanceState[inst.Name] = desiredState;
            }

            _appliedProfileName = _currentProfileName;
            if (producerCreateFailed)
            {
                return new GraphicsApplyResponse(
                    GraphicsApplyResult.ProducerAtlasCreateFailed,
                    firstCreateFailure?.ErrorCode,
                    firstCreateFailure?.Error);
            }

            if (producerCreateNoResponse)
                return new GraphicsApplyResponse(GraphicsApplyResult.ProducerAtlasCreateNoResponse);

            return new GraphicsApplyResponse(GraphicsApplyResult.Applied);
        }
        finally
        {
            _applySemaphore.Release();
        }
    }

    public async Task UpdateInstanceVisibilityAsync(string name, bool visible)
    {
        if (!_webSocket.IsConnected)
            return;
        if (string.IsNullOrWhiteSpace(name))
            return;
        await _webSocket.SendCommandAsync("gfx.instance.update", new
        {
            name,
            visible
        });
    }

    public async Task UpdateInstancesVisibilityAsync(IEnumerable<GraphicsInstance> instances, bool visible)
    {
        if (!_webSocket.IsConnected)
            return;
        foreach (var inst in instances)
        {
            if (string.IsNullOrWhiteSpace(inst.Name))
                continue;
            await _webSocket.SendCommandAsync("gfx.instance.update", new
            {
                name = inst.Name,
                visible
            });
        }
    }

    public Task TriggerInstanceAsync(string instanceName, string action)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
            return Task.CompletedTask;
        var instance = _profile.Instances.FirstOrDefault(i => i.Name == instanceName);
        if (instance == null)
            return Task.CompletedTask;
        if (instance.SourceType == GraphicsInstanceSourceType.Image)
        {
            if (string.Equals(action, "animIn", StringComparison.OrdinalIgnoreCase))
            {
                _ = UpdateInstanceVisibilityAsync(instance.Name, true);
                RaiseInstancesVisibilityChanged(new[] { instance.Name }, true);
            }
            else if (string.Equals(action, "animOut", StringComparison.OrdinalIgnoreCase))
            {
                _ = UpdateInstanceVisibilityAsync(instance.Name, false);
                RaiseInstancesVisibilityChanged(new[] { instance.Name }, false);
            }
            return Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(instance.Atlas) || string.IsNullOrWhiteSpace(instance.Region))
            return Task.CompletedTask;
        if (string.Equals(action, "animIn", StringComparison.OrdinalIgnoreCase))
        {
            _ = UpdateInstanceVisibilityAsync(instance.Name, true);
            RaiseInstancesVisibilityChanged(new[] { instance.Name }, true);
        }
        return _producerClient.TriggerAsync(instance.Atlas, action, instance.Region);
    }

    public async Task TriggerAtlasInstancesAsync(string atlasName, string action)
    {
        if (string.IsNullOrWhiteSpace(atlasName))
            return;
        var instances = _profile.Instances
            .Where(i => i.SourceType == GraphicsInstanceSourceType.Atlas)
            .Where(i => string.Equals(i.Atlas, atlasName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var regions = instances
            .Select(i => i.Region)
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (regions.Count == 0)
            return;
        if (string.Equals(action, "animIn", StringComparison.OrdinalIgnoreCase))
        {
            await UpdateInstancesVisibilityAsync(instances, true);
            RaiseInstancesVisibilityChanged(instances.Select(i => i.Name), true);
        }
        foreach (var region in regions)
        {
            await _producerClient.TriggerAsync(atlasName, action, region);
        }
    }

    private async Task ClearRemoteCoreAsync()
    {
        foreach (var name in _appliedInstanceState.Keys.ToList())
        {
            await DestroyRemoteInstanceAsync(name);
        }
        _appliedInstanceState.Clear();

        foreach (var name in _appliedAtlasProducerState.Keys.ToList())
        {
            await DestroyRemoteAtlasAsync(name);
        }
        await DestroyProducerAtlasesCoreAsync();
        _appliedAtlasProducerState.Clear();
        _appliedAtlasRegionState.Clear();
        _appliedAtlasRegionIds.Clear();
        _appliedProfileName = null;
    }

    private async Task DestroyRemoteInstanceAsync(string name)
    {
        if (!_webSocket.IsConnected)
            return;
        await _webSocket.SendCommandAsync("gfx.instance.destroy", new { name });
    }

    private async Task DestroyRemoteAtlasAsync(string name)
    {
        if (_webSocket.IsConnected)
        {
            await _webSocket.SendCommandAsync("gfx.atlas.destroy", new { name });
        }

        if (_producerAtlases.Contains(name))
        {
            await _producerClient.DestroyAtlasAsync(name);
            _producerAtlases.Remove(name);
        }
    }

    private async Task DestroyProducerAtlasesCoreAsync()
    {
        foreach (var name in _producerAtlases.ToList())
        {
            await _producerClient.DestroyAtlasAsync(name);
            _producerAtlases.Remove(name);
        }
    }

    private void RemoveAppliedAtlasState(string name)
    {
        _appliedAtlasProducerState.Remove(name);
        _appliedAtlasRegionState.Remove(name);
        _appliedAtlasRegionIds.Remove(name);
    }

    private static bool IsValidAtlas(GraphicsAtlas atlas)
    {
        return atlas.Enabled &&
               !string.IsNullOrWhiteSpace(atlas.Name) &&
               !string.IsNullOrWhiteSpace(atlas.HtmlPath) &&
               IsSupportedHtmlPath(atlas.HtmlPath);
    }

    private static bool IsSupportedHtmlPath(string htmlPath)
    {
        if (htmlPath.Length >= 2 && htmlPath[1] == ':')
            return true;

        if (Uri.TryCreate(htmlPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        if (Uri.TryCreate(htmlPath, UriKind.Absolute, out uri))
            return uri.IsFile;

        return true;
    }

    private static bool IsValidInstance(GraphicsInstance inst)
    {
        if (string.IsNullOrWhiteSpace(inst.Name))
            return false;

        return inst.SourceType switch
        {
            GraphicsInstanceSourceType.Image => !string.IsNullOrWhiteSpace(inst.ImageFile),
            GraphicsInstanceSourceType.Atlas => !string.IsNullOrWhiteSpace(inst.Atlas) && !string.IsNullOrWhiteSpace(inst.Region),
            _ => false
        };
    }

    private static string GetAtlasProducerStateKey(GraphicsAtlas atlas)
    {
        return JsonSerializer.Serialize(new
        {
            atlas.Width,
            atlas.Height,
            atlas.Format,
            atlas.AlphaMode,
            atlas.KeyedMutex,
            atlas.HtmlPath
        });
    }

    private static string GetAtlasRegionStateKey(GraphicsAtlas atlas)
    {
        return JsonSerializer.Serialize(atlas.Regions);
    }

    private static HashSet<string> GetAtlasRegionIds(GraphicsAtlas atlas)
    {
        return atlas.Regions
            .Select(region => region.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetInstanceStateKey(GraphicsInstance inst)
    {
        return JsonSerializer.Serialize(inst);
    }

    private static Dictionary<string, object?> BuildInstancePayload(GraphicsInstance inst)
    {
        var payload = new Dictionary<string, object?>
        {
            ["name"] = inst.Name,
            ["attach"] = new
            {
                slot = inst.AttachSlot,
                useYaw = inst.AttachUseYaw,
                usePitch = inst.AttachUsePitch,
                useRoll = inst.AttachUseRoll,
                attachment = inst.AttachAttachmentName
            },
            ["pos"] = new[] { inst.PosX, inst.PosY, inst.PosZ },
            ["ang"] = new[] { inst.Pitch, inst.Yaw, inst.Roll },
            ["scale"] = new[] { inst.ScaleX, inst.ScaleY },
            ["visible"] = inst.Visible,
            ["depthTest"] = inst.DepthTest,
            ["depthWrite"] = inst.DepthWrite
        };

        if (inst.SourceType == GraphicsInstanceSourceType.Image)
        {
            payload["imageFile"] = inst.ImageFile;
        }
        else
        {
            payload["atlas"] = inst.Atlas;
            payload["region"] = inst.Region;
        }

        return payload;
    }

    private static List<T> GetDistinctByName<T>(IEnumerable<T> items, Func<T, string> getName)
    {
        var result = new List<T>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (names.Add(getName(item)))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState e)
    {
        if (_producerClient.IsConnected && !string.IsNullOrWhiteSpace(e.RawJson))
        {
            var extras = _gsiExtrasTracker.Update(e.RawJson);
            _ = _producerClient.SendGsiAsync(e.RawJson, e.Heartbeat, extras);
        }

        // Profiles are now user-selected (not tied to map).
    }

    private void OnProducerTriggerCompleted(object? sender, ProducerTriggerEvent e)
    {
        if (!string.Equals(e.Action, "animOut", StringComparison.OrdinalIgnoreCase))
            return;
        if (string.IsNullOrWhiteSpace(e.Atlas) || string.IsNullOrWhiteSpace(e.Target))
            return;
        var instances = _profile.Instances
            .Where(i => string.Equals(i.Atlas, e.Atlas, StringComparison.OrdinalIgnoreCase))
            .Where(i => string.Equals(i.Region, e.Target, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (instances.Count == 0)
            return;
        _ = UpdateInstancesVisibilityAsync(instances, false);
        RaiseInstancesVisibilityChanged(instances.Select(i => i.Name), false);
    }

    private void RaiseInstancesVisibilityChanged(IEnumerable<string> names, bool visible)
    {
        var list = names.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            return;
        InstancesVisibilityChanged?.Invoke(this, new GraphicsVisibilityEvent(list, visible));
    }

    private void OnWebSocketMessageReceived(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp))
                return;
            var type = typeProp.GetString();
            if (!root.TryGetProperty("requestId", out var requestIdProp))
                return;

            var requestId = requestIdProp.GetString();
            if (string.IsNullOrWhiteSpace(requestId))
                return;

            if (string.Equals(type, "gfx.image.list", StringComparison.Ordinal))
            {
                if (!_pendingImageListRequests.TryRemove(requestId, out var imageTcs))
                    return;

                var result = Array.Empty<string>();
                if (root.TryGetProperty("images", out var imagesProp) && imagesProp.ValueKind == JsonValueKind.Array)
                {
                    result = imagesProp
                        .EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Cast<string>()
                        .ToArray();
                }

                imageTcs.TrySetResult(result);
                return;
            }

            if (string.Equals(type, "gfx.camera.get", StringComparison.Ordinal))
            {
                if (!_pendingCameraRequests.TryRemove(requestId, out var cameraTcs))
                    return;

                if (root.TryGetProperty("pos", out var posProp) && posProp.ValueKind == JsonValueKind.Array && posProp.GetArrayLength() == 3
                    && root.TryGetProperty("ang", out var angProp) && angProp.ValueKind == JsonValueKind.Array && angProp.GetArrayLength() == 3)
                {
                    cameraTcs.TrySetResult(new GraphicsCameraTransform(
                        posProp[0].GetDouble(),
                        posProp[1].GetDouble(),
                        posProp[2].GetDouble(),
                        angProp[0].GetDouble(),
                        angProp[1].GetDouble(),
                        angProp[2].GetDouble()
                    ));
                }
                else
                {
                    cameraTcs.TrySetResult(null);
                }
            }
        }
        catch
        {
            // Ignore unrelated or malformed websocket messages.
        }
    }

    public void Dispose()
    {
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _webSocket.MessageReceived -= OnWebSocketMessageReceived;
        _producerClient.TriggerCompleted -= OnProducerTriggerCompleted;
    }
}

public sealed record GraphicsVisibilityEvent(IReadOnlyList<string> InstanceNames, bool Visible);
public sealed record GraphicsCameraTransform(double PosX, double PosY, double PosZ, double Pitch, double Yaw, double Roll);
public sealed record GraphicsApplyResponse(GraphicsApplyResult Result, string? ErrorCode = null, string? Error = null);

public enum GraphicsApplyResult
{
    Applied,
    HlaeDisconnected,
    ProducerDisconnected,
    ProducerAtlasCreateFailed,
    ProducerAtlasCreateNoResponse
}
