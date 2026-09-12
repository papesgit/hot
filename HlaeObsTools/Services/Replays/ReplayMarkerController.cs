using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.HotLink;

namespace HlaeObsTools.Services.Replays;

public readonly record struct ReplayMarkerSettings(double PreSeconds, double PostSeconds, double ExtendWindowSeconds, ReplayBackendOptions BackendOptions);

/// <summary>Shared replay policy: groups kills, waits for post-roll, and creates or extends one clip.</summary>
public sealed class ReplayMarkerController : IDisposable
{
    private readonly IReplayBackend _backend;
    private readonly Func<ReplayMarkerSettings> _settings;
    private readonly object _sync = new();
    private readonly ReplayKillGroup _group = new();
    private ReplayEventHandle? _activeEvent;
    private DateTimeOffset? _appliedThrough;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public ReplayMarkerController(IReplayBackend backend, Func<ReplayMarkerSettings> settings) { _backend = backend; _settings = settings; }
    public event EventHandler<string>? StatusChanged;
    public bool IsEnabled => _backend.IsEnabled;

    public void RecordKill(int round, string playerName, int? roundKillNumber = null)
    {
        lock (_sync)
        {
            var settings = _settings();
            var newGroup = _group.Add(round, playerName, roundKillNumber, DateTimeOffset.UtcNow, settings.ExtendWindowSeconds);
            if (newGroup) { _pending?.Cancel(); _activeEvent = null; _appliedThrough = null; }
            ScheduleLocked(settings);
        }
    }

    public async Task<bool> RecordWindowAsync(IReadOnlyList<HotLinkPublisherKillEvent> kills, double seconds, CancellationToken token)
    {
        if (kills.Count == 0 || !_backend.IsAvailable) return false;
        var settings = _settings();
        var first = kills[0];
        var round = first.LabelRoundNumber > 0 ? first.LabelRoundNumber : first.RoundNumber;
        var numbers = kills.Select(k => k.RoundKillNumber).Where(n => n > 0).Order().ToArray();
        var range = numbers.Length <= 1 ? numbers.FirstOrDefault(1).ToString() : $"{numbers[0]}-{numbers[^1]}";
        var label = $"R{round}_{ReplayMarkerPrimitives.Sanitize(first.AttackerName)}_K{range}_Grenade";
        var created = await _backend.CreateAsync(new ReplayCreateRequest(Math.Max(1, (int)Math.Ceiling(seconds * 1000)), label, round, kills.Count, settings.BackendOptions), token).ConfigureAwait(false);
        StatusChanged?.Invoke(this, created.HasValue ? $"Delayed replay marked: {label}" : "Delayed replay marker failed.");
        return created.HasValue;
    }

    private void ScheduleLocked(ReplayMarkerSettings settings)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var due = (_group.LastKillUtc ?? DateTimeOffset.UtcNow).AddSeconds(Math.Max(0, settings.PostSeconds)) - DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(due > TimeSpan.Zero ? due : TimeSpan.Zero, cts.Token).ConfigureAwait(false); await ApplyAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Console.WriteLine($"[REPLAYS] Replay marker failed: {ex}"); StatusChanged?.Invoke(this, $"Replay marker failed: {ex.Message}"); }
        }, cts.Token);
    }

    private async Task ApplyAsync(CancellationToken token)
    {
        ReplayKillGroupSnapshot group; ReplayEventHandle? active; DateTimeOffset? applied;
        var settings = _settings();
        lock (_sync) { group = _group.Snapshot(); active = _activeEvent; applied = _appliedThrough; }
        if (group.FirstKillUtc == null || group.LastKillUtc == null) return;
        if (!_backend.IsAvailable)
        {
            DiscardFailedGroup(group);
            StatusChanged?.Invoke(this, "Replay marker failed: replay backend is unavailable.");
            return;
        }
        if (!active.HasValue)
        {
            var duration = settings.PreSeconds + settings.PostSeconds + (group.LastKillUtc.Value - group.FirstKillUtc.Value).TotalSeconds;
            var created = await _backend.CreateAsync(new ReplayCreateRequest(Math.Max(1, (int)Math.Ceiling(duration * 1000)), group.Label, group.Round, group.KillCount, settings.BackendOptions), token).ConfigureAwait(false);
            if (created.HasValue)
            {
                lock (_sync) { _activeEvent = created; _appliedThrough = group.LastKillUtc; }
                StatusChanged?.Invoke(this, $"Replay marked: {group.Label}");
            }
            else StatusChanged?.Invoke(this, "Replay marker failed.");
            if (!created.HasValue)
                DiscardFailedGroup(group);
            return;
        }
        var extensionMs = applied.HasValue ? Math.Max(0, (int)Math.Round((group.LastKillUtc.Value - applied.Value).TotalMilliseconds)) : 0;
        if (extensionMs <= 0) return;
        var updated = await _backend.ExtendAsync(active.Value, new ReplayExtendRequest(extensionMs, group.Label, group.KillCount, settings.BackendOptions), token).ConfigureAwait(false);
        if (updated)
        {
            lock (_sync) _appliedThrough = group.LastKillUtc;
            StatusChanged?.Invoke(this, $"Replay extended: {group.Label}");
        }
        else
        {
            DiscardFailedGroup(group);
            StatusChanged?.Invoke(this, "Replay extension failed.");
        }
    }

    private void DiscardFailedGroup(ReplayKillGroupSnapshot attempted)
    {
        lock (_sync)
        {
            // Do not erase a newer kill group that arrived while an older request was in flight.
            if (_group.FirstKillUtc != attempted.FirstKillUtc || _group.LastKillUtc != attempted.LastKillUtc)
                return;
            _group.Clear();
            _activeEvent = null;
            _appliedThrough = null;
        }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _pending?.Cancel(); }
}
