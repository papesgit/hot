using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.IO;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.WebSocket;

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
    private bool _isShooting;
    private DateTime _shootingExpiryTime;

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

    public bool IsShooting
    {
        get => _isShooting;
        set => SetProperty(ref _isShooting, value);
    }

    public DateTime ShootingExpiryTime
    {
        get => _shootingExpiryTime;
        set => SetProperty(ref _shootingExpiryTime, value);
    }

    public void SetMarkerScale(double scale)
    {
        MarkerScale = scale;
    }

    public void TriggerShootingFlash(int durationMs = 100)
    {
        IsShooting = true;
        ShootingExpiryTime = DateTime.UtcNow.AddMilliseconds(durationMs);
    }

    public void UpdateShootingState()
    {
        if (IsShooting && DateTime.UtcNow >= ShootingExpiryTime)
        {
            IsShooting = false;
        }
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

internal sealed class PlayerWeaponState
{
    public string ActiveWeaponName { get; set; } = string.Empty;
    public int LastAmmoClip { get; set; }
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

public sealed class RadarBombViewModel : ViewModelBase
{
    private double _canvasX;
    private double _canvasY;

    public RadarBombViewModel(string state, Vec3 position)
    {
        State = state;
        Position = position;
    }

    public string State { get; }
    public Vec3 Position { get; }

    public bool IsDropped => State == "dropped";
    public bool IsPlanted => State == "planted" || State == "defusing";
    public bool IsDefused => State == "defused";

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
    private readonly Dictionary<string, PlayerWeaponState> _playerWeaponStates = new();
    private readonly CampathsDockViewModel? _campathsVm;
    private readonly HlaeWebSocketClient? _webSocketClient;
    private readonly RadarSettings _settings;
    private CampathProfileViewModel? _attachedProfile;
    private DispatcherTimer? _flashCleanupTimer;

    private Bitmap? _radarImage;
    private string? _currentMap;
    private bool _hasRadar;
    private long _lastProcessedHeartbeat;

    private const double PositionThreshold = 1.0; // Units of movement to consider stationary
    private const int StationaryUpdatesRequired = 2; // Number of updates smoke must be stationary to be detonated

    public ObservableCollection<RadarPlayerViewModel> Players { get; } = new();
    public ObservableCollection<RadarGrenadeViewModel> Grenades { get; } = new();
    public ObservableCollection<FlameViewModel> Flames { get; } = new();
    public ObservableCollection<RadarBombViewModel> Bombs { get; } = new();
    public ObservableCollection<CampathPathViewModel> CampathPaths { get; } = new();

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

    public RadarDockViewModel(GsiServer gsiServer, RadarConfigProvider configProvider, RadarSettings settings, CampathsDockViewModel? campathsVm, HlaeWebSocketClient? webSocketClient)
    {
        _gsiServer = gsiServer;
        _configProvider = configProvider;
        _settings = settings;
        _campathsVm = campathsVm;
        _webSocketClient = webSocketClient;
        _projector = new RadarProjector(configProvider);

        Title = "Radar";
        CanClose = false;
        CanFloat = true;
        CanPin = true;

        _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _gsiServer.Start(); // fire and forget

        _settings.PropertyChanged += OnSettingsChanged;

        // Initialize flash cleanup timer
        _flashCleanupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50) // Check every 50ms
        };
        _flashCleanupTimer.Tick += OnFlashCleanupTick;
        _flashCleanupTimer.Start();

        if (_campathsVm != null)
        {
            _campathsVm.PropertyChanged += OnCampathsPropertyChanged;
            AttachProfile(_campathsVm.SelectedProfile);
        }
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        Dispatcher.UIThread.Post(() => ApplyState(state));
    }

    private void ApplyState(GsiGameState state)
    {
        if (string.IsNullOrWhiteSpace(state.MapName))
            return;

        bool mapChanged = false;

        if (!string.Equals(_currentMap, state.MapName, StringComparison.OrdinalIgnoreCase))
        {
            _currentMap = state.MapName;
            LoadRadarResources(state.MapName);
            mapChanged = true;
        }

        if (!_projector.TryProject(state.MapName, default, out _, out _, out _))
        {
            HasRadar = false;
            Players.Clear();
            CampathPaths.Clear();
            return;
        }

        var aliveColorT = new SolidColorBrush(Color.Parse("#FF9340"));
        var aliveColorCt = new SolidColorBrush(Color.Parse("#4DB3FF"));
        var deadColor = new SolidColorBrush(Color.Parse("#777777"));
        var border = new SolidColorBrush(Color.Parse("#0D1015"));
        var bombColor = new SolidColorBrush(Color.Parse("#FF5353"));
        Players.Clear();

        // Clean up weapon states for disconnected players
        var currentPlayerIds = new HashSet<string>(state.Players.Select(p => p.SteamId));
        var stateKeysToRemove = _playerWeaponStates.Keys
            .Where(id => !currentPlayerIds.Contains(id))
            .ToList();
        foreach (var key in stateKeysToRemove)
        {
            _playerWeaponStates.Remove(key);
        }

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

            // Track weapon state and detect shots
            if (!_playerWeaponStates.TryGetValue(p.SteamId, out var weaponState))
            {
                weaponState = new PlayerWeaponState();
                _playerWeaponStates[p.SteamId] = weaponState;
            }

            // Find active weapon
            var activeWeapon = p.Weapons.FirstOrDefault(w =>
                w.State.Equals("active", StringComparison.OrdinalIgnoreCase));

            if (activeWeapon != null)
            {
                // Check if this is the same weapon as before
                bool isSameWeapon = weaponState.ActiveWeaponName == activeWeapon.Name;

                if (isSameWeapon)
                {
                    // Detect shot: ammo decreased
                    if (activeWeapon.AmmoClip < weaponState.LastAmmoClip)
                    {
                        // Trigger shooting flash on player marker
                        vm.TriggerShootingFlash(100);
                    }
                }

                // Update state
                weaponState.ActiveWeaponName = activeWeapon.Name;
                weaponState.LastAmmoClip = activeWeapon.AmmoClip;
            }
            else
            {
                // No active weapon, reset state
                weaponState.ActiveWeaponName = string.Empty;
                weaponState.LastAmmoClip = 0;
            }
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

        // Process bomb
        Bombs.Clear();
        if (state.Bomb != null &&
            !string.IsNullOrEmpty(state.Bomb.State) &&
            (state.Bomb.State == "dropped" || state.Bomb.State == "planted" || state.Bomb.State == "defusing" || state.Bomb.State == "defused"))
        {
            if (_projector.TryProject(state.MapName, state.Bomb.Position, out var bombX, out var bombY, out _))
            {
                var bombVm = new RadarBombViewModel(state.Bomb.State, state.Bomb.Position)
                {
                    CanvasX = bombX * 1024.0 - 12.0, // center the 24px icon
                    CanvasY = bombY * 1024.0 - 12.0
                };
                Bombs.Add(bombVm);
            }
        }

        if (mapChanged)
        {
            RefreshCampathOverlay();
        }
    }

    private void OnFlashCleanupTick(object? sender, EventArgs e)
    {
        // Update shooting state for all players
        foreach (var player in Players)
        {
            player.UpdateShootingState();
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
        if (_campathsVm != null)
        {
            _campathsVm.PropertyChanged -= OnCampathsPropertyChanged;
            DetachProfile(_campathsVm.SelectedProfile);
        }
        _flashCleanupTimer?.Stop();
        _flashCleanupTimer = null;
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

    private void OnCampathsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathsDockViewModel.SelectedProfile))
        {
            DetachProfile(null);
            AttachProfile(_campathsVm?.SelectedProfile);
            RefreshCampathOverlay();
        }
    }

    private void OnCampathItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathItemViewModel.FilePath) || e.PropertyName == nameof(CampathItemViewModel.Name))
        {
            RefreshCampathOverlay();
        }
    }

    private void OnCampathCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<CampathItemViewModel>())
            {
                item.PropertyChanged -= OnCampathItemChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<CampathItemViewModel>())
            {
                item.PropertyChanged += OnCampathItemChanged;
            }
        }

        RefreshCampathOverlay();
    }

    private void AttachProfile(CampathProfileViewModel? profile)
    {
        if (profile == null)
            return;

        profile.Campaths.CollectionChanged += OnCampathCollectionChanged;
        foreach (var item in profile.Campaths)
        {
            item.PropertyChanged += OnCampathItemChanged;
        }

        _attachedProfile = profile;
    }

    private void DetachProfile(CampathProfileViewModel? profile)
    {
        var target = profile ?? _attachedProfile;
        if (target == null)
            return;

        target.Campaths.CollectionChanged -= OnCampathCollectionChanged;
        foreach (var item in target.Campaths)
        {
            item.PropertyChanged -= OnCampathItemChanged;
        }

        if (ReferenceEquals(_attachedProfile, target))
        {
            _attachedProfile = null;
        }
    }

    private void RefreshCampathOverlay()
    {
        CampathPaths.Clear();

        if (_campathsVm?.SelectedProfile == null || string.IsNullOrWhiteSpace(_currentMap) || !HasRadar)
            return;

        foreach (var campath in _campathsVm.SelectedProfile.Campaths)
        {
            if (string.IsNullOrWhiteSpace(campath.FilePath) || !File.Exists(campath.FilePath))
                continue;

            var parsed = CampathFileParser.Parse(campath.FilePath);
            if (parsed?.Points == null || parsed.Points.Count == 0)
                continue;

            var points = BuildCampathPolyline(parsed);
            if (points.Count == 0)
                continue;

            var forward = parsed.Points[0].Forward;
            var angle = NormalizeDegrees(Math.Atan2(forward.X, forward.Y) * 180.0 / Math.PI) - 90;
            var iconX = points[0].X - 12.0; // center 24px icon
            var iconY = points[0].Y - 12.0;

            CampathPaths.Add(new CampathPathViewModel(campath.Id, campath.Name, campath.FilePath, points, iconX, iconY, angle));
        }
    }

    public async void PlayCampath(CampathPathViewModel? path)
    {
        if (path == null || string.IsNullOrWhiteSpace(path.FilePath) || _webSocketClient == null)
            return;

        await _webSocketClient.SendCampathPlayAsync(path.FilePath);
    }

    public void SetCampathHighlight(CampathPathViewModel? target, bool isHighlighted)
    {
        if (isHighlighted)
        {
            foreach (var p in CampathPaths)
            {
                p.IsHighlighted = ReferenceEquals(p, target);
            }
        }
        else if (target != null)
        {
            target.IsHighlighted = false;
        }
    }

    private AvaloniaList<Point> BuildCampathPolyline(CampathFile parsed)
    {
        var result = new AvaloniaList<Point>();
        if (parsed.Points.Count == 0 || string.IsNullOrWhiteSpace(_currentMap))
            return result;

        bool useLinear = parsed.IsLinearPosition || parsed.Points.Count < 3;

        void AddProjected(Vec3 pos)
        {
            if (_projector.TryProject(_currentMap!, pos, out var px, out var py, out _))
            {
                var pt = new Point(px * 1024.0, py * 1024.0);
                if (result.Count == 0 || result[^1] != pt)
                {
                    result.Add(pt);
                }
            }
        }

        if (useLinear)
        {
            foreach (var p in parsed.Points)
            {
                AddProjected(p.Position);
            }
            return result;
        }

        int count = parsed.Points.Count;
        int stepsPerSegment = 16;

        for (int i = 0; i < count - 1; i++)
        {
            var p0 = parsed.Points[Math.Max(i - 1, 0)].Position;
            var p1 = parsed.Points[i].Position;
            var p2 = parsed.Points[i + 1].Position;
            var p3 = parsed.Points[Math.Min(i + 2, count - 1)].Position;

            for (int s = 0; s <= stepsPerSegment; s++)
            {
                double t = s / (double)stepsPerSegment;
                var pos = CatmullRom(p0, p1, p2, p3, t);
                AddProjected(pos);
            }
        }

        return result;
    }

    private static Vec3 CatmullRom(in Vec3 p0, in Vec3 p1, in Vec3 p2, in Vec3 p3, double t)
    {
        double t2 = t * t;
        double t3 = t2 * t;

        double x = 0.5 * ((2 * p1.X) + (-p0.X + p2.X) * t + (2 * p0.X - 5 * p1.X + 4 * p2.X - p3.X) * t2 + (-p0.X + 3 * p1.X - 3 * p2.X + p3.X) * t3);
        double y = 0.5 * ((2 * p1.Y) + (-p0.Y + p2.Y) * t + (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 + (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3);
        double z = 0.5 * ((2 * p1.Z) + (-p0.Z + p2.Z) * t + (2 * p0.Z - 5 * p1.Z + 4 * p2.Z - p3.Z) * t2 + (-p0.Z + 3 * p1.Z - 3 * p2.Z + p3.Z) * t3);

        return new Vec3(x, y, z);
    }
}
