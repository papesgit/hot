using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private string _currentMap = string.Empty;
    private GraphicsProfile _profile = new();
    private bool _enabled;
    private readonly int _targetFps;
    private readonly HashSet<string> _producerAtlases = new(StringComparer.OrdinalIgnoreCase);
    private readonly GsiExtrasTracker _gsiExtrasTracker = new();

    public event EventHandler? ProfileChanged;

    public GraphicsService(HlaeWebSocketClient webSocket, GraphicsProducerClient producerClient, GsiServer gsiServer, GraphicsProfileStorage storage, int targetFps = 30)
    {
        _webSocket = webSocket;
        _producerClient = producerClient;
        _gsiServer = gsiServer;
        _storage = storage;
        _targetFps = targetFps;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _webSocket.Connected += OnWebSocketConnected;
        _producerClient.Connected += OnProducerConnected;
    }

    public bool Enabled => _enabled;
    public GraphicsProfile Profile => _profile;
    public string CurrentMap => _currentMap;

    public void SetEnabled(bool enabled)
    {
        if (_enabled == enabled)
            return;

        _enabled = enabled;
        if (_enabled)
        {
            LoadProfileForMap(_currentMap);
            _ = ApplyProfileAsync();
        }
        else
        {
            _ = ClearRemoteAsync();
            _ = DestroyProducerAtlasesAsync();
        }
    }

    public void LoadProfileForMap(string mapName)
    {
        _currentMap = string.IsNullOrWhiteSpace(mapName) ? "default" : mapName;
        _profile = _storage.Load(_currentMap);
        ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveProfile()
    {
        _storage.Save(_currentMap, _profile);
    }

    public Task ReloadAtlasAsync(string atlasName)
    {
        if (!_enabled)
            return Task.CompletedTask;
        return _producerClient.ReloadAtlasAsync(atlasName);
    }

    public async Task ApplyProfileAsync()
    {
        if (!_enabled)
            return;

        await ClearRemoteAsync(_profile);

        var activeAtlases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var atlas in _profile.Atlases)
        {
            if (!atlas.Enabled)
                continue;
            if (string.IsNullOrWhiteSpace(atlas.HtmlPath) || !File.Exists(atlas.HtmlPath))
                continue;

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

            activeAtlases.Add(atlas.Name);
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
        }

        foreach (var inst in _profile.Instances)
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

            await _webSocket.SendCommandAsync("gfx.instance.create", new
            {
                name = inst.Name,
                atlas = inst.Atlas,
                region = inst.Region,
                attach = attachPayload,
                pos = new[] { inst.PosX, inst.PosY, inst.PosZ },
                ang = new[] { inst.Pitch, inst.Yaw, inst.Roll },
                scale = new[] { inst.ScaleX, inst.ScaleY },
                visible = inst.Visible,
                depthTest = inst.DepthTest,
                depthWrite = inst.DepthWrite
            });
        }

        var toRemove = _producerAtlases.Where(name => !activeAtlases.Contains(name)).ToList();
        foreach (var name in toRemove)
        {
            await _producerClient.DestroyAtlasAsync(name);
            _producerAtlases.Remove(name);
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
        if (instance == null || string.IsNullOrWhiteSpace(instance.Atlas))
            return Task.CompletedTask;
        return _producerClient.TriggerAsync(instance.Atlas, action, instanceName);
    }

    public async Task TriggerAtlasInstancesAsync(string atlasName, string action)
    {
        if (!_enabled)
            return;
        if (string.IsNullOrWhiteSpace(atlasName))
            return;
        var instances = _profile.Instances.Where(i => i.Atlas == atlasName).ToList();
        if (instances.Count == 0)
            return;
        foreach (var inst in instances)
        {
            await _producerClient.TriggerAsync(atlasName, action, inst.Name);
        }
    }

    private async Task ClearRemoteAsync()
    {
        await ClearRemoteAsync(_profile);
    }

    private async Task ClearRemoteAsync(GraphicsProfile profile)
    {
        if (!_webSocket.IsConnected)
            return;

        foreach (var inst in profile.Instances)
        {
            await _webSocket.SendCommandAsync("gfx.instance.destroy", new { name = inst.Name });
        }
        foreach (var atlas in profile.Atlases)
        {
            await _webSocket.SendCommandAsync("gfx.atlas.destroy", new { name = atlas.Name });
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

    private void OnGameStateUpdated(object? sender, GsiGameState e)
    {
        if (_enabled && _producerClient.IsConnected && !string.IsNullOrWhiteSpace(e.RawJson))
        {
            var extras = _gsiExtrasTracker.Update(e.RawJson);
            _ = _producerClient.SendGsiAsync(e.RawJson, e.Heartbeat, extras);
        }

        if (string.Equals(_currentMap, e.MapName, StringComparison.OrdinalIgnoreCase))
            return;
        var previousProfile = _profile;
        _currentMap = e.MapName;
        if (!_enabled)
            return;
        _ = ClearRemoteAsync(previousProfile);
        LoadProfileForMap(_currentMap);
        _ = ApplyProfileAsync();
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

    public void Dispose()
    {
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _webSocket.Connected -= OnWebSocketConnected;
        _producerClient.Connected -= OnProducerConnected;
    }
}
