using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.HotLink;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.ReplayDirector;

/// <summary>Replay-specific consumer of the shared HOT Link event stream.</summary>
public sealed class ReplayDirectorClient : IDisposable
{
    private const double LateSwitchGraceSeconds = 0.25;
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly GsiServer _gsiServer;
    private readonly HotLinkSettings _settings;
    private readonly VmixReplaySettings _vmixReplaySettings;
    private readonly HotLinkClient _hotLinkClient;
    private readonly HotLinkGameClock _clock;
    private readonly object _sync = new();
    private readonly List<HotLinkKillEvent> _pending = new();
    private readonly HashSet<int> _aliveSlots = new();
    private CancellationTokenSource? _cts;
    private int _currentTargetSlot = -1;
    private double? _currentTargetKillGameTime;
    private bool _disposed;

    public ReplayDirectorClient(HlaeWebSocketClient webSocketClient, GsiServer gsiServer,
        HotLinkSettings settings, VmixReplaySettings vmixReplaySettings,
        HotLinkClient hotLinkClient, HotLinkGameClock clock)
    {
        _webSocketClient = webSocketClient;
        _gsiServer = gsiServer;
        _settings = settings;
        _vmixReplaySettings = vmixReplaySettings;
        _hotLinkClient = hotLinkClient;
        _clock = clock;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _hotLinkClient.EventsReceived += OnEventsReceived;
        _hotLinkClient.SessionChanged += OnReset;
        _clock.TimeReset += OnReset;
        _settings.PropertyChanged += OnSettingsChanged;
        ApplySettings();
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(HotLinkSettings.Role) or nameof(HotLinkSettings.ClientMode) or nameof(HotLinkSettings.ClientConnectionEnabled))
            ApplySettings();
    }

    private void ApplySettings()
    {
        if (_settings.IsReplayDirectorMode && _settings.ClientConnectionEnabled) Start();
        else Stop();
    }

    private void Start()
    {
        lock (_sync)
        {
            if (_cts != null) return;
            _cts = new CancellationTokenSource();
            foreach (var kill in _hotLinkClient.GetRecentEvents())
                if (kill.Attacker.ObserverSlot is >= 0 and <= 9 && !IsExpired(kill)) _pending.Add(kill);
            _ = Task.Run(() => SchedulerLoopAsync(_cts.Token));
        }
        _settings.ScheduledTarget = "Replay Director waiting for HOT Link events.";
    }

    private void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync) { cts = _cts; _cts = null; _pending.Clear(); _currentTargetSlot = -1; _currentTargetKillGameTime = null; }
        try { cts?.Cancel(); } catch { }
    }

    private void OnEventsReceived(object? sender, HotLinkKillEvent[] events)
    {
        if (!_settings.IsReplayDirectorMode) return;
        lock (_sync)
        {
            foreach (var kill in events)
                if (kill.Attacker.ObserverSlot is >= 0 and <= 9)
                    _pending.Add(kill);
        }
    }

    private void OnReset(object? sender, EventArgs e)
    {
        lock (_sync) { _pending.Clear(); _currentTargetSlot = -1; _currentTargetKillGameTime = null; }
        _settings.ScheduledTarget = "Cleared Replay Director state after clock/session reset.";
    }

    private async Task SchedulerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            HotLinkKillEvent? selected;
            lock (_sync)
            {
                _pending.RemoveAll(IsExpired);
                selected = SelectCandidate();
                if (selected != null) _pending.Remove(selected);
            }
            if (selected != null) await SwitchToKillAsync(selected, token).ConfigureAwait(false);
            var time = _clock.EstimateGameTime();
            if (time.HasValue) _settings.LocalGameTime = time.Value.ToString("F2", CultureInfo.InvariantCulture);
            try { await Task.Delay(50, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private HotLinkKillEvent? SelectCandidate()
    {
        var now = _clock.EstimateGameTime();
        if (!now.HasValue) return null;
        if (_currentTargetKillGameTime.HasValue && now < _currentTargetKillGameTime + Math.Max(0, _settings.SwitchLockSeconds)) return null;
        var due = _pending.Where(k => k.GameTime.HasValue)
            .Where(k => _aliveSlots.Contains(k.Attacker.ObserverSlot))
            .Where(k => !_settings.OnlyFollowMissedKills || !k.MainCaught)
            .Where(k => now >= k.GameTime!.Value - _settings.PreSwitchSeconds && now <= k.GameTime.Value + LateSwitchGraceSeconds)
            .ToArray();
        if (due.Length == 0) return null;
        var earliest = due.Min(k => k.GameTime!.Value);
        return due.Where(k => k.GameTime!.Value <= earliest + Math.Max(0.25, _vmixReplaySettings.ExtendWindowSeconds))
            .OrderByDescending(Score).ThenBy(k => k.GameTime).ThenBy(k => k.Id).First();
    }

    private int Score(HotLinkKillEvent kill)
    {
        var score = kill.MainCaught ? 0 : 1000;
        if (kill.Attacker.ObserverSlot == _currentTargetSlot) score += 200;
        if (kill.Headshot) score += 25;
        if (kill.Wallbang || kill.Noscope || kill.ThroughSmoke) score += 50;
        if (kill.Blind || kill.InAir) score += 25;
        score += _pending.Count(other => other != kill && other.Attacker.ObserverSlot == kill.Attacker.ObserverSlot &&
            other.GameTime.HasValue && kill.GameTime.HasValue && Math.Abs(other.GameTime.Value - kill.GameTime.Value) <= _vmixReplaySettings.ExtendWindowSeconds) * 100;
        return score;
    }

    private bool IsExpired(HotLinkKillEvent kill)
    {
        var now = _clock.EstimateGameTime();
        return kill.GameTime.HasValue && now.HasValue && now > kill.GameTime.Value + Math.Max(3, _vmixReplaySettings.ExtendWindowSeconds);
    }

    private async Task SwitchToKillAsync(HotLinkKillEvent kill, CancellationToken token)
    {
        _currentTargetSlot = kill.Attacker.ObserverSlot;
        _currentTargetKillGameTime = kill.GameTime;
        _settings.ScheduledTarget = $"Switched to slot {kill.Attacker.ObserverSlot + 1} for kill at {kill.GameTime?.ToString("F2", CultureInfo.InvariantCulture)}.";
        await _webSocketClient.SendCommandAsync("spectate_slot", new { observer_slot = kill.Attacker.ObserverSlot }).ConfigureAwait(false);
        _settings.LastSwitch = $"spectate_slot {kill.Attacker.ObserverSlot}";
        _ = ScheduleReplayMarkAsync(kill, token);
    }

    private async Task ScheduleReplayMarkAsync(HotLinkKillEvent kill, CancellationToken token)
    {
        try
        {
            var now = _clock.EstimateGameTime();
            var delay = kill.GameTime.HasValue && now.HasValue ? kill.GameTime.Value - now.Value : 0;
            if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
            _settings.LastVmixMark = $"Requesting publisher mark for event {kill.Id}.";
            var response = await _hotLinkClient.RequestReplayMarkAsync(kill.Id, token).ConfigureAwait(false);
            _settings.LastVmixMark = response.Scheduled
                ? $"Publisher scheduled delayed mark for event {kill.Id}."
                : string.Equals(response.Reason, "disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Publisher replay marking is disabled."
                    : $"Publisher did not schedule delayed mark for event {kill.Id}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _settings.LastVmixMark = $"Delayed mark failed: {ex.Message}"; }
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        lock (_sync)
        {
            _aliveSlots.Clear();
            foreach (var player in state.Players)
                if (player.IsAlive && player.Slot is >= 0 and <= 9) _aliveSlots.Add(player.Slot);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _settings.PropertyChanged -= OnSettingsChanged;
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _hotLinkClient.EventsReceived -= OnEventsReceived;
        _hotLinkClient.SessionChanged -= OnReset;
        _clock.TimeReset -= OnReset;
    }
}
