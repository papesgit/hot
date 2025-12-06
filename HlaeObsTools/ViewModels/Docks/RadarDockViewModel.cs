using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class RadarPlayerViewModel : ViewModelBase
{
    private double _relativeX;
    private double _relativeY;
    private double _rotation;
    private bool _isAlive;
    private bool _hasBomb;
    private bool _isFocused;
    private string _level = "default";
    private double _canvasX;
    private double _canvasY;
    private double _markerScale = 1.0;

    public RadarPlayerViewModel(string id, string name, string team, int slot, IBrush fill, IBrush border)
    {
        Id = id;
        Name = name;
        Team = team;
        Slot = slot;
        Fill = fill;
        Border = border;
    }

    public string Id { get; }
    public string Name { get; }
    public string Team { get; }
    public int Slot { get; }
    public IBrush Fill { get; }
    public IBrush Border { get; }

    /// <summary>
    /// Gets the display number for the hotkey binding.
    /// Slot 0 -> "1", Slot 1 -> "2", ..., Slot 9 -> "0", Slot -1 -> "" (no slot)
    /// </summary>
    public string DisplayNumber => Slot >= 0 && Slot <= 9 ? ((Slot + 1) % 10).ToString() : string.Empty;

    /// <summary>
    /// Gets the actual border color - white when focused, default border otherwise
    /// </summary>
    public IBrush ActualBorder => IsFocused ? Brushes.White : Border;

    public double RelativeX
    {
        get => _relativeX;
        set => SetProperty(ref _relativeX, value);
    }

    public double RelativeY
    {
        get => _relativeY;
        set => SetProperty(ref _relativeY, value);
    }

    public double Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value);
    }

    public bool IsAlive
    {
        get => _isAlive;
        set => SetProperty(ref _isAlive, value);
    }

    public bool HasBomb
    {
        get => _hasBomb;
        set => SetProperty(ref _hasBomb, value);
    }

    public bool IsFocused
    {
        get => _isFocused;
        set
        {
            if (SetProperty(ref _isFocused, value))
            {
                OnPropertyChanged(nameof(ActualBorder));
            }
        }
    }

    public string Level
    {
        get => _level;
        set => SetProperty(ref _level, value);
    }

    public double CanvasX
    {
        get => _canvasX;
        set
        {
            if (SetProperty(ref _canvasX, value))
            {
                OnPropertyChanged(nameof(ScaledCanvasX));
            }
        }
    }

    public double CanvasY
    {
        get => _canvasY;
        set
        {
            if (SetProperty(ref _canvasY, value))
            {
                OnPropertyChanged(nameof(ScaledCanvasY));
            }
        }
    }

    public double MarkerScale
    {
        get => _markerScale;
        private set
        {
            if (SetProperty(ref _markerScale, value))
            {
                OnPropertyChanged(nameof(ScaledCanvasX));
                OnPropertyChanged(nameof(ScaledCanvasY));
            }
        }
    }

    public double ScaledCanvasX => CanvasX - 18.0 * (MarkerScale - 1.0);
    public double ScaledCanvasY => CanvasY - 22.0 * (MarkerScale - 1.0);

    public void SetMarkerScale(double scale)
    {
        MarkerScale = scale;
    }
}

public sealed class FlameViewModel : ViewModelBase
{
    private double _canvasX;
    private double _canvasY;

    public double CanvasX
    {
        get => _canvasX;
        set => SetProperty(ref _canvasX, value);
    }

    public double CanvasY
    {
        get => _canvasY;
        set => SetProperty(ref _canvasY, value);
    }
}

internal sealed class SmokeTracker
{
    public Vec3 Position { get; set; }
    public Vec3 LastPosition { get; set; }
    public int StationaryUpdates { get; set; }
    public bool IsDetonated { get; set; }
}

public sealed class RadarGrenadeViewModel : ViewModelBase
{
    private double _canvasX;
    private double _canvasY;

    public RadarGrenadeViewModel(string id, string type, string iconPath, Vec3 position, bool isDetonated)
    {
        Id = id;
        Type = type;
        IconPath = iconPath;
        Position = position;
        IsDetonated = isDetonated;
    }

    public string Id { get; }
    public string Type { get; }
    public string IconPath { get; }
    public Vec3 Position { get; }
    public bool IsDetonated { get; }

    public bool IsSmoke => Type == "smoke" && IsDetonated;
    public bool IsInferno => Type == "inferno";

    public double CanvasX
    {
        get => _canvasX;
        set => SetProperty(ref _canvasX, value);
    }

    public double CanvasY
    {
        get => _canvasY;
        set => SetProperty(ref _canvasY, value);
    }
}

/// <summary>
/// Radar dock view model showing CS2 positions from GSI.
/// </summary>
public sealed class RadarDockViewModel : Tool, IDisposable
{
    private readonly GsiServer _gsiServer;
    private readonly RadarConfigProvider _configProvider;
    private readonly RadarProjector _projector;
    private readonly Dictionary<int, SmokeTracker> _smokeTrackers = new();
    private readonly RadarSettings _settings;

    private Bitmap? _radarImage;
    private string? _currentMap;
    private bool _hasRadar;
    private long _lastProcessedHeartbeat;

    private const double PositionThreshold = 1.0; // Units of movement to consider stationary
    private const int StationaryUpdatesRequired = 2; // Number of updates smoke must be stationary to be detonated

    public ObservableCollection<RadarPlayerViewModel> Players { get; } = new();
    public ObservableCollection<RadarGrenadeViewModel> Grenades { get; } = new();
    public ObservableCollection<FlameViewModel> Flames { get; } = new();

    public Bitmap? RadarImage
    {
        get => _radarImage;
        private set => SetProperty(ref _radarImage, value);
    }

    public bool HasRadar
    {
        get => _hasRadar;
        private set => SetProperty(ref _hasRadar, value);
    }

    public double MarkerScale => _settings.MarkerScale;

    public RadarDockViewModel(GsiServer gsiServer, RadarConfigProvider configProvider, RadarSettings settings)
    {
        _gsiServer = gsiServer;
        _configProvider = configProvider;
        _settings = settings;
        _projector = new RadarProjector(configProvider);

        Title = "Radar";
        CanClose = false;
        CanFloat = true;
        CanPin = true;

        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _gsiServer.Start(); // fire and forget

        _settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        Dispatcher.UIThread.Post(() => ApplyState(state));
    }

    private void ApplyState(GsiGameState state)
    {
        if (string.IsNullOrWhiteSpace(state.MapName))
            return;

        if (!string.Equals(_currentMap, state.MapName, StringComparison.OrdinalIgnoreCase))
        {
            _currentMap = state.MapName;
            LoadRadarResources(state.MapName);
        }

        if (!_projector.TryProject(state.MapName, default, out _, out _, out _))
        {
            HasRadar = false;
            Players.Clear();
            return;
        }

        var aliveColorT = new SolidColorBrush(Color.Parse("#FF9340"));
        var aliveColorCt = new SolidColorBrush(Color.Parse("#4DB3FF"));
        var deadColor = new SolidColorBrush(Color.Parse("#777777"));
        var border = new SolidColorBrush(Color.Parse("#0D1015"));
        var bombColor = new SolidColorBrush(Color.Parse("#FF5353"));
        Players.Clear();
        foreach (var p in state.Players)
        {
            if (!p.IsAlive)
                continue;

            if (!_projector.TryProject(state.MapName, p.Position, out var x, out var y, out var level))
                continue;

            var brush = p.Team.Equals("T", StringComparison.OrdinalIgnoreCase) ? aliveColorT : aliveColorCt;
            if (p.HasBomb) brush = bombColor;

            var vm = new RadarPlayerViewModel(p.SteamId, p.Name, p.Team, p.Slot, brush, border)
            {
                RelativeX = x,
                RelativeY = y,
                CanvasX = x * 1024.0 - 18.0, // center the 36px marker on the projected point
                CanvasY = y * 1024.0 - 22.0,
                Rotation = NormalizeDegrees(Math.Atan2(p.Forward.X, p.Forward.Y) * 180.0 / Math.PI),
                IsAlive = p.IsAlive,
                HasBomb = p.HasBomb,
                IsFocused = p.SteamId == state.FocusedPlayerSteamId,
                Level = level
            };
            vm.SetMarkerScale(_settings.MarkerScale);

            Players.Add(vm);
        }

        // Process grenades
        Grenades.Clear();
        Flames.Clear();

        // Update smoke trackers only on heartbeat change
        if (state.Heartbeat != _lastProcessedHeartbeat)
        {
            _lastProcessedHeartbeat = state.Heartbeat;

            // Track current smokes to clean up old ones
            var currentSmokeKeys = new HashSet<int>();

            // Update smoke trackers
            foreach (var g in state.Grenades)
            {
                if (g.Type == "smoke")
                {
                    int key = GetPositionHash(g.Position);
                    currentSmokeKeys.Add(key);

                    if (!_smokeTrackers.TryGetValue(key, out var tracker))
                    {
                        // New smoke - add to tracker
                        tracker = new SmokeTracker
                        {
                            Position = g.Position,
                            LastPosition = g.Position,
                            StationaryUpdates = 0,
                            IsDetonated = false
                        };
                        _smokeTrackers[key] = tracker;
                    }
                    else
                    {
                        // Update existing smoke on new GSI update
                        double distMoved = GetDistance(g.Position, tracker.Position);

                        if (!tracker.IsDetonated)
                        {
                            // Only track movement before detonation
                            if (distMoved < PositionThreshold)
                            {
                                tracker.StationaryUpdates++;
                                if (tracker.StationaryUpdates >= StationaryUpdatesRequired)
                                {
                                    tracker.IsDetonated = true;
                                }
                            }
                            else
                            {
                                tracker.StationaryUpdates = 0;
                            }
                        }

                        tracker.LastPosition = tracker.Position;
                        tracker.Position = g.Position;
                    }
                }
            }

            // Remove old smokes that are no longer in GSI data
            var keysToRemove = _smokeTrackers.Keys.Where(k => !currentSmokeKeys.Contains(k)).ToList();
            foreach (var key in keysToRemove)
            {
                _smokeTrackers.Remove(key);
            }
        }

        foreach (var g in state.Grenades)
        {
            if (!_projector.TryProject(state.MapName, g.Position, out var x, out var y, out var level))
                continue;

            // Determine if grenade is detonated
            bool isDetonated;
            if (g.Type == "smoke")
            {
                // Use tracker for smokes
                int key = GetPositionHash(g.Position);
                isDetonated = _smokeTrackers.TryGetValue(key, out var tracker) && tracker.IsDetonated;
            }
            else
            {
                // For other grenades, use velocity check
                isDetonated = g.Velocity.X == 0 && g.Velocity.Y == 0 && g.Velocity.Z == 0;
            }

            // Determine icon based on type
            string iconName = g.Type switch
            {
                "decoy" => "decoy",
                "firebomb" => "molotov", // Could be molotov or incgrenade, default to molotov
                "flashbang" => "flashbang",
                "frag" => "hegrenade",
                "smoke" => "smokegrenade",
                "inferno" => "inferno",
                _ => "hegrenade"
            };

            var iconPath = $"avares://HlaeObsTools/Assets/hud/weapons/{iconName}.svg";

            // For inferno (fire), project all flame positions to separate collection
            if (g.Type == "inferno" && g.Flames != null)
            {
                foreach (var flame in g.Flames)
                {
                    if (_projector.TryProject(state.MapName, flame, out var flameX, out var flameY, out _))
                    {
                        // Calculate absolute canvas position
                        var canvasX = flameX * 1024.0 - 8.0; // center the 16px flame circle
                        var canvasY = flameY * 1024.0 - 8.0;
                        Flames.Add(new FlameViewModel { CanvasX = canvasX, CanvasY = canvasY });
                    }
                }
            }

            // Only show projectiles that are not detonated, or infernos/smokes that are active
            if (!isDetonated || g.Type == "inferno" || (g.Type == "smoke" && g.EffectTime > 0))
            {
                var grenadeVm = new RadarGrenadeViewModel(g.Id, g.Type, iconPath, g.Position, isDetonated)
                {
                    CanvasX = x * 1024.0 - 12.0, // center the 24px icon
                    CanvasY = y * 1024.0 - 12.0
                };

                Grenades.Add(grenadeVm);
            }
        }
    }

    private static double NormalizeDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0) degrees += 360.0;
        return degrees;
    }

    private static int GetPositionHash(Vec3 pos)
    {
        // Create a hash key from position rounded to avoid floating point issues
        return ((int)(pos.X / 10.0) * 1000000) + ((int)(pos.Y / 10.0) * 1000) + (int)(pos.Z / 10.0);
    }

    private static double GetDistance(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private void LoadRadarResources(string mapName)
    {
        if (!_configProvider.TryGet(mapName, out var cfg))
        {
            HasRadar = false;
            RadarImage?.Dispose();
            RadarImage = null;
            return;
        }

        HasRadar = true;
        RadarImage?.Dispose();

        try
        {
            var relative = cfg.ImagePath ?? $"/hud/img/radars/ingame/{RadarConfigProvider.Sanitize(mapName)}.png";

            // Our assets live under Assets/hud/... (no extra /img segment). Normalize if needed.
            if (relative.StartsWith("/hud/img/", StringComparison.OrdinalIgnoreCase))
            {
                relative = "/hud/" + relative.Substring("/hud/img/".Length);
            }

            var trimmed = relative.TrimStart('/');
            var uri = new Uri($"avares://HlaeObsTools/Assets/{trimmed}");
            RadarImage = new Bitmap(AssetLoader.Open(uri));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load radar image for {mapName}: {ex.Message}");
            RadarImage = null;
        }
    }

    public void Dispose()
    {
        _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _settings.PropertyChanged -= OnSettingsChanged;
        RadarImage?.Dispose();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadarSettings.MarkerScale))
        {
            OnPropertyChanged(nameof(MarkerScale));
            foreach (var player in Players)
            {
                player.SetMarkerScale(_settings.MarkerScale);
            }
        }
    }
}
