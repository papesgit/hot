using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Cues;

namespace HlaeObsTools.Services.HotLink;

public sealed class DelayedObserverCueService : IDisposable
{
    private const double SpatialFadeSeconds = 2.0;
    private readonly HotLinkClient _client;
    private readonly HotLinkGameClock _clock;
    private readonly GsiServer _gsiServer;
    private readonly HotLinkSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _recentLeadTimes = new();
    private readonly Dictionary<int, string> _teamsBySlot = new();
    private double _autoUpcomingSeconds = 15;
    private double _autoTargetUpcomingSeconds = 15;
    private bool _disposed;

    public DelayedObserverCueService(HotLinkClient client, HotLinkGameClock clock, GsiServer gsiServer, HotLinkSettings settings)
    {
        _client = client;
        _clock = clock;
        _gsiServer = gsiServer;
        _settings = settings;
        _client.EventsReceived += OnEventsReceived;
        _client.SessionChanged += OnReset;
        _clock.TimeReset += OnReset;
        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _settings.PropertyChanged += OnSettingsChanged;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public ObservableCollection<CueEventViewModel> Events { get; } = new();
    public HotLinkSettings Settings => _settings;
    public double UpcomingSeconds => _settings.CueTimelineAutoRange ? _autoUpcomingSeconds : _settings.CueTimelineFixedUpcomingSeconds;
    public double HistorySeconds => UpcomingSeconds / 3.0;
    public bool IsCueModeActive => _settings.IsCueMode && _settings.ClientConnectionEnabled;
    public event EventHandler? Updated;

    private void OnEventsReceived(object? sender, HotLinkKillEvent[] events)
    {
        if (!_settings.IsCueMode) return;
        Dispatcher.UIThread.Post(() =>
        {
            var now = _clock.EstimateGameTime();
            if (!now.HasValue) return;
            foreach (var item in events)
            {
                if (!item.GameTime.HasValue || item.Attacker.ObserverSlot < 0 || item.Victim.ObserverSlot < 0) continue;
                var lead = item.GameTime.Value - now.Value;
                if (lead > 0)
                {
                    _recentLeadTimes.Add(lead);
                    if (_recentLeadTimes.Count > 20) _recentLeadTimes.RemoveAt(0);
                    var ordered = _recentLeadTimes.OrderBy(v => v).ToArray();
                    var target = ordered[(int)Math.Floor((ordered.Length - 1) * 0.75)];
                    _autoTargetUpcomingSeconds = Math.Max(1, target);
                    if (lead > _autoUpcomingSeconds) _autoUpcomingSeconds = lead;
                }
                Events.Add(new CueEventViewModel
                {
                    Id = item.Id,
                    GameTime = item.GameTime.Value,
                    InitialLeadSeconds = Math.Max(0.001, lead),
                    AttackerSlot = item.Attacker.ObserverSlot,
                    VictimSlot = item.Victim.ObserverSlot,
                    AttackerPosition = item.Attacker.Position,
                    VictimPosition = item.Victim.Position,
                    Weapon = item.Weapon
                });
            }
            UpdateEvents(now.Value);
        });
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        lock (_teamsBySlot)
        {
            _teamsBySlot.Clear();
            foreach (var player in state.Players)
                if (player.Slot >= 0) _teamsBySlot[player.Slot] = player.Team;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsCueModeActive) return;
        _autoUpcomingSeconds += (_autoTargetUpcomingSeconds - _autoUpcomingSeconds) * 0.025;
        var now = _clock.EstimateGameTime();
        if (now.HasValue) UpdateEvents(now.Value);
    }

    private void UpdateEvents(double now)
    {
        var upcoming = Math.Max(1, UpcomingSeconds);
        var history = upcoming / 3.0;
        for (var i = Events.Count - 1; i >= 0; i--)
        {
            var cue = Events[i];
            var remaining = cue.GameTime - now;
            if (remaining < -history) { Events.RemoveAt(i); continue; }
            cue.SecondsUntil = remaining;
            cue.RingProgress = remaining <= 0 ? 0 : Math.Clamp(remaining / cue.InitialLeadSeconds, 0, 1);
            cue.TimelinePosition = remaining >= 0
                ? 0.25 + 0.75 * Math.Clamp(remaining / upcoming, 0, 1)
                : 0.25 * Math.Clamp(1 + remaining / history, 0, 1);
            cue.IsTimelineVisible = remaining <= upcoming && remaining >= -history;
            cue.SpatialOpacity = remaining >= 0 ? 1 : Math.Clamp(1 + remaining / SpatialFadeSeconds, 0, 1);
            lock (_teamsBySlot)
            {
                cue.AttackerTeam = _teamsBySlot.GetValueOrDefault(cue.AttackerSlot, string.Empty);
                cue.VictimTeam = _teamsBySlot.GetValueOrDefault(cue.VictimSlot, string.Empty);
            }
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    private void OnReset(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() => { Events.Clear(); _recentLeadTimes.Clear(); _autoUpcomingSeconds = 15; _autoTargetUpcomingSeconds = 15; Updated?.Invoke(this, EventArgs.Empty); });
    }

    private void OnSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(HotLinkSettings.Role) or nameof(HotLinkSettings.ClientMode) or nameof(HotLinkSettings.ClientConnectionEnabled))) return;
        if (!_settings.IsCueMode) return;
        var existing = Events.Select(item => item.Id).ToHashSet();
        OnEventsReceived(_client, _client.GetRecentEvents().Where(item => !existing.Contains(item.Id)).ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _client.EventsReceived -= OnEventsReceived;
        _client.SessionChanged -= OnReset;
        _clock.TimeReset -= OnReset;
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _settings.PropertyChanged -= OnSettingsChanged;
    }
}
