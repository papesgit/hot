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
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly GsiServer _gsiServer;
    private readonly HotLinkSettings _settings;
    private readonly ReplaySettings _vmixReplaySettings;
    private readonly HotLinkClient _hotLinkClient;
    private readonly HotLinkGameClock _clock;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _cameraGate = new(1, 1);
    private readonly List<HotLinkKillEvent> _pending = new();
    private readonly Dictionary<long, ReplayShotPlan> _coveredGrenades = new();
    private readonly HashSet<int> _aliveSlots = new();
    private CancellationTokenSource? _cts;
    private ReplayShotPlan? _active;
    private DateTimeOffset _shotStartedUtc;
    private bool _markRequested;
    private bool _nadeStarted;
    private bool _cleanupPending;
    private long _cleanupGeneration;
    private int _currentTargetSlot = -1;
    private bool _disposed;

    public ReplayDirectorClient(HlaeWebSocketClient webSocketClient, GsiServer gsiServer,
        HotLinkSettings settings, ReplaySettings vmixReplaySettings,
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
        _webSocketClient.Connected += OnConnected;
        _webSocketClient.Disconnected += OnReset;
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

    private void Start(bool seedRecent = true)
    {
        lock (_sync)
        {
            if (_cts != null) return;
            var cts = _cts = new CancellationTokenSource();
            if (seedRecent) _pending.AddRange(_hotLinkClient.GetRecentEvents().Where(k => !IsExpired(k)));
            _ = Task.Run(() => SchedulerLoopAsync(cts.Token));
        }
        _settings.ScheduledTarget = "Replay Director waiting for HOT Link events.";
    }

    private void Stop()
    {
        long cleanupGeneration;
        lock (_sync)
        {
            _cts?.Cancel();
            _cts = null;
            _pending.Clear();
            _coveredGrenades.Clear();
            _active = null;
            _currentTargetSlot = -1;
            _cleanupPending = true;
            cleanupGeneration = ++_cleanupGeneration;
        }
        _ = CleanupCameraAsync(cleanupGeneration);
    }

    private async Task CleanupCameraAsync(long cleanupGeneration)
    {
        await _cameraGate.WaitAsync().ConfigureAwait(false);
        try { await CleanupCameraCoreAsync(cleanupGeneration).ConfigureAwait(false); }
        catch (Exception ex) { _settings.LastSwitch = $"Grenade camera cleanup failed: {ex.Message}"; }
        finally { _cameraGate.Release(); }
    }

    private async Task CleanupCameraCoreAsync(long cleanupGeneration)
    {
        lock (_sync)
        {
            if (!_cleanupPending || cleanupGeneration != _cleanupGeneration) return;
            if (_nadeStarted && !_webSocketClient.IsConnected) return;
            _cleanupPending = false;
        }
        if (_nadeStarted)
        {
            await _webSocketClient.SendExecCommandAsync("mirv_nadecam stop").ConfigureAwait(false);
            _nadeStarted = false;
            _settings.LastSwitch = "mirv_nadecam stop (director reset or disconnect).";
        }
    }

    private void OnConnected(object? sender, EventArgs e)
    {
        long cleanupGeneration;
        lock (_sync) cleanupGeneration = _cleanupGeneration;
        _ = CleanupCameraAsync(cleanupGeneration);
    }

    private void OnEventsReceived(object? sender, HotLinkKillEvent[] events)
    {
        lock (_sync)
        {
            if (_cts == null) return;
            foreach (var kill in events)
                if (_pending.All(k => k.Id != kill.Id) && _active?.Kill.Id != kill.Id &&
                    (kill.GrenadeThrow == null || !_coveredGrenades.TryGetValue(kill.GrenadeThrow.Id, out var covered) || !covered.Covers(kill)))
                    _pending.Add(kill);
        }
    }

    private void OnReset(object? sender, EventArgs e)
    {
        Stop();
        if (!_disposed && _settings.IsReplayDirectorMode && _settings.ClientConnectionEnabled) Start(false);
        _settings.ScheduledTarget = "Cleared Replay Director state after connection, clock or session reset.";
    }

    private async Task SchedulerLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _cameraGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    long cleanupGeneration;
                    lock (_sync) cleanupGeneration = _cleanupGeneration;
                    await CleanupCameraCoreAsync(cleanupGeneration).ConfigureAwait(false);
                    await TickAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (Exception ex) { _settings.ScheduledTarget = $"Replay Director error: {ex.Message}"; }
                finally { _cameraGate.Release(); }
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task TickAsync(CancellationToken token)
    {
        var time = _clock.EstimateGameTime();
        if (!time.HasValue || !_webSocketClient.IsConnected) return;
        var now = time.Value;
        _settings.LocalGameTime = now.ToString("F2", CultureInfo.InvariantCulture);
        ReplayShotPlan? active;
        lock (_sync) active = _active;
        if (active != null)
        {
            var killTime = active.OutcomeGameTime;
            if (active.FollowGrenade && !_nadeStarted && now >= active.Kill.GrenadeThrow!.GameTime - 0.1 && now < killTime)
            {
                // Flush any older reset cleanup before the new camera owns the sequence, then
                // invalidate queued copies of that cleanup after the start command is sent.
                long cleanupGeneration;
                lock (_sync) cleanupGeneration = _cleanupGeneration;
                await CleanupCameraCoreAsync(cleanupGeneration).ConfigureAwait(false);
                await _webSocketClient.SendExecCommandAsync("mirv_nadecam start").ConfigureAwait(false);
                _nadeStarted = true;
                lock (_sync)
                {
                    if (_cleanupGeneration == cleanupGeneration && ReferenceEquals(_active, active))
                        _cleanupGeneration++;
                }
                _settings.LastSwitch = $"mirv_nadecam start at {now:F2}; throw {active.Kill.GrenadeThrow.GameTime:F2}, outcome {killTime:F2}.";
            }
            var markTime = active.ThrowBased ? killTime + active.ThrowPostRollSeconds : killTime;
            if (!_markRequested && now >= markTime)
            {
                _markRequested = true;
                if (_nadeStarted)
                {
                    await _webSocketClient.SendExecCommandAsync("mirv_nadecam stop").ConfigureAwait(false);
                    _nadeStarted = false;
                    _settings.LastSwitch = $"mirv_nadecam stop after outcome at {now:F2}.";
                }
                token.ThrowIfCancellationRequested();
                try
                {
                    // vMix records wall time, including pauses or playback-rate changes.
                    var length = active.ThrowBased ? (DateTimeOffset.UtcNow - _shotStartedUtc).TotalSeconds : (double?)null;
                    var response = await _hotLinkClient.RequestReplayMarkAsync(active.Kill.Id, token, length,
                        active.ThrowBased ? 0 : null, active.CoveredEventIds).ConfigureAwait(false);
                    _settings.LastVmixMark = response.Scheduled ? $"Publisher marked event {active.Kill.Id}." :
                        $"Publisher did not mark event {active.Kill.Id}: {response.Reason}.";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _settings.LastVmixMark = $"Delayed mark failed: {ex.Message}"; }
            }
            if (now < active.End) return;
            lock (_sync) if (ReferenceEquals(_active, active)) _active = null;
        }
        token.ThrowIfCancellationRequested();
        // A mark request may have taken time; never select using the pre-request clock sample.
        now = _clock.EstimateGameTime() ?? now;
        ReplayShotPlan? selected;
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            _pending.RemoveAll(IsExpired);
            foreach (var id in _coveredGrenades.Where(p => now - p.Value.OutcomeGameTime > 120).Select(p => p.Key).ToArray()) _coveredGrenades.Remove(id);
            selected = SelectCandidate(now);
            if (selected == null) return;
            _pending.Remove(selected.Kill);
            if (selected.Kill.GrenadeThrow is { } grenade)
            {
                _coveredGrenades[grenade.Id] = selected;
                _pending.RemoveAll(selected.Covers);
            }
            _active = selected;
            _markRequested = false;
            _currentTargetSlot = selected.TargetSlot;
        }
        token.ThrowIfCancellationRequested();
        await _webSocketClient.SendCommandAsync("spectate_slot", new { observer_slot = selected.TargetSlot }).ConfigureAwait(false);
        _shotStartedUtc = DateTimeOffset.UtcNow;
        _settings.LastSwitch = $"spectate_slot {selected.TargetSlot}";
        _settings.ScheduledTarget = $"Slot {selected.TargetSlot + 1}: {(selected.FollowGrenade ? "grenade follow" : selected.ThrowBased ? "thrower POV" : "kill POV")}" +
            (selected.FallbackReason == null ? "." : $" (victim fallback: {selected.FallbackReason}).");
    }

    private ReplayShotPlan? SelectCandidate(double now)
    {
        var due = _pending.Select(k => ReplayShotPlan.Create(k, _settings, now, _aliveSlots)?.IncludeRelatedOutcomes(_pending, _settings.SwitchLockSeconds))
            .Where(p => p != null && now >= p.Start).Select(p => p!).ToArray();
        if (due.Length == 0) return null;
        var earliest = due.Min(p => p.Kill.GameTime!.Value);
        return due.Where(p => p.Kill.GameTime!.Value <= earliest + Math.Max(0.25, _vmixReplaySettings.ExtendWindowSeconds))
            .OrderByDescending(Score).ThenBy(p => p.Kill.GameTime).ThenBy(p => p.Kill.Id).First();
    }

    private int Score(ReplayShotPlan plan)
    {
        var kill = plan.Kill;
        var grenade = GrenadeWeapons.IsGrenade(kill.Weapon);
        var score = grenade || !kill.MainCaught ? 1000 : 0;
        if (plan.TargetSlot == _currentTargetSlot) score += 10;
        score += Math.Max(0, kill.RoundKillNumber - 1) * 75;
        if (kill.Headshot) score += 5;
        
        var specialModifiers = 0;

        if (kill.Wallbang)      { score += 20; specialModifiers++; }
        if (kill.ThroughSmoke)  { score += 20; specialModifiers++; }
        if (kill.Blind)         { score += 30; specialModifiers++; }
        if (kill.Noscope)       { score += 100; specialModifiers++; }
        if (kill.InAir)         { score += 75; specialModifiers++; }

        score += Math.Max(0, specialModifiers - 1) * 25;

        score += _pending.Count(other => other != kill && other.GameTime.HasValue && kill.GameTime.HasValue &&
            (grenade ? kill.GrenadeThrow != null && other.GrenadeThrow?.Id == kill.GrenadeThrow.Id :
                !GrenadeWeapons.IsGrenade(other.Weapon) && other.Attacker.ObserverSlot == kill.Attacker.ObserverSlot) &&
            Math.Abs(other.GameTime.Value - kill.GameTime.Value) <= _vmixReplaySettings.ExtendWindowSeconds) * 50;
        return score;
    }

    private bool IsExpired(HotLinkKillEvent kill)
    {
        var now = _clock.EstimateGameTime();
        return !kill.GameTime.HasValue || (now.HasValue && now > kill.GameTime.Value + Math.Max(3, _vmixReplaySettings.ExtendWindowSeconds));
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
        _webSocketClient.Connected -= OnConnected;
        _webSocketClient.Disconnected -= OnReset;
    }
}
