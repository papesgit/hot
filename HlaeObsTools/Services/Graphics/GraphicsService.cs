using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    private string _currentProfileName = "default";
    private GraphicsProfile _profile = new();
    private bool _enabled;
    private readonly int _targetFps;
    private readonly HashSet<string> _producerAtlases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appliedAtlasState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _appliedInstanceState = new(StringComparer.OrdinalIgnoreCase);
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
        _webSocket.Connected += OnWebSocketConnected;
        _webSocket.MessageReceived += OnWebSocketMessageReceived;
        _producerClient.Connected += OnProducerConnected;
        _producerClient.TriggerCompleted += OnProducerTriggerCompleted;
    }

    public bool Enabled => _enabled;
    public GraphicsProfile Profile => _profile;
    public string CurrentProfileName => _currentProfileName;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        if (_enabled)
        {
            LoadProfile(_currentProfileName);
            _ = ApplyProfileAsync();
        }
        else
        {
            _ = ClearRemoteAsync();
            _ = DestroyProducerAtlasesAsync();
        }
    }

    public void LoadProfile(string profileName)
    {
        _currentProfileName = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        _profile = _storage.Load(_currentProfileName);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile(string profileName)
    {
        _currentProfileName = string.IsNullOrWhiteSpace(profileName) ? "default" : profileName.Trim();
        _storage.Save(_currentProfileName, _profile);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteProfile(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            return;
        _storage.Delete(profileName);
        if (string.Equals(_currentProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            _currentProfileName = "default";
            _profile = _storage.Load(_currentProfileName);
            ProfileChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string[] ListProfiles()
    {
        return _storage.ListProfiles();
    }

    public Task ReloadAtlasAsync(string atlasName)
    {
        if (!_enabled)
            return Task.CompletedTask;
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

    public async Task ApplyProfileAsync()
    {
        if (!_enabled)
            return;

        var desiredAtlases = _profile.Atlases
            .Where(IsValidAtlas)
            .ToList();
        var desiredAtlasState = desiredAtlases.ToDictionary(a => a.Name, GetAtlasStateKey, StringComparer.OrdinalIgnoreCase);

        var desiredInstances = _profile.Instances
            .Where(IsValidInstance)
            .ToList();
        var desiredInstanceState = desiredInstances.ToDictionary(i => i.Name, GetInstanceStateKey, StringComparer.OrdinalIgnoreCase);

        var instancesToDestroy = _appliedInstanceState
            .Where(pair => !desiredInstanceState.TryGetValue(pair.Key, out var desiredState) || !string.Equals(pair.Value, desiredState, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var name in instancesToDestroy)
        {
            await DestroyRemoteInstanceAsync(name);
            _appliedInstanceState.Remove(name);
        }

        var atlasesToDestroy = _appliedAtlasState
            .Where(pair => !desiredAtlasState.TryGetValue(pair.Key, out var desiredState) || !string.Equals(pair.Value, desiredState, StringComparison.Ordinal))
            .Select(pair => pair.Key)
            .ToList();
        foreach (var name in atlasesToDestroy)
        {
            await DestroyRemoteAtlasAsync(name);
            _appliedAtlasState.Remove(name);
        }

        foreach (var atlas in desiredAtlases)
        {
            if (_appliedAtlasState.TryGetValue(atlas.Name, out var appliedState) &&
                desiredAtlasState.TryGetValue(atlas.Name, out var desiredState) &&
                string.Equals(appliedState, desiredState, StringComparison.Ordinal))
            {
                continue;
            }

            var info = await _producerClient.CreateAtlasAsync(new ProducerAtlasRequest
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

            if (info == null || string.IsNullOrWhiteSpace(info.Handle))
                continue;

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

            _appliedAtlasState[atlas.Name] = desiredAtlasState[atlas.Name];
        }

        foreach (var inst in desiredInstances)
        {
            if (_appliedInstanceState.TryGetValue(inst.Name, out var appliedState) &&
                desiredInstanceState.TryGetValue(inst.Name, out var desiredState) &&
                string.Equals(appliedState, desiredState, StringComparison.Ordinal))
            {
                continue;
            }

            await _webSocket.SendCommandAsync("gfx.instance.create", BuildInstancePayload(inst));

            _appliedInstanceState[inst.Name] = desiredInstanceState[inst.Name];
        }
    }

    public async Task UpdateInstanceVisibilityAsync(string name, bool visible)
    {
        if (!_enabled || !_webSocket.IsConnected)
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
        if (!_enabled || !_webSocket.IsConnected)
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
        if (!_enabled)
            return Task.CompletedTask;
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
        if (!_enabled)
            return;
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

    private async Task ClearRemoteAsync()
    {
        foreach (var name in _appliedInstanceState.Keys.ToList())
        {
            await DestroyRemoteInstanceAsync(name);
        }
        _appliedInstanceState.Clear();

        foreach (var name in _appliedAtlasState.Keys.ToList())
        {
            await DestroyRemoteAtlasAsync(name);
        }
        _appliedAtlasState.Clear();
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

    private async Task DestroyProducerAtlasesAsync()
    {
        foreach (var name in _producerAtlases.ToList())
        {
            await _producerClient.DestroyAtlasAsync(name);
            _producerAtlases.Remove(name);
        }
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
        if (Uri.TryCreate(htmlPath, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        return File.Exists(htmlPath);
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

    private static string GetAtlasStateKey(GraphicsAtlas atlas)
    {
        return JsonSerializer.Serialize(atlas);
    }

    private static string GetInstanceStateKey(GraphicsInstance inst)
    {
        return JsonSerializer.Serialize(inst);
    }

    private static Dictionary<string, object?> BuildInstancePayload(GraphicsInstance inst)
    {
        var attachPayload = inst.AttachSlot >= 0
            ? new
            {
                slot = inst.AttachSlot,
                useYaw = inst.AttachUseYaw,
                usePitch = inst.AttachUsePitch,
                useRoll = inst.AttachUseRoll,
                attachment = inst.AttachAttachmentName
            }
            : null;

        var payload = new Dictionary<string, object?>
        {
            ["name"] = inst.Name,
            ["attach"] = attachPayload,
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

    private void OnGameStateUpdated(object? sender, GsiGameState e)
    {
        if (_enabled && _producerClient.IsConnected && !string.IsNullOrWhiteSpace(e.RawJson))
        {
            var extras = _gsiExtrasTracker.Update(e.RawJson);
            _ = _producerClient.SendGsiAsync(e.RawJson, e.Heartbeat, extras);
        }

        // Profiles are now user-selected (not tied to map).
    }

    private void OnWebSocketConnected(object? sender, EventArgs e)
    {
        if (!_enabled)
            return;
        _ = ApplyProfileAsync();
    }

    private void OnProducerConnected(object? sender, EventArgs e)
    {
        if (!_enabled)
            return;
        _ = ApplyProfileAsync();
    }

    private void OnProducerTriggerCompleted(object? sender, ProducerTriggerEvent e)
    {
        if (!_enabled)
            return;
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
        _webSocket.Connected -= OnWebSocketConnected;
        _webSocket.MessageReceived -= OnWebSocketMessageReceived;
        _producerClient.Connected -= OnProducerConnected;
        _producerClient.TriggerCompleted -= OnProducerTriggerCompleted;
    }
}

public sealed record GraphicsVisibilityEvent(IReadOnlyList<string> InstanceNames, bool Visible);
public sealed record GraphicsCameraTransform(double PosX, double PosY, double PosZ, double Pitch, double Yaw, double Roll);
