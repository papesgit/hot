using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.ReplayDirector;

public sealed class ReplayDirectorFollower : IDisposable
{
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly GsiServer _gsiServer;
    private readonly ReplayDirectorSettings _settings;
    private readonly HttpClient _httpClient = new();
    private readonly object _sync = new();
    private readonly List<ReplayDirectorKillEvent> _pending = new();
    private readonly Dictionary<int, GsiPlayer> _alivePlayersBySlot = new();
    private CancellationTokenSource? _cts;
    private long _lastEventId;
    private double? _localGameTime;
    private DateTimeOffset _localGameTimeReceivedUtc;
    private int _focusedSlot = -1;
    private int _currentTargetSlot = -1;
    private double? _currentTargetKillGameTime;
    private DateTimeOffset? _currentTargetReleaseUtc;
    private DateTimeOffset _lastSwitchUtc = DateTimeOffset.MinValue;
    private bool _disposed;

    private const double LateSwitchGraceSeconds = 0.25;
    private const double GameTimeResetThresholdSeconds = 5.0;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public ReplayDirectorFollower(
        HlaeWebSocketClient webSocketClient,
        GsiServer gsiServer,
        ReplayDirectorSettings settings)
    {
        _webSocketClient = webSocketClient;
        _gsiServer = gsiServer;
        _settings = settings;
        _webSocketClient.MessageReceived += OnWebSocketMessage;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReplayDirectorSettings.Role) ||
                e.PropertyName == nameof(ReplayDirectorSettings.PublisherIp) ||
                e.PropertyName == nameof(ReplayDirectorSettings.PublisherPort))
            {
                ApplyRole();
            }
        };
        ApplyRole();
    }

    private void ApplyRole()
    {
        if (string.Equals(_settings.Role, "Delayed Follower", StringComparison.Ordinal))
            Start();
        else
            Stop();
    }

    private void Start()
    {
        lock (_sync)
        {
            if (_cts != null)
                return;
            _cts = new CancellationTokenSource();
        }

        _settings.Status = "Delayed follower starting.";
        _ = Task.Run(() => PollLoopAsync(_cts.Token));
        _ = Task.Run(() => CurtimeLoopAsync(_cts.Token));
        _ = Task.Run(() => SchedulerLoopAsync(_cts.Token));
    }

    private void Stop()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            _pending.Clear();
        }

        try { cts?.Cancel(); } catch { }
        if (!string.Equals(_settings.Role, "Delayed Follower", StringComparison.Ordinal))
            _settings.Status = "Replay director disabled.";
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var endpoint = BuildEventsUri();
                using var response = await _httpClient.GetAsync(endpoint, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<EventEnvelope>(json, JsonOptions);
                var received = envelope?.Events ?? Array.Empty<ReplayDirectorKillEvent>();
                if (received.Length > 0)
                {
                    lock (_sync)
                    {
                        foreach (var kill in received.OrderBy(e => e.Id))
                        {
                            _lastEventId = Math.Max(_lastEventId, kill.Id);
                            if (kill.AttackerSlot >= 0)
                                _pending.Add(kill);
                        }
                        _pending.RemoveAll(e => IsTooOld(e));
                    }

                    var last = received[^1];
                    _settings.LastKill = $"{last.AttackerName} at {(last.GameTime.HasValue ? last.GameTime.Value.ToString("F2", CultureInfo.InvariantCulture) : "wall-clock")} ({(last.MainCaught ? "main caught" : "uncaught")})";
                }

                _settings.Status = $"Follower connected. Last event {_lastEventId}.";
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _settings.Status = $"Follower polling error: {ex.Message}";
            }

            try { await Task.Delay(250, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private Uri BuildEventsUri()
    {
        var builder = CreatePublisherUriBuilder("/replay-director/events");
        builder.Query = $"after={_lastEventId}";
        return builder.Uri;
    }

    private async Task CurtimeLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _webSocketClient.SendCommandAsync("curtime_get").ConfigureAwait(false);
            }
            catch
            {
            }

            try { await Task.Delay(500, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            ReplayDirectorKillEvent? selected = null;
            lock (_sync)
            {
                _pending.RemoveAll(e => IsTooOld(e) || IsMissedSwitchWindow(e));
                selected = SelectBestSwitchCandidate();
                if (selected != null)
                    _pending.Remove(selected);
            }

            if (selected != null)
                await SwitchToKillAsync(selected, token).ConfigureAwait(false);

            try { await Task.Delay(50, token).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private ReplayDirectorKillEvent? SelectBestSwitchCandidate()
    {
        var nowGameTime = EstimateLocalGameTime();
        if (IsHoldingCurrentTarget(nowGameTime))
            return null;

        var dueCandidates = _pending
            .Where(IsCandidateValid)
            .Where(e => !_settings.OnlyFollowMissedKills || !e.MainCaught)
            .Where(e => !IsMissedSwitchWindow(e))
            .Where(IsDueForSwitch)
            .ToArray();
        if (dueCandidates.Length == 0)
            return null;

        var earliest = dueCandidates.Min(EventOrderTime);
        var contenderWindow = Math.Max(0.25, _settings.MergeWindowSeconds);
        return dueCandidates
            .Where(e => EventOrderTime(e) <= earliest + contenderWindow)
            .OrderByDescending(e => ScoreCandidate(e))
            .ThenBy(e => EventOrderTime(e))
            .ThenBy(e => e.Id)
            .FirstOrDefault();
    }

    private bool IsHoldingCurrentTarget(double? nowGameTime)
    {
        if (_currentTargetSlot < 0)
            return false;

        if (_currentTargetKillGameTime.HasValue && nowGameTime.HasValue)
        {
            return nowGameTime.Value < _currentTargetKillGameTime.Value + Math.Max(0, _settings.SwitchLockSeconds);
        }

        return _currentTargetReleaseUtc.HasValue && DateTimeOffset.UtcNow < _currentTargetReleaseUtc.Value;
    }

    private bool IsCandidateValid(ReplayDirectorKillEvent kill)
    {
        if (kill.AttackerSlot < 0 || kill.AttackerSlot > 9)
            return false;

        if (!_alivePlayersBySlot.ContainsKey(kill.AttackerSlot))
            return false;

        return true;
    }

    private int ScoreCandidate(ReplayDirectorKillEvent kill)
    {
        var score = kill.MainCaught ? 0 : 1000;
        if (kill.AttackerSlot == _currentTargetSlot)
            score += 200;
        if (kill.Headshot) score += 25;
        if (kill.Wallbang) score += 50;
        if (kill.Noscope) score += 50;
        if (kill.ThroughSmoke) score += 50;
        if (kill.Blind) score += 25;
        if (kill.InAir) score += 25;
        if (kill.GameTime.HasValue)
        {
            foreach (var other in _pending)
            {
                if (ReferenceEquals(other, kill))
                    continue;
                if (other.AttackerSlot == kill.AttackerSlot && other.GameTime.HasValue &&
                    Math.Abs(other.GameTime.Value - kill.GameTime.Value) <= _settings.MergeWindowSeconds)
                {
                    score += 100;
                }
            }
        }
        return score;
    }

    private bool IsDueForSwitch(ReplayDirectorKillEvent kill)
    {
        var nowGameTime = EstimateLocalGameTime();
        var dueGameTime = DueGameTime(kill, nowGameTime);
        if (dueGameTime.HasValue && nowGameTime.HasValue)
            return nowGameTime.Value >= dueGameTime.Value;

        return false;
    }

    private double? DueGameTime(ReplayDirectorKillEvent kill, double? nowGameTime)
    {
        if (!kill.GameTime.HasValue || !nowGameTime.HasValue)
            return null;
        return kill.GameTime.Value - _settings.PreSwitchSeconds;
    }

    private static double EventOrderTime(ReplayDirectorKillEvent kill)
    {
        return kill.GameTime ?? double.MaxValue;
    }

    private bool IsTooOld(ReplayDirectorKillEvent kill)
    {
        var nowGameTime = EstimateLocalGameTime();
        if (kill.GameTime.HasValue && nowGameTime.HasValue)
            return nowGameTime.Value > kill.GameTime.Value + Math.Max(3.0, _settings.MergeWindowSeconds);

        return (DateTimeOffset.UtcNow - kill.ReceivedUtc).TotalSeconds > 10.0;
    }

    private bool IsMissedSwitchWindow(ReplayDirectorKillEvent kill)
    {
        var nowGameTime = EstimateLocalGameTime();
        if (!kill.GameTime.HasValue || !nowGameTime.HasValue)
            return false;

        return nowGameTime.Value > kill.GameTime.Value + LateSwitchGraceSeconds;
    }

    private async Task SwitchToKillAsync(ReplayDirectorKillEvent kill, CancellationToken token)
    {
        if (kill.AttackerSlot < 0)
            return;

        _currentTargetSlot = kill.AttackerSlot;
        _currentTargetKillGameTime = kill.GameTime;
        _currentTargetReleaseUtc = kill.GameTime.HasValue
            ? null
            : DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(_settings.PreSwitchSeconds, _settings.SwitchLockSeconds));
        _lastSwitchUtc = DateTimeOffset.UtcNow;
        _settings.ScheduledTarget = $"Switched to {kill.AttackerName} for kill at {(kill.GameTime.HasValue ? kill.GameTime.Value.ToString("F2", CultureInfo.InvariantCulture) : "wall-clock")}.";

        await _webSocketClient.SendCommandAsync("spectate_slot", new { observer_slot = kill.AttackerSlot }).ConfigureAwait(false);
        _settings.LastSwitch = $"spectate_slot {kill.AttackerSlot} ({kill.AttackerName})";
        _ = ScheduleReplayMarkForKillAsync(kill, token);
    }

    private async Task ScheduleReplayMarkForKillAsync(ReplayDirectorKillEvent kill, CancellationToken token)
    {
        try
        {
            var delaySeconds = _settings.PreSwitchSeconds;
            var nowGameTime = EstimateLocalGameTime();
            if (kill.GameTime.HasValue && nowGameTime.HasValue)
            {
                delaySeconds = kill.GameTime.Value - nowGameTime.Value;
            }

            if (delaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token).ConfigureAwait(false);
            }

            _settings.LastVmixMark = $"Requesting main HOT mark for {kill.AttackerName}.";
            await SendRemoteReplayMarkAsync(kill, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _settings.LastVmixMark = $"Delayed mark failed: {ex.Message}";
        }
    }

    private async Task SendRemoteReplayMarkAsync(ReplayDirectorKillEvent kill, CancellationToken token)
    {
        var uri = BuildReplayMarkUri();
        var json = JsonSerializer.Serialize(new ReplayDirectorReplayMarkRequest { Kill = kill }, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(uri, content, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        _settings.LastVmixMark = $"Main HOT accepted delayed mark: {kill.AttackerName}.";
    }

    private Uri BuildReplayMarkUri()
    {
        return CreatePublisherUriBuilder("/replay-director/replay/mark").Uri;
    }

    private UriBuilder CreatePublisherUriBuilder(string path)
    {
        var host = _settings.PublisherIp;
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";

        // Accept a pasted URL too, while the UI only requires the publisher IP/hostname.
        if (Uri.TryCreate(host, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            host = uri.Host;

        return new UriBuilder(Uri.UriSchemeHttp, host, _settings.PublisherPort, path);
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        lock (_sync)
        {
            _alivePlayersBySlot.Clear();
            foreach (var player in state.Players)
            {
                if (player.IsAlive && player.Slot >= 0 && player.Slot <= 9)
                    _alivePlayersBySlot[player.Slot] = player;

                if (!string.IsNullOrWhiteSpace(state.FocusedPlayerSteamId) &&
                    string.Equals(player.SteamId, state.FocusedPlayerSteamId, StringComparison.Ordinal))
                {
                    _focusedSlot = player.Slot;
                }
            }
        }
    }

    private void OnWebSocketMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) ||
                !string.Equals(typeProp.GetString(), "curtime", StringComparison.Ordinal))
            {
                return;
            }

            if (root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
                return;

            if (root.TryGetProperty("value", out var valueProp) && valueProp.TryGetDouble(out var value))
            {
                lock (_sync)
                {
                    if (_localGameTime.HasValue && value + GameTimeResetThresholdSeconds < _localGameTime.Value)
                    {
                        _pending.Clear();
                        _currentTargetSlot = -1;
                        _currentTargetKillGameTime = null;
                        _currentTargetReleaseUtc = null;
                        _settings.ScheduledTarget = "Cleared replay director state after game time reset.";
                    }

                    _localGameTime = value;
                    _localGameTimeReceivedUtc = DateTimeOffset.UtcNow;
                }
                _settings.LocalGameTime = value.ToString("F2", CultureInfo.InvariantCulture);
            }
        }
        catch
        {
        }
    }

    private double? EstimateLocalGameTime()
    {
        lock (_sync)
        {
            if (!_localGameTime.HasValue)
                return null;

            var elapsed = (DateTimeOffset.UtcNow - _localGameTimeReceivedUtc).TotalSeconds;
            return _localGameTime.Value + Math.Max(0, elapsed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Stop();
        _webSocketClient.MessageReceived -= OnWebSocketMessage;
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _httpClient.Dispose();
    }

    private sealed class EventEnvelope
    {
        public ReplayDirectorKillEvent[] Events { get; init; } = Array.Empty<ReplayDirectorKillEvent>();
    }
}
