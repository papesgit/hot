using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.HotLink;

public sealed class HotLinkGameClock : IDisposable
{
    private const double ResetThresholdSeconds = 5.0;
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly HotLinkSettings _settings;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private double? _sampleTime;
    private DateTimeOffset _sampleUtc;
    private double _rate = 1.0;
    private bool _disposed;

    public HotLinkGameClock(HlaeWebSocketClient webSocketClient, HotLinkSettings settings)
    {
        _webSocketClient = webSocketClient;
        _settings = settings;
        _webSocketClient.MessageReceived += OnWebSocketMessage;
        _settings.PropertyChanged += OnSettingsChanged;
        _ = Task.Run(() => SampleLoopAsync(_cts.Token));
    }

    public event EventHandler? TimeReset;

    public double? EstimateGameTime()
    {
        lock (_sync)
        {
            if (!_sampleTime.HasValue)
                return null;

            var elapsed = (DateTimeOffset.UtcNow - _sampleUtc).TotalSeconds;
            // Do not let a missing HLAE response make countdowns run indefinitely.
            if (elapsed > 1.5)
                return _sampleTime;
            return _sampleTime.Value + Math.Max(0, elapsed) * _rate;
        }
    }

    private async Task SampleLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (_settings.IsClient && _settings.ClientConnectionEnabled)
            {
                try { await _webSocketClient.SendCommandAsync("curtime_get").ConfigureAwait(false); }
                catch { }
            }
            try { await Task.Delay(_settings.IsClient && _settings.ClientConnectionEnabled ? 250 : 500, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(HotLinkSettings.Role) or nameof(HotLinkSettings.ClientConnectionEnabled))) return;
        if (_settings.IsClient && _settings.ClientConnectionEnabled) return;
        lock (_sync) { _sampleTime = null; _rate = 1; }
        TimeReset?.Invoke(this, EventArgs.Empty);
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "curtime" ||
                (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) ||
                !root.TryGetProperty("value", out var valueElement) || !valueElement.TryGetDouble(out var value))
                return;

            var now = DateTimeOffset.UtcNow;
            var reset = false;
            lock (_sync)
            {
                if (_sampleTime.HasValue)
                {
                    reset = value + ResetThresholdSeconds < _sampleTime.Value;
                    var wallDelta = (now - _sampleUtc).TotalSeconds;
                    var gameDelta = value - _sampleTime.Value;
                    if (wallDelta > 0.05 && gameDelta >= 0)
                    {
                        var measuredRate = gameDelta / wallDelta;
                        _rate = measuredRate < 0.05 ? 0 : Math.Clamp(measuredRate, 0, 4);
                    }
                }
                _sampleTime = value;
                _sampleUtc = now;
            }
            if (reset)
                TimeReset?.Invoke(this, EventArgs.Empty);
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _webSocketClient.MessageReceived -= OnWebSocketMessage;
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
