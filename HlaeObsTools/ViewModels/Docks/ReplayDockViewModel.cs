using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Vmix;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class ReplayDockViewModel : Tool, IDisposable
{
    private readonly VmixReplayCoordinator _coordinator;
    private readonly List<ReplayEventRecord> _selectedEvents = new();
    private ReplayEventRecord? _selectedEvent;
    private string _status = "No replay event selected.";
    private bool _disposed;

    public ReplayDockViewModel(VmixReplayCoordinator coordinator)
    {
        _coordinator = coordinator;
        CanClose = true;
        CanFloat = true;
        CanPin = true;

        PlaySelectedCommand = new AsyncRelay(PlaySelectionAsync);
        PlayLastRoundCommand = new AsyncRelay(PlayLastRoundAsync);
        ClearCommand = new Relay(_ => ClearTrackedEvents());

        _coordinator.Registry.Changed += OnRegistryChanged;
        RefreshEvents();
    }

    public ObservableCollection<ReplayEventRecord> Events { get; } = new();

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
    public ICommand ClearCommand { get; }

    private void ClearTrackedEvents()
    {
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

    private static int GetReplayIdSortKey(ReplayEventRecord record)
    {
        return int.TryParse(record.VmixEventId, out var value) ? value : int.MaxValue;
    }

    private void OnRegistryChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshEvents, DispatcherPriority.Background);
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
