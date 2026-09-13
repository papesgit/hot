using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Vmix;
using HlaeObsTools.Services.ObsReplays;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class ReplayDockViewModel : Tool, IDisposable
{
    private const int PlaybackSpeedRequestIntervalMs = 50;
    private static readonly Regex RoundLabelPattern = new("^R(?<round>\\d+)_", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KillLabelPattern = new("_K(?<first>\\d+)(?:-(?<last>\\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly VmixReplayCoordinator _coordinator;
    private readonly ObsWebSocketClient _obsClient;
    private readonly ObsWebSocketSettings _obsSettings;
    private readonly ReplaySettings _replaySettings;
    private readonly List<ReplayEventRecord> _selectedEvents = new();
    private ReplayEventRecord? _selectedEvent;
    private string _status = "No replay event selected.";
    private double _playbackSpeed = 100;
    private bool _suppressSpeedDebounce;
    private readonly object _speedUpdateGate = new();
    private readonly object _speedAnimationGate = new();
    private CancellationTokenSource? _speedUpdateCts;
    private CancellationTokenSource? _presetSpeedAnimationCts;
    private bool _speedUpdateRunning;
    private int _pendingPlaybackSpeed;
    private long _playbackSpeedUpdateVersion;
    private DateTimeOffset _lastPlaybackSpeedRequestAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public ReplayDockViewModel(VmixReplayCoordinator coordinator, ObsWebSocketClient obsClient, ObsWebSocketSettings obsSettings, ReplaySettings replaySettings)
    {
        _coordinator = coordinator;
        _obsClient = obsClient;
        _obsSettings = obsSettings;
        _replaySettings = replaySettings;
        CanClose = true;
        CanFloat = true;
        CanPin = true;

        PlaySelectedCommand = new AsyncRelay(PlaySelectionAsync);
        PlayLastRoundCommand = new AsyncRelay(PlayLastRoundAsync);
        StopReplayCommand = new AsyncRelay(StopReplayAsync);
        RefreshCommand = new AsyncRelay(RefreshEventsAsync);
        ClearCommand = new AsyncRelay(ClearTrackedEventsAsync);
        Set100SpeedCommand = new AsyncRelay(() => SetPlaybackSpeedPresetAsync(100));
        Set75SpeedCommand = new AsyncRelay(() => SetPlaybackSpeedPresetAsync(75));
        Set50SpeedCommand = new AsyncRelay(() => SetPlaybackSpeedPresetAsync(50));

        _coordinator.Registry.Changed += OnRegistryChanged;
        _obsClient.ReplayStateChanged += OnObsReplayStateChanged;
        _obsClient.StateChanged += OnObsClientStateChanged;
        _replaySettings.PropertyChanged += OnObsSettingsChanged;
        RefreshEvents();
    }

    public ObservableCollection<ReplayEventRecord> Events { get; } = new();
    public string EventIdColumnHeader => _replaySettings.IsObsReplaysSelected ? "#" : "ID";
    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            var clamped = Math.Clamp(value, 10, 100);
            if (Math.Abs(_playbackSpeed - clamped) < 0.01) return;
            _playbackSpeed = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlaybackSpeedText));
            if (!_suppressSpeedDebounce)
            {
                CancelPresetSpeedAnimation();
                SchedulePlaybackSpeedApply();
            }
        }
    }
    public string PlaybackSpeedText => $"{Math.Round(PlaybackSpeed):0}%";

    public ReplayEventRecord? SelectedEvent
    {
        get => _selectedEvent;
        set
        {
            if (_selectedEvent == value)
                return;
            _selectedEvent = value;
            OnPropertyChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public ICommand PlaySelectedCommand { get; }
    public ICommand PlayLastRoundCommand { get; }
    public ICommand StopReplayCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand Set100SpeedCommand { get; }
    public ICommand Set75SpeedCommand { get; }
    public ICommand Set50SpeedCommand { get; }

    private void SchedulePlaybackSpeedApply()
    {
        CancellationTokenSource cts;
        lock (_speedUpdateGate)
        {
            _pendingPlaybackSpeed = (int)Math.Round(PlaybackSpeed);
            _playbackSpeedUpdateVersion++;
            if (_speedUpdateRunning)
                return;

            _speedUpdateRunning = true;
            cts = _speedUpdateCts = new CancellationTokenSource();
        }

        _ = Task.Run(() => ApplyQueuedPlaybackSpeedUpdatesAsync(cts), cts.Token);
    }

    private async Task ApplyQueuedPlaybackSpeedUpdatesAsync(CancellationTokenSource cts)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                int rate;
                long version;
                DateTimeOffset lastRequestAt;
                lock (_speedUpdateGate)
                {
                    rate = _pendingPlaybackSpeed;
                    version = _playbackSpeedUpdateVersion;
                    lastRequestAt = _lastPlaybackSpeedRequestAt;
                }

                var remaining = TimeSpan.FromMilliseconds(PlaybackSpeedRequestIntervalMs) - (DateTimeOffset.UtcNow - lastRequestAt);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, cts.Token).ConfigureAwait(false);

                // Throttle from when the request is sent, not when its response arrives.
                // Requests stay ordered, but OBS round-trip time does not add another rate-limit interval
                // of avoidable lag while the slider is moving quickly.
                lock (_speedUpdateGate)
                    _lastPlaybackSpeedRequestAt = DateTimeOffset.UtcNow;
                await ApplyPlaybackSpeedAsync(rate, smooth: false, cts.Token).ConfigureAwait(false);

                lock (_speedUpdateGate)
                {
                    if (version != _playbackSpeedUpdateVersion)
                        continue;

                    if (ReferenceEquals(_speedUpdateCts, cts))
                    {
                        _speedUpdateCts = null;
                        _speedUpdateRunning = false;
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        finally
        {
            lock (_speedUpdateGate)
            {
                if (ReferenceEquals(_speedUpdateCts, cts))
                {
                    _speedUpdateCts = null;
                    _speedUpdateRunning = false;
                }
            }
            cts.Dispose();
        }
    }

    private void CancelQueuedPlaybackSpeedUpdates()
    {
        CancellationTokenSource? cts;
        lock (_speedUpdateGate)
        {
            cts = _speedUpdateCts;
            _speedUpdateCts = null;
            _speedUpdateRunning = false;
        }
        cts?.Cancel();
    }

    private async Task SetPlaybackSpeedPresetAsync(int rate)
    {
        CancelQueuedPlaybackSpeedUpdates();
        CancelPresetSpeedAnimation();

        if (!_replaySettings.IsObsReplaysSelected)
        {
            if (!await ApplyPlaybackSpeedAsync(rate, smooth: false, CancellationToken.None).ConfigureAwait(false)) return;
            SetPlaybackSpeedSilently(rate);
            return;
        }

        if (!_obsClient.IsConnected) { Status = "OBS Replays is not connected."; return; }

        var animationCts = BeginPresetSpeedAnimation();
        try
        {
            var result = await _obsClient.SetPlayoutRateSmoothAsync(rate, _obsSettings.SmoothPlayoutRateDurationMs, animationCts.Token).ConfigureAwait(false);
            if (VendorRequestFailed(result)) { Status = "OBS Replays failed to set playback speed."; return; }

            var startingRate = GetRatePercent(result, "startingRatePercent") ?? (int)Math.Round(PlaybackSpeed);
            var targetRate = GetRatePercent(result, "ratePercent") ?? rate;
            Status = $"Replay speed set to {targetRate}%";
            await AnimatePlaybackSpeedAsync(startingRate, targetRate, _obsSettings.SmoothPlayoutRateDurationMs, animationCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (animationCts.IsCancellationRequested) { }
        catch (Exception ex) { Status = $"Failed to set replay speed: {ex.Message}"; }
        finally
        {
            EndPresetSpeedAnimation(animationCts);
        }
    }

    private CancellationTokenSource BeginPresetSpeedAnimation()
    {
        var cts = new CancellationTokenSource();
        lock (_speedAnimationGate)
            _presetSpeedAnimationCts = cts;
        return cts;
    }

    private void CancelPresetSpeedAnimation()
    {
        CancellationTokenSource? cts;
        lock (_speedAnimationGate)
        {
            cts = _presetSpeedAnimationCts;
            _presetSpeedAnimationCts = null;
        }
        cts?.Cancel();
    }

    private void EndPresetSpeedAnimation(CancellationTokenSource cts)
    {
        lock (_speedAnimationGate)
        {
            if (ReferenceEquals(_presetSpeedAnimationCts, cts))
                _presetSpeedAnimationCts = null;
        }
        cts.Dispose();
    }

    private async Task AnimatePlaybackSpeedAsync(int startingRate, int targetRate, int durationMs, CancellationToken token)
    {
        startingRate = Math.Clamp(startingRate, 10, 100);
        targetRate = Math.Clamp(targetRate, 10, 100);
        durationMs = Math.Clamp(durationMs, 0, 600000);
        if (durationMs == 0 || startingRate == targetRate)
        {
            await Dispatcher.UIThread.InvokeAsync(() => SetPlaybackSpeedSilently(targetRate));
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var progress = Math.Min(1.0, (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds / durationMs);
            var currentRate = startingRate + ((targetRate - startingRate) * progress);
            await Dispatcher.UIThread.InvokeAsync(() => SetPlaybackSpeedSilently(currentRate));
            if (progress >= 1.0)
                return;
            await Task.Delay(16, token).ConfigureAwait(false);
        }
    }

    private void SetPlaybackSpeedSilently(double rate)
    {
        _suppressSpeedDebounce = true;
        try { PlaybackSpeed = rate; }
        finally { _suppressSpeedDebounce = false; }
    }

    private async Task<bool> ApplyPlaybackSpeedAsync(int rate, bool smooth, CancellationToken token)
    {
        rate = Math.Clamp(rate, 10, 100);
        try
        {
            if (_replaySettings.IsObsReplaysSelected)
            {
                if (!_obsClient.IsConnected) { Status = "OBS Replays is not connected."; return false; }
                var result = smooth
                    ? await _obsClient.SetPlayoutRateSmoothAsync(rate, _obsSettings.SmoothPlayoutRateDurationMs, token).ConfigureAwait(false)
                    : await _obsClient.SetPlayoutRateAsync(rate, token).ConfigureAwait(false);
                if (VendorRequestFailed(result)) { Status = "OBS Replays failed to set playback speed."; return false; }
            }
            else if (_replaySettings.IsVmixSelected)
            {
                if (!await _coordinator.SetReplaySpeedAsync(rate, token).ConfigureAwait(false)) { Status = "vMix failed to set replay speed."; return false; }
            }
            else return false;
            Status = $"Replay speed set to {rate}%";
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Status = $"Failed to set replay speed: {ex.Message}"; return false; }
    }

    private async Task ClearTrackedEventsAsync()
    {
        if (_replaySettings.IsObsReplaysSelected)
        {
            await ClearObsEventsAsync();
            return;
        }
        _selectedEvents.Clear();
        SelectedEvent = null;
        _coordinator.ClearTrackedEvents();
        Status = "Cleared tracked replay events. Next HOT-created replay ID will be 0000.";
    }

    public void SetSelectedEvents(IEnumerable<ReplayEventRecord> selectedEvents)
    {
        _selectedEvents.Clear();
        _selectedEvents.AddRange(selectedEvents);
        Status = _selectedEvents.Count switch
        {
            0 => "No replay event selected.",
            1 => $"Selected {_selectedEvents[0].Label}.",
            _ => $"Selected {_selectedEvents.Count} replay events."
        };
    }

    private async Task PlaySelectionAsync()
    {
        var selected = _selectedEvents.Count > 0
            ? _selectedEvents.OrderBy(GetReplayIdSortKey).ToArray()
            : SelectedEvent != null ? new[] { SelectedEvent } : Array.Empty<ReplayEventRecord>();
        if (selected.Length == 0)
        {
            Status = "Select one or more replay events first.";
            return;
        }

        var missingId = selected.FirstOrDefault(e => string.IsNullOrWhiteSpace(e.VmixEventId));
        if (missingId != null)
        {
            Status = $"Replay '{missingId.Label}' has no tracked vMix ID yet.";
            return;
        }

        if (_replaySettings.IsObsReplaysSelected)
        {
            try
            {
                var result = await _obsClient.StartPlayoutAsync(selected.Select(e => e.VmixEventId!), CancellationToken.None);
                Status = result.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.False
                    ? "OBS Replays failed to start playout." : $"Playing {selected.Length} OBS replay event{(selected.Length == 1 ? string.Empty : "s")}.";
            }
            catch (Exception ex) { Status = $"OBS Replays: {ex.Message}"; }
            return;
        }
        var channels = selected
            .Select(e => string.IsNullOrWhiteSpace(e.Channel) ? "A" : e.Channel.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (channels.Length > 1)
        {
            Status = "Select replay events from one channel at a time.";
            return;
        }

        var ok = selected.Length == 1
            ? await _coordinator.PlayToOutputAsync(selected[0], CancellationToken.None)
            : await _coordinator.PlayToOutputAsync(selected, channels[0], CancellationToken.None);
        Status = ok
            ? $"Playing {selected.Length} replay event{(selected.Length == 1 ? string.Empty : "s")} on channel {channels[0]}."
            : "Failed to play selected replay events.";
    }

    private async Task PlayLastRoundAsync()
    {
        var latestRound = Events
            .Where(e => e.Round > 0)
            .Select(e => e.Round)
            .DefaultIfEmpty(0)
            .Max();
        if (latestRound <= 0)
        {
            Status = "No round replay events found.";
            return;
        }

        var roundEvents = Events
            .Where(e => e.Round == latestRound)
            .OrderBy(GetReplayIdSortKey)
            .ToArray();
        if (roundEvents.Length == 0)
        {
            Status = $"No replay events found for round {latestRound}.";
            return;
        }

        if (_replaySettings.IsObsReplaysSelected)
        {
            try
            {
                var result = await _obsClient.StartPlayoutAsync(roundEvents.Select(e => e.VmixEventId!), CancellationToken.None);
                Status = result.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.False
                    ? $"Failed to play round {latestRound}." : $"Playing {roundEvents.Length} replay event{(roundEvents.Length == 1 ? string.Empty : "s")} from round {latestRound}.";
            }
            catch (Exception ex) { Status = $"OBS Replays: {ex.Message}"; }
            return;
        }

        var channels = roundEvents
            .Select(e => string.IsNullOrWhiteSpace(e.Channel) ? "A" : e.Channel.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (channels.Length > 1)
        {
            Status = $"Round {latestRound} has replay events on multiple channels.";
            return;
        }

        var ok = await _coordinator.PlayToOutputAsync(roundEvents, channels[0], CancellationToken.None);
        Status = ok
            ? $"Playing {roundEvents.Length} replay event{(roundEvents.Length == 1 ? string.Empty : "s")} from round {latestRound}."
            : $"Failed to play round {latestRound}.";
    }

    private async Task StopReplayAsync()
    {
        if (_replaySettings.IsObsReplaysSelected)
        {
            try { await _obsClient.StopPlayoutAsync(CancellationToken.None); Status = "Stopping OBS replay playout."; }
            catch (Exception ex) { Status = $"OBS Replays: {ex.Message}"; }
            return;
        }

        var ok = await _coordinator.StopReplayAsync(CancellationToken.None);
        Status = ok
            ? "Stopped replay on channels A and B."
            : "Failed to stop replay on one or more channels.";
    }

    private static int GetReplayIdSortKey(ReplayEventRecord record)
    {
        if (record.EventIndex > 0)
            return record.EventIndex;
        return int.TryParse(record.VmixEventId, out var value) ? value : int.MaxValue;
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        if (_replaySettings.IsObsReplaysSelected)
            Dispatcher.UIThread.Post(() => _ = RefreshObsEventsAsync(), DispatcherPriority.Background);
        else
            Dispatcher.UIThread.Post(RefreshEvents, DispatcherPriority.Background);
    }

    private async Task RefreshEventsAsync()
    {
        if (_replaySettings.IsObsReplaysSelected)
            await RefreshObsEventsAsync();
        else
            RefreshEvents();
    }

    private void OnObsReplayStateChanged(object? sender, EventArgs e)
    {
        if (_replaySettings.IsObsReplaysSelected)
            _ = RefreshObsEventsAsync();
    }

    private void OnObsClientStateChanged(object? sender, EventArgs e)
    {
        if (_replaySettings.IsObsReplaysSelected && _obsClient.IsConnected)
            _ = RefreshObsEventsAsync();
    }

    private void OnObsSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReplaySettings.Backend))
        {
            OnPropertyChanged(nameof(EventIdColumnHeader));
            if (_replaySettings.IsObsReplaysSelected)
                _ = RefreshObsEventsAsync();
        }
    }

    private async Task RefreshObsEventsAsync()
    {
        if (!_obsClient.IsConnected) { Status = "OBS Replays is not connected."; return; }
        try
        {
            var response = await _obsClient.ListEventsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!response.TryGetProperty("events", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            var known = _coordinator.Registry.Snapshot()
                .Where(record => !string.IsNullOrWhiteSpace(record.VmixEventId))
                .GroupBy(record => record.VmixEventId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            var records = new List<ReplayEventRecord>(); long localId = 0;
            foreach (var item in items.EnumerateArray())
            {
                var label = item.TryGetProperty("label", out var labelValue) ? labelValue.GetString() ?? string.Empty : string.Empty;
                var eventId = item.GetProperty("id").GetString() ?? string.Empty;
                known.TryGetValue(eventId, out var attached);
                records.Add(new ReplayEventRecord { LocalId = ++localId, EventIndex = item.TryGetProperty("index", out var index) && index.TryGetInt32(out var value) ? value : 0, VmixEventId = eventId, Label = label, Round = attached?.Round ?? GetRoundFromLabel(label), KillCount = attached?.KillCount ?? GetKillCountFromLabel(label), Status = attached?.Status ?? "Recorded", Source = attached?.Source ?? "OBS Replays", CreatedUtc = item.TryGetProperty("createdAtUtc", out var created) && created.TryGetDateTimeOffset(out var time) ? time : DateTimeOffset.UtcNow });
            }
            await Dispatcher.UIThread.InvokeAsync(() => { Events.Clear(); foreach (var record in records) Events.Add(record); Status = $"Loaded {records.Count} OBS replay event{(records.Count == 1 ? string.Empty : "s")}."; });
        }
        catch (Exception ex) { Status = $"Failed to load OBS replay events: {ex.Message}"; }
    }

    private async Task ClearObsEventsAsync()
    {
        if (!_obsClient.IsConnected) { Status = "OBS Replays is not connected."; return; }
        _selectedEvents.Clear();
        SelectedEvent = null;
        try
        {
            var response = await _obsClient.ListEventsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!response.TryGetProperty("events", out var items) || items.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                Status = "Failed to load OBS replay events for deletion.";
                return;
            }
            var ids = items.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToArray();
            var deleted = new List<string>();
            foreach (var id in ids)
            {
                var result = await _obsClient.DeleteEventAsync(id, CancellationToken.None).ConfigureAwait(false);
                if (VendorRequestFailed(result))
                {
                    Status = $"Deleted {deleted.Count} of {ids.Length} OBS replay events; deletion failed.";
                    await RefreshObsEventsAsync().ConfigureAwait(false);
                    return;
                }
                deleted.Add(id);
            }
            _coordinator.Registry.RemoveByEventIds(deleted);
            await RefreshObsEventsAsync().ConfigureAwait(false);
            Status = ids.Length == 0 ? "No OBS replay events to delete." : $"Deleted {ids.Length} OBS replay event{(ids.Length == 1 ? string.Empty : "s")}.";
        }
        catch (Exception ex) { Status = $"Failed to delete OBS replay events: {ex.Message}"; }
    }

    private static bool VendorRequestFailed(System.Text.Json.JsonElement response)
    {
        while (response.TryGetProperty("responseData", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
            response = nested;
        return response.TryGetProperty("success", out var success) && success.ValueKind == System.Text.Json.JsonValueKind.False;
    }

    private static int? GetRatePercent(System.Text.Json.JsonElement response, string propertyName)
    {
        while (response.TryGetProperty("responseData", out var nested) && nested.ValueKind == System.Text.Json.JsonValueKind.Object)
            response = nested;
        return response.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var rate)
            ? rate
            : null;
    }

    private static int GetRoundFromLabel(string label)
    {
        var match = RoundLabelPattern.Match(label ?? string.Empty);
        return match.Success && int.TryParse(match.Groups["round"].Value, out var round) ? round : 0;
    }

    private static int GetKillCountFromLabel(string label)
    {
        var total = 0;
        foreach (Match match in KillLabelPattern.Matches(label ?? string.Empty))
        {
            if (!int.TryParse(match.Groups["first"].Value, out var first)) continue;
            var last = match.Groups["last"].Success && int.TryParse(match.Groups["last"].Value, out var parsed) ? parsed : first;
            total += Math.Max(1, last - first + 1);
        }
        return total;
    }

    private void RefreshEvents()
    {
        var selectedId = SelectedEvent?.LocalId;
        Events.Clear();
        foreach (var record in _coordinator.Registry.Snapshot())
            Events.Add(record);

        SelectedEvent = selectedId.HasValue ? FindRecord(selectedId.Value) : null;
    }

    private ReplayEventRecord? FindRecord(long localId)
    {
        foreach (var record in Events)
        {
            if (record.LocalId == localId)
                return record;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelQueuedPlaybackSpeedUpdates();
        CancelPresetSpeedAnimation();
        _coordinator.Registry.Changed -= OnRegistryChanged;
        _obsClient.ReplayStateChanged -= OnObsReplayStateChanged;
        _obsClient.StateChanged -= OnObsClientStateChanged;
        _replaySettings.PropertyChanged -= OnObsSettingsChanged;
    }

    private sealed class Relay : ICommand
    {
        private readonly Action<object?> _execute;

        public Relay(Action<object?> execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    private sealed class AsyncRelay : ICommand
    {
        private readonly Func<Task> _execute;
        private bool _running;

        public AsyncRelay(Func<Task> execute)
        {
            _execute = execute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !_running;

        public async void Execute(object? parameter)
        {
            if (_running)
                return;

            _running = true;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                await _execute();
            }
            finally
            {
                _running = false;
                CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
