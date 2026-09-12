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
    private static readonly Regex RoundLabelPattern = new("^R(?<round>\\d+)_", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex KillLabelPattern = new("_K(?<first>\\d+)(?:-(?<last>\\d+))?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly VmixReplayCoordinator _coordinator;
    private readonly ObsWebSocketClient _obsClient;
    private readonly ReplaySettings _replaySettings;
    private readonly List<ReplayEventRecord> _selectedEvents = new();
    private ReplayEventRecord? _selectedEvent;
    private string _status = "No replay event selected.";
    private bool _disposed;

    public ReplayDockViewModel(VmixReplayCoordinator coordinator, ObsWebSocketClient obsClient, ReplaySettings replaySettings)
    {
        _coordinator = coordinator;
        _obsClient = obsClient;
        _replaySettings = replaySettings;
        CanClose = true;
        CanFloat = true;
        CanPin = true;

        PlaySelectedCommand = new AsyncRelay(PlaySelectionAsync);
        PlayLastRoundCommand = new AsyncRelay(PlayLastRoundAsync);
        StopReplayCommand = new AsyncRelay(StopReplayAsync);
        RefreshCommand = new AsyncRelay(RefreshEventsAsync);
        ClearCommand = new Relay(_ => ClearTrackedEvents());

        _coordinator.Registry.Changed += OnRegistryChanged;
        _obsClient.ReplayStateChanged += OnObsReplayStateChanged;
        _obsClient.StateChanged += OnObsClientStateChanged;
        _replaySettings.PropertyChanged += OnObsSettingsChanged;
        RefreshEvents();
    }

    public ObservableCollection<ReplayEventRecord> Events { get; } = new();
    public string EventIdColumnHeader => _replaySettings.IsObsReplaysSelected ? "#" : "ID";

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

    private void ClearTrackedEvents()
    {
        if (_replaySettings.IsObsReplaysSelected)
        {
            _ = RefreshObsEventsAsync();
            Status = "OBS Replays owns this event library; refreshed its authoritative list.";
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
