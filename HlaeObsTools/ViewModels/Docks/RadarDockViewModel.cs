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
    private const double MarkerWidth = 36.0;
    private bool _hasInterpolationSample;
    private double _previousCanvasX;
    private double _previousCanvasY;
    private double _currentCanvasX;
    private double _currentCanvasY;
    private double _previousRotation;
    private double _currentRotation;
    private DateTime _previousSampleTimeUtc;
    private DateTime _currentSampleTimeUtc;
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
    private double _baseScale = 1.0;
    private double _heightScale = 1.0;
    private bool _isShooting;
    private bool _isSniperEquipped;
    private DateTime _shootingExpiryTime;
    private bool _useAltBindings;
    private string? _activeGrenadeIconPath;
    private IBrush _fill;
    private IBrush _border;
    private int _slot;
    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public RadarPlayerViewModel(string id, string name, string team, int slot, IBrush fill, IBrush border)
    {
        Id = id;
        Name = name;
        Team = team;
        _slot = slot;
        _fill = fill;
        _border = border;
    }

    public string Id { get; }
    public string Name { get; }
    public string Team { get; }
    public int Slot
    {
        get => _slot;
        set
        {
            if (SetProperty(ref _slot, value))
            {
                OnPropertyChanged(nameof(DisplayNumber));
            }
        }
    }
    public IBrush Fill
    {
        get => _fill;
        set => SetProperty(ref _fill, value);
    }

    public IBrush Border
    {
        get => _border;
        set => SetProperty(ref _border, value);
    }
    public double Altitude { get; set; }

    /// <summary>
    /// Gets the display number for the hotkey binding.
    /// Slot 0 -> "1", Slot 1 -> "2", ..., Slot 9 -> "0", Slot -1 -> "" (no slot)
    /// </summary>
    public string DisplayNumber
    {
        get
        {
            if (Slot < 0 || Slot > 9)
            {
                return string.Empty;
            }

            if (UseAltBindings && Slot >= 5)
            {
                return AltBindLabels[Slot - 5];
            }

            return ((Slot + 1) % 10).ToString();
        }
    }

    public bool UseAltBindings
    {
        get => _useAltBindings;
        set
        {
            if (SetProperty(ref _useAltBindings, value))
            {
                OnPropertyChanged(nameof(DisplayNumber));
            }
        }
    }

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

    public bool IsSniperEquipped
    {
        get => _isSniperEquipped;
        set => SetProperty(ref _isSniperEquipped, value);
    }

    public DateTime ShootingExpiryTime
    {
        get => _shootingExpiryTime;
        set => SetProperty(ref _shootingExpiryTime, value);
    }

    public string? ActiveGrenadeIconPath
    {
        get => _activeGrenadeIconPath;
        set
        {
            if (SetProperty(ref _activeGrenadeIconPath, value))
            {
                OnPropertyChanged(nameof(HasActiveGrenadeIcon));
            }
        }
    }

    public bool HasActiveGrenadeIcon => !string.IsNullOrWhiteSpace(ActiveGrenadeIconPath);

    public void SetBaseScale(double scale)
    {
        if (Math.Abs(_baseScale - scale) < 0.0001)
            return;

        _baseScale = scale;
        UpdateMarkerScale();
    }

    public void SetHeightScale(double scale)
    {
        if (Math.Abs(_heightScale - scale) < 0.0001)
            return;

        _heightScale = scale;
        UpdateMarkerScale();
    }

    public void SetMarkerScale(double scale)
    {
        SetBaseScale(scale);
    }

    public void TriggerShootingFlash(int durationMs = 100)
    {
        IsShooting = true;
        ShootingExpiryTime = DateTime.UtcNow.AddMilliseconds(durationMs);
    }

    public void PushPositionSample(double canvasX, double canvasY, double rotation, DateTime sampleTimeUtc, bool snap)
    {
        if (snap || !_hasInterpolationSample)
        {
            _hasInterpolationSample = true;
            _previousCanvasX = canvasX;
            _previousCanvasY = canvasY;
            _currentCanvasX = canvasX;
            _currentCanvasY = canvasY;
            _previousRotation = rotation;
            _currentRotation = rotation;
            _previousSampleTimeUtc = sampleTimeUtc;
            _currentSampleTimeUtc = sampleTimeUtc;
            CanvasX = canvasX;
            CanvasY = canvasY;
            Rotation = rotation;
            return;
        }

        _previousCanvasX = _currentCanvasX;
        _previousCanvasY = _currentCanvasY;
        _currentCanvasX = canvasX;
        _currentCanvasY = canvasY;
        _previousRotation = _currentRotation;
        _currentRotation = rotation;
        _previousSampleTimeUtc = _currentSampleTimeUtc;
        _currentSampleTimeUtc = sampleTimeUtc;
    }

    public void AdvanceInterpolation(DateTime renderTimeUtc, double interpolationDelaySeconds)
    {
        if (!_hasInterpolationSample)
            return;

        if (_currentSampleTimeUtc <= _previousSampleTimeUtc)
        {
            CanvasX = _currentCanvasX;
            CanvasY = _currentCanvasY;
            Rotation = _currentRotation;
            return;
        }

        var delayedTimeUtc = renderTimeUtc - TimeSpan.FromSeconds(interpolationDelaySeconds);
        var totalSeconds = (_currentSampleTimeUtc - _previousSampleTimeUtc).TotalSeconds;
        var elapsedSeconds = (delayedTimeUtc - _previousSampleTimeUtc).TotalSeconds;
        var t = Math.Clamp(elapsedSeconds / totalSeconds, 0.0, 1.0);

        CanvasX = Lerp(_previousCanvasX, _currentCanvasX, t);
        CanvasY = Lerp(_previousCanvasY, _currentCanvasY, t);
        Rotation = LerpAngleDegrees(_previousRotation, _currentRotation, t);
    }

    public void UpdateShootingState()
    {
        if (IsShooting && DateTime.UtcNow >= ShootingExpiryTime)
        {
            IsShooting = false;
        }
    }

    private void UpdateMarkerScale()
    {
        MarkerScale = _baseScale * _heightScale;
    }

    private static double Lerp(double from, double to, double t)
    {
        return from + (to - from) * t;
    }

    private static double LerpAngleDegrees(double from, double to, double t)
    {
        var delta = ((to - from + 540.0) % 360.0) - 180.0;
        return NormalizeDegrees(from + delta * t);
    }

    private static double NormalizeDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0)
        {
            degrees += 360.0;
        }

        return degrees;
    }
}

public sealed class RadarDeadPlayerViewModel : ViewModelBase
{
    private double _canvasX;
    private double _canvasY;
    private double _markerScale = 1.0;
    private double _baseScale = 1.0;
    private double _heightScale = 1.0;

    public RadarDeadPlayerViewModel(string id, string team, IBrush stroke)
    {
        Id = id;
        Team = team;
        Stroke = stroke;
    }

    public string Id { get; }
    public string Team { get; }
    public IBrush Stroke { get; }
    public double Altitude { get; set; }

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

    public double ScaledCanvasX => CanvasX - 6.0 * (MarkerScale - 1.0);
    public double ScaledCanvasY => CanvasY - 6.0 * (MarkerScale - 1.0);

    public void SetBaseScale(double scale)
    {
        if (Math.Abs(_baseScale - scale) < 0.0001)
            return;

        _baseScale = scale;
        UpdateMarkerScale();
    }

    public void SetHeightScale(double scale)
    {
        if (Math.Abs(_heightScale - scale) < 0.0001)
            return;

        _heightScale = scale;
        UpdateMarkerScale();
    }

    public void SetMarkerScale(double scale)
    {
        SetBaseScale(scale);
    }

    private void UpdateMarkerScale()
    {
        MarkerScale = _baseScale * _heightScale;
    }
}

public sealed class RadarDroppedDefuserViewModel : ViewModelBase
{
    private double _canvasX;
    private double _canvasY;
    private double _markerScale = 1.0;
    private double _baseScale = 1.0;
    private double _heightScale = 1.0;

    public RadarDroppedDefuserViewModel(string id)
    {
        Id = id;
    }

    public string Id { get; }
    public double Altitude { get; set; }

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

    public double ScaledCanvasX => CanvasX - 8.0 * (MarkerScale - 1.0);
    public double ScaledCanvasY => CanvasY - 8.0 * (MarkerScale - 1.0);

    public void SetBaseScale(double scale)
    {
        if (Math.Abs(_baseScale - scale) < 0.0001)
            return;

        _baseScale = scale;
        UpdateMarkerScale();
    }

    public void SetHeightScale(double scale)
    {
        if (Math.Abs(_heightScale - scale) < 0.0001)
            return;

        _heightScale = scale;
        UpdateMarkerScale();
    }

    private void UpdateMarkerScale()
    {
        MarkerScale = _baseScale * _heightScale;
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
    public bool IsDetonated { get; set; }
    public DateTime? DetonatedAtUtc { get; set; }
    public bool HasDetonatedOnce { get; set; }
}

internal sealed class PlayerWeaponState
{
    public string ActiveWeaponName { get; set; } = string.Empty;
    public int LastAmmoClip { get; set; }
}

public sealed class RadarGrenadeViewModel : ViewModelBase
{
    private bool _hasInterpolationSample;
    private double _previousCanvasX;
    private double _previousCanvasY;
    private double _currentCanvasX;
    private double _currentCanvasY;
    private DateTime _previousSampleTimeUtc;
    private DateTime _currentSampleTimeUtc;
    private string _type;
    private string _iconPath;
    private Vec3 _position;
    private bool _isDetonated;
    private double _smokeProgress;
    private double _canvasX;
    private double _canvasY;

    public RadarGrenadeViewModel(string id, string type, string iconPath, Vec3 position, bool isDetonated, double smokeProgress = 0)
    {
        Id = id;
        _type = type;
        _iconPath = iconPath;
        _position = position;
        _isDetonated = isDetonated;
        _smokeProgress = smokeProgress;
    }

    public string Id { get; }
    public string Type
    {
        get => _type;
        private set
        {
            if (SetProperty(ref _type, value))
            {
                OnPropertyChanged(nameof(IsSmoke));
                OnPropertyChanged(nameof(IsInferno));
            }
        }
    }

    public string IconPath
    {
        get => _iconPath;
        private set => SetProperty(ref _iconPath, value);
    }

    public Vec3 Position
    {
        get => _position;
        private set => SetProperty(ref _position, value);
    }

    public bool IsDetonated
    {
        get => _isDetonated;
        private set
        {
            if (SetProperty(ref _isDetonated, value))
            {
                OnPropertyChanged(nameof(IsSmoke));
            }
        }
    }

    public double SmokeProgress
    {
        get => _smokeProgress;
        private set
        {
            if (SetProperty(ref _smokeProgress, value))
            {
                OnPropertyChanged(nameof(SmokeRemainingProgress));
            }
        }
    }

    public double SmokeRemainingProgress => Math.Clamp(1.0 - SmokeProgress, 0.0, 1.0);

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

    public double CurrentSampleCanvasX => _currentCanvasX;
    public double CurrentSampleCanvasY => _currentCanvasY;

    public void Update(string type, string iconPath, Vec3 position, bool isDetonated, double smokeProgress)
    {
        Type = type;
        IconPath = iconPath;
        Position = position;
        IsDetonated = isDetonated;
        SmokeProgress = smokeProgress;
    }

    public void PushPositionSample(double canvasX, double canvasY, DateTime sampleTimeUtc, bool snap)
    {
        if (snap || !_hasInterpolationSample)
        {
            _hasInterpolationSample = true;
            _previousCanvasX = canvasX;
            _previousCanvasY = canvasY;
            _currentCanvasX = canvasX;
            _currentCanvasY = canvasY;
            _previousSampleTimeUtc = sampleTimeUtc;
            _currentSampleTimeUtc = sampleTimeUtc;
            CanvasX = canvasX;
            CanvasY = canvasY;
            return;
        }

        _previousCanvasX = _currentCanvasX;
        _previousCanvasY = _currentCanvasY;
        _currentCanvasX = canvasX;
        _currentCanvasY = canvasY;
        _previousSampleTimeUtc = _currentSampleTimeUtc;
        _currentSampleTimeUtc = sampleTimeUtc;
    }

    public void AdvanceInterpolation(DateTime renderTimeUtc, double interpolationDelaySeconds)
    {
        if (!_hasInterpolationSample)
            return;

        if (_currentSampleTimeUtc <= _previousSampleTimeUtc)
        {
            CanvasX = _currentCanvasX;
            CanvasY = _currentCanvasY;
            return;
        }

        var delayedTimeUtc = renderTimeUtc - TimeSpan.FromSeconds(interpolationDelaySeconds);
        var totalSeconds = (_currentSampleTimeUtc - _previousSampleTimeUtc).TotalSeconds;
        var elapsedSeconds = (delayedTimeUtc - _previousSampleTimeUtc).TotalSeconds;
        var t = Math.Clamp(elapsedSeconds / totalSeconds, 0.0, 1.0);

        CanvasX = Lerp(_previousCanvasX, _currentCanvasX, t);
        CanvasY = Lerp(_previousCanvasY, _currentCanvasY, t);
    }

    private static double Lerp(double from, double to, double t)
    {
        return from + (to - from) * t;
    }
}

public sealed class RadarDetonationViewModel : ViewModelBase
{
    private double _canvasX;
    private double _canvasY;
    private double _opacity = 1.0;
    private IBrush _fill;

    public RadarDetonationViewModel(double canvasX, double canvasY, IBrush fill, DateTime expiresAtUtc)
    {
        _canvasX = canvasX;
        _canvasY = canvasY;
        _fill = fill;
        ExpiresAtUtc = expiresAtUtc;
    }

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

    public double Opacity
    {
        get => _opacity;
        set => SetProperty(ref _opacity, value);
    }

    public IBrush Fill
    {
        get => _fill;
        set => SetProperty(ref _fill, value);
    }

    public DateTime ExpiresAtUtc { get; }
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
    private const double InterpolationTimerIntervalMs = 16.0;
    private const double DefaultInterpolationDelaySeconds = 0.1;
    private const double MaxInterpolationDelaySeconds = 0.25;
    private const double TeleportSnapDistancePixels = 160.0;
    private const double DetonationDurationSeconds = 0.5;
    private readonly GsiServer _gsiServer;
    private readonly RadarConfigProvider _configProvider;
    private readonly RadarProjector _projector;
    private readonly Dictionary<string, SmokeTracker> _smokeTrackers = new();
    private readonly Dictionary<string, PlayerWeaponState> _playerWeaponStates = new();
    private readonly Dictionary<string, RadarPlayerViewModel> _playerMarkers = new();
    private readonly Dictionary<string, RadarDeadPlayerViewModel> _deadPlayerMarkers = new();
    private readonly Dictionary<string, RadarDroppedDefuserViewModel> _droppedDefuserMarkers = new();
    private readonly Dictionary<string, RadarGrenadeViewModel> _grenadeMarkers = new();
    private readonly Dictionary<string, Vec3> _lastAlivePositions = new();
    private readonly Dictionary<string, int> _playerHeightBuckets = new();
    private readonly CampathsDockViewModel? _campathsVm;
    private readonly HlaeWebSocketClient? _webSocketClient;
    private readonly RadarSettings _settings;
    private CampathProfileViewModel? _attachedProfile;
    private DispatcherTimer? _animationTimer;
    private CampathPathViewModel? _hoveredCampath;
    private string? _hoveredCampathName;
    private Bitmap? _hoveredCampathThumbnail;

    private Bitmap? _radarImage;
    private string? _currentMap;
    private bool _hasRadar;
    private long _lastProcessedHeartbeat;
    private long _lastInterpolatedHeartbeat = -1;
    private DateTime _lastSampleTimeUtc;
    private double _interpolationDelaySeconds = DefaultInterpolationDelaySeconds;

    private const double SmokeDurationSeconds = 20.0;
    private const double HeightScaleMin = 0.85;
    private const double HeightScaleMax = 1.15;
    private const double HeightBucketSize = 64.0;
    private const double HeightBucketHysteresisRatio = 0.6;

    public ObservableCollection<RadarPlayerViewModel> Players { get; } = new();
    public ObservableCollection<RadarDeadPlayerViewModel> DeadPlayers { get; } = new();
    public ObservableCollection<RadarDroppedDefuserViewModel> DroppedDefusers { get; } = new();
    public ObservableCollection<RadarGrenadeViewModel> Grenades { get; } = new();
    public ObservableCollection<RadarDetonationViewModel> Detonations { get; } = new();
    public ObservableCollection<FlameViewModel> Flames { get; } = new();
    public ObservableCollection<RadarBombViewModel> Bombs { get; } = new();
    public ObservableCollection<CampathPathViewModel> CampathPaths { get; } = new();

    public string? HoveredCampathName
    {
        get => _hoveredCampathName;
        private set => SetProperty(ref _hoveredCampathName, value);
    }

    public Bitmap? HoveredCampathThumbnail
    {
        get => _hoveredCampathThumbnail;
        private set
        {
            if (SetProperty(ref _hoveredCampathThumbnail, value))
            {
                OnPropertyChanged(nameof(HasHoveredCampathThumbnail));
            }
        }
    }

    public bool HasHoveredCampathThumbnail => _hoveredCampathThumbnail != null;

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

    public RadarSettings RadarSettings => _settings;

    public RadarDockViewModel(GsiServer gsiServer, RadarConfigProvider configProvider, RadarSettings settings, CampathsDockViewModel? campathsVm, HlaeWebSocketClient? webSocketClient)
    {
        _gsiServer = gsiServer;
        _configProvider = configProvider;
        _settings = settings;
        _campathsVm = campathsVm;
        _webSocketClient = webSocketClient;
        _projector = new RadarProjector(configProvider);

        Title = "Radar";
        CanClose = true;
        CanFloat = true;
        CanPin = true;

        _gsiServer.GameStateUpdated += OnGameStateUpdated;

        _settings.PropertyChanged += OnSettingsChanged;

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(InterpolationTimerIntervalMs)
        };
        _animationTimer.Tick += OnAnimationTick;
        _animationTimer.Start();

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

        var sampleTimeUtc = DateTime.UtcNow;
        UpdateInterpolationDelay(state.Heartbeat, sampleTimeUtc);

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
            _playerMarkers.Clear();
            DeadPlayers.Clear();
            DroppedDefusers.Clear();
            Grenades.Clear();
            Detonations.Clear();
            CampathPaths.Clear();
            ClearCampathHover();
            _deadPlayerMarkers.Clear();
            _droppedDefuserMarkers.Clear();
            _grenadeMarkers.Clear();
            _lastAlivePositions.Clear();
            _playerHeightBuckets.Clear();
            return;
        }

        var aliveColorT = new SolidColorBrush(Color.Parse("#FF9340"));
        var aliveColorCt = new SolidColorBrush(Color.Parse("#4DB3FF"));
        var border = new SolidColorBrush(Color.Parse("#0D1015"));
        var bombColor = new SolidColorBrush(Color.Parse("#FF5353"));
        var pendingPlayers = new List<(RadarPlayerViewModel Vm, int HeightBucket)>();

        if (mapChanged)
        {
            Players.Clear();
            _playerMarkers.Clear();
            DeadPlayers.Clear();
            DroppedDefusers.Clear();
            Grenades.Clear();
            Detonations.Clear();
            _deadPlayerMarkers.Clear();
            _droppedDefuserMarkers.Clear();
            _grenadeMarkers.Clear();
            _lastAlivePositions.Clear();
            _playerHeightBuckets.Clear();
        }

        // Clean up weapon states for disconnected players
        var currentPlayerIds = new HashSet<string>(state.Players.Select(p => p.SteamId));
        var stateKeysToRemove = _playerWeaponStates.Keys
            .Where(id => !currentPlayerIds.Contains(id))
            .ToList();
        foreach (var key in stateKeysToRemove)
        {
            _playerWeaponStates.Remove(key);
            _playerHeightBuckets.Remove(key);
        }

        var deadKeysToRemove = _deadPlayerMarkers.Keys
            .Where(id => !currentPlayerIds.Contains(id))
            .ToList();
        foreach (var key in deadKeysToRemove)
        {
            DeadPlayers.Remove(_deadPlayerMarkers[key]);
            _deadPlayerMarkers.Remove(key);
            _lastAlivePositions.Remove(key);
        }

        var aliveIds = new HashSet<string>(state.Players.Where(p => p.IsAlive).Select(p => p.SteamId));
        var playerKeysToRemove = _playerMarkers.Keys
            .Where(id => !currentPlayerIds.Contains(id) || !aliveIds.Contains(id))
            .ToList();
        foreach (var key in playerKeysToRemove)
        {
            Players.Remove(_playerMarkers[key]);
            _playerMarkers.Remove(key);
            _playerHeightBuckets.Remove(key);
        }

        foreach (var p in state.Players)
        {
            if (p.IsAlive)
            {
                _lastAlivePositions[p.SteamId] = p.Position;
                if (_deadPlayerMarkers.TryGetValue(p.SteamId, out var deadVm))
                {
                    DeadPlayers.Remove(deadVm);
                    _deadPlayerMarkers.Remove(p.SteamId);
                }

                if (!_projector.TryProject(state.MapName, p.Position, out var x, out var y, out var level))
                    continue;

                var brush = p.Team.Equals("T", StringComparison.OrdinalIgnoreCase) ? aliveColorT : aliveColorCt;
                if (p.HasBomb) brush = bombColor;

                var activeWeapon = p.Weapons.FirstOrDefault(w =>
                    w.State.Equals("active", StringComparison.OrdinalIgnoreCase));
                var activeGrenadeIcon = GetActiveGrenadeIconPath(p.Weapons);
                var hasSniperEquipped = HasSniperEquipped(p.Weapons);

                if (!_playerMarkers.TryGetValue(p.SteamId, out var vm))
                {
                    vm = new RadarPlayerViewModel(p.SteamId, p.Name, p.Team, p.Slot, brush, border);
                    _playerMarkers[p.SteamId] = vm;
                }

                var targetCanvasX = x * 1024.0 - 18.0;
                var targetCanvasY = y * 1024.0 - 22.0;
                var targetRotation = NormalizeDegrees(Math.Atan2(p.Forward.X, p.Forward.Y) * 180.0 / Math.PI);
                var shouldSnapPosition = mapChanged
                    || !string.Equals(vm.Level, level, StringComparison.OrdinalIgnoreCase)
                    || ShouldSnapInterpolation(vm.CanvasX, vm.CanvasY, targetCanvasX, targetCanvasY);

                vm.Fill = brush;
                vm.Border = border;
                vm.Slot = p.Slot;
                vm.RelativeX = x;
                vm.RelativeY = y;
                vm.IsAlive = p.IsAlive;
                vm.HasBomb = p.HasBomb;
                vm.IsFocused = p.SteamId == state.FocusedPlayerSteamId;
                vm.Level = level;
                vm.UseAltBindings = _settings.UseAltPlayerBinds;
                vm.ActiveGrenadeIconPath = activeGrenadeIcon;
                vm.IsSniperEquipped = hasSniperEquipped;
                vm.Altitude = p.Position.Z;
                vm.SetHeightScale(ResolveHeightScale(state.MapName, p.Position.Z, level));
                vm.SetBaseScale(_settings.MarkerScale);
                vm.PushPositionSample(targetCanvasX, targetCanvasY, targetRotation, sampleTimeUtc, shouldSnapPosition);

                var heightBucket = ResolveHeightBucket(p.SteamId, p.Position.Z);
                pendingPlayers.Add((vm, heightBucket));

                // Track weapon state and detect shots
                if (!_playerWeaponStates.TryGetValue(p.SteamId, out var weaponState))
                {
                    weaponState = new PlayerWeaponState();
                    _playerWeaponStates[p.SteamId] = weaponState;
                }

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
            else
            {
                if (_deadPlayerMarkers.ContainsKey(p.SteamId))
                {
                    continue;
                }

                var deathPos = _lastAlivePositions.TryGetValue(p.SteamId, out var lastPos) ? lastPos : p.Position;
                if (!_projector.TryProject(state.MapName, deathPos, out var x, out var y, out var deathLevel))
                {
                    continue;
                }

                var stroke = p.Team.Equals("T", StringComparison.OrdinalIgnoreCase) ? aliveColorT : aliveColorCt;
                var deadVm = new RadarDeadPlayerViewModel(p.SteamId, p.Team, stroke)
                {
                    CanvasX = x * 1024.0 - 6.0, // center the 12px cross on the projected point
                    CanvasY = y * 1024.0 - 6.0,
                    Altitude = deathPos.Z
                };
                deadVm.SetHeightScale(ResolveHeightScale(state.MapName, deathPos.Z, deathLevel));
                deadVm.SetBaseScale(_settings.MarkerScale);

                _deadPlayerMarkers[p.SteamId] = deadVm;
                DeadPlayers.Add(deadVm);
            }
        }

        if (pendingPlayers.Count > 0)
        {
            var orderedPlayers = pendingPlayers
                .OrderBy(p => p.Vm.IsFocused ? 1 : 0)
                .ThenBy(p => p.HeightBucket)
                .ThenBy(p => p.Vm.Slot < 0 ? int.MaxValue : p.Vm.Slot)
                .ThenBy(p => p.Vm.Id, StringComparer.Ordinal)
                .Select(p => p.Vm)
                .ToList();

            SyncPlayers(Players, orderedPlayers);
        }

        var orderedDroppedDefusers = new List<RadarDroppedDefuserViewModel>();
        var presentDroppedDefuserIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var droppedDefuser in state.DroppedDefusers)
        {
            if (!_projector.TryProject(state.MapName, droppedDefuser.Position, out var x, out var y, out var level))
            {
                continue;
            }

            presentDroppedDefuserIds.Add(droppedDefuser.Id);

            if (!_droppedDefuserMarkers.TryGetValue(droppedDefuser.Id, out var defuserVm))
            {
                defuserVm = new RadarDroppedDefuserViewModel(droppedDefuser.Id);
                _droppedDefuserMarkers[droppedDefuser.Id] = defuserVm;
            }

            defuserVm.CanvasX = x * 1024.0 + 2.0;
            defuserVm.CanvasY = y * 1024.0 - 18.0;
            defuserVm.Altitude = droppedDefuser.Position.Z;
            defuserVm.SetHeightScale(ResolveHeightScale(state.MapName, droppedDefuser.Position.Z, level));
            defuserVm.SetBaseScale(_settings.MarkerScale);
            orderedDroppedDefusers.Add(defuserVm);
        }

        var droppedDefusersToRemove = _droppedDefuserMarkers.Keys
            .Where(id => !presentDroppedDefuserIds.Contains(id))
            .ToList();
        foreach (var droppedDefuserId in droppedDefusersToRemove)
        {
            _droppedDefuserMarkers.Remove(droppedDefuserId);
        }

        SyncDroppedDefusers(DroppedDefusers, orderedDroppedDefusers);

        // Process grenades
        Flames.Clear();
        var presentSmokeKeys = new HashSet<string>(
            state.Grenades
                .Where(g => g.Type == "smoke")
                .Select(GetSmokeKey),
            StringComparer.Ordinal);
        var presentGrenadeKeys = new HashSet<string>(StringComparer.Ordinal);
        var orderedGrenades = new List<RadarGrenadeViewModel>();

        var nowUtc = DateTime.UtcNow;

        // Update smoke trackers only on heartbeat change
        if (state.Heartbeat != _lastProcessedHeartbeat)
        {
            _lastProcessedHeartbeat = state.Heartbeat;

            // Track current smokes to clean up old ones
            var currentSmokeKeys = new HashSet<string>(StringComparer.Ordinal);

            // Update smoke trackers
            foreach (var g in state.Grenades)
            {
                if (g.Type == "smoke")
                {
                    var key = GetSmokeKey(g);
                    currentSmokeKeys.Add(key);

                    if (!_smokeTrackers.TryGetValue(key, out var tracker))
                    {
                        // New smoke - add to tracker
                        tracker = new SmokeTracker
                        {
                            Position = g.Position,
                            LastPosition = g.Position,
                            IsDetonated = false,
                            HasDetonatedOnce = false
                        };
                        _smokeTrackers[key] = tracker;
                    }
                    else
                    {
                        // Update existing smoke on new GSI update
                        double distMoved = GetDistance(g.Position, tracker.Position);

                        if (!tracker.IsDetonated && !tracker.HasDetonatedOnce)
                        {
                            if (distMoved <= 0.0)
                            {
                                tracker.IsDetonated = true;
                                tracker.DetonatedAtUtc ??= nowUtc;
                                tracker.HasDetonatedOnce = true;
                            }
                        }

                        tracker.LastPosition = tracker.Position;
                        tracker.Position = g.Position;
                    }
                }
            }

            // Remove old smokes that are no longer in GSI data (unless timer is still running)
            var keysToRemove = _smokeTrackers.Keys
                .Where(k => !currentSmokeKeys.Contains(k))
                .Where(k =>
                {
                    var tracker = _smokeTrackers[k];
                    if (!tracker.IsDetonated || tracker.DetonatedAtUtc == null)
                    {
                        return true;
                    }

                    return (nowUtc - tracker.DetonatedAtUtc.Value).TotalSeconds >= SmokeDurationSeconds;
                })
                .ToList();
            foreach (var key in keysToRemove)
            {
                _smokeTrackers.Remove(key);
            }
        }

        foreach (var g in state.Grenades)
        {
            double smokeProgress = 0;
            Vec3 position = g.Position;
            bool isDetonated;
            string smokeKey = string.Empty;

            if (g.Type == "smoke")
            {
                smokeKey = GetSmokeKey(g);
                if (!_smokeTrackers.TryGetValue(smokeKey, out var tracker))
                {
                    tracker = new SmokeTracker
                    {
                        Position = g.Position,
                        LastPosition = g.Position,
                        IsDetonated = false
                    };
                    _smokeTrackers[smokeKey] = tracker;
                }

                isDetonated = tracker.IsDetonated;
                position = tracker.Position;

                if (isDetonated && tracker.DetonatedAtUtc == null)
                {
                    tracker.DetonatedAtUtc = nowUtc;
                }

                if (isDetonated && tracker.DetonatedAtUtc.HasValue)
                {
                    var elapsed = (nowUtc - tracker.DetonatedAtUtc.Value).TotalSeconds;
                    if (elapsed >= SmokeDurationSeconds)
                    {
                        tracker.IsDetonated = false;
                        continue;
                    }
                    smokeProgress = Math.Clamp(elapsed / SmokeDurationSeconds, 0, 1);
                }
            }
            else
            {
                // For other grenades, use velocity check
                isDetonated = g.Velocity.X == 0 && g.Velocity.Y == 0 && g.Velocity.Z == 0;
            }

            if (!_projector.TryProject(state.MapName, position, out var x, out var y, out var level))
                continue;

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

            var shouldAdd = g.Type switch
            {
                "smoke" => _smokeTrackers.TryGetValue(smokeKey, out var tracker)
                    && (tracker.IsDetonated || !tracker.HasDetonatedOnce),
                "inferno" => true,
                _ => !isDetonated
            };

            if (shouldAdd)
            {
                var grenadeKey = GetGrenadeKey(g, smokeKey);
                presentGrenadeKeys.Add(grenadeKey);

                if (!_grenadeMarkers.TryGetValue(grenadeKey, out var grenadeVm))
                {
                    grenadeVm = new RadarGrenadeViewModel(grenadeKey, g.Type, iconPath, position, isDetonated, smokeProgress);
                    _grenadeMarkers[grenadeKey] = grenadeVm;
                }

                var targetCanvasX = x * 1024.0 - 12.0;
                var targetCanvasY = y * 1024.0 - 12.0;
                var isMovingProjectile = !isDetonated && g.Type != "inferno";
                var shouldSnapPosition = mapChanged
                    || !isMovingProjectile
                    || !string.Equals(grenadeVm.Type, g.Type, StringComparison.Ordinal)
                    || grenadeVm.IsDetonated != isDetonated
                    || ShouldSnapInterpolation(grenadeVm.CanvasX, grenadeVm.CanvasY, targetCanvasX, targetCanvasY);

                grenadeVm.Update(g.Type, iconPath, position, isDetonated, smokeProgress);
                grenadeVm.PushPositionSample(targetCanvasX, targetCanvasY, sampleTimeUtc, shouldSnapPosition);
                orderedGrenades.Add(grenadeVm);
            }
        }

        var grenadeKeysToRemove = _grenadeMarkers.Keys
            .Where(key => !presentGrenadeKeys.Contains(key))
            .ToList();
        foreach (var key in grenadeKeysToRemove)
        {
            if (!mapChanged && _grenadeMarkers.TryGetValue(key, out var removedVm))
            {
                TryAddDetonationEffect(removedVm, nowUtc);
            }

            _grenadeMarkers.Remove(key);
        }

        SyncGrenades(Grenades, orderedGrenades);

        var missingSmokeKeys = _smokeTrackers.Keys.Where(k => !presentSmokeKeys.Contains(k)).ToList();
        foreach (var key in missingSmokeKeys)
        {
            _smokeTrackers.Remove(key);
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

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        var nowUtc = DateTime.UtcNow;

        foreach (var player in Players)
        {
            player.AdvanceInterpolation(nowUtc, _interpolationDelaySeconds);
            player.UpdateShootingState();
        }

        foreach (var grenade in Grenades)
        {
            grenade.AdvanceInterpolation(nowUtc, _interpolationDelaySeconds);
        }

        for (int i = Detonations.Count - 1; i >= 0; i--)
        {
            var detonation = Detonations[i];
            var remainingSeconds = (detonation.ExpiresAtUtc - nowUtc).TotalSeconds;
            if (remainingSeconds <= 0)
            {
                Detonations.RemoveAt(i);
                continue;
            }

            detonation.Opacity = Math.Clamp(remainingSeconds / DetonationDurationSeconds, 0.0, 1.0);
        }
    }

    private static double NormalizeDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees < 0) degrees += 360.0;
        return degrees;
    }

    private static double GetDistance(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static string? GetActiveGrenadeIconPath(IReadOnlyList<GsiWeapon> weapons)
    {
        if (weapons == null || weapons.Count == 0)
            return null;

        var activeWeapon = weapons.FirstOrDefault(w =>
            w.State.Equals("active", StringComparison.OrdinalIgnoreCase));
        if (activeWeapon != null)
        {
            var activePath = GetGrenadeIconPath(activeWeapon);
            if (activePath != null)
                return activePath;
        }

        foreach (var weapon in weapons)
        {
            if (weapon.State.Equals("holstered", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = GetGrenadeIconPath(weapon);
            if (path != null)
                return path;
        }

        return null;
    }

    private static string? GetGrenadeIconPath(GsiWeapon weapon)
    {
        var fromName = GetGrenadeIconPath(weapon.Name);
        if (fromName != null)
            return fromName;

        return GetGrenadeIconPath(weapon.Type);
    }

    private static string? GetGrenadeIconPath(string? weaponName)
    {
        if (string.IsNullOrWhiteSpace(weaponName))
            return null;

        var normalized = weaponName.Trim().ToLowerInvariant();
        if (normalized.StartsWith("weapon_", StringComparison.Ordinal))
        {
            normalized = normalized.Substring("weapon_".Length);
        }

        var iconName = normalized switch
        {
            "hegrenade" => "hegrenade",
            "flashbang" => "flashbang",
            "smokegrenade" => "smokegrenade",
            "molotov" => "molotov",
            "incgrenade" => "incgrenade",
            "firebomb" => "firebomb",
            "decoy" => "decoy",
            "tagrenade" => "tagrenade",
            _ => null
        };

        return iconName == null ? null : $"avares://HlaeObsTools/Assets/hud/weapons/{iconName}.svg";
    }

    private static bool HasSniperEquipped(IReadOnlyList<GsiWeapon> weapons)
    {
        if (weapons == null || weapons.Count == 0)
        {
            return false;
        }

        foreach (var weapon in weapons)
        {
            if (string.IsNullOrWhiteSpace(weapon.Name))
            {
                continue;
            }

            var normalized = weapon.Name.Trim().ToLowerInvariant();
            if (normalized.StartsWith("weapon_", StringComparison.Ordinal))
            {
                normalized = normalized.Substring("weapon_".Length);
            }

            if (normalized == "awp" || normalized == "ssg08")
            {
                return true;
            }
        }

        return false;
    }

    private void LoadRadarResources(string mapName)
    {
        // The bundled radar metadata uses a 1024x1024 reference image. Reset this
        // before loading so a missing image cannot retain the previous map's size.
        _projector.SetRadarImageSize(1024, 1024);

        if (!_configProvider.TryGet(mapName, out var cfg))
        {
            HasRadar = false;
            RadarImage?.Dispose();
            RadarImage = null;
            return;
        }

        HasRadar = true;
        RadarImage?.Dispose();
        RadarImage = null;

        if (cfg.IsUserImagePath && TryLoadUserRadarImage(cfg.ImagePath))
        {
            return;
        }

        var imageMapName = cfg.ImageMapName;
        var ingameImage = $"/hud/img/radars/ingame/{imageMapName}.png";
        var imageCandidates = imageMapName == "de_nuke" && _settings.RadarStyle != "JTs"
            ? new[] { "/hud/img/radars/simpleradar/de_nuke.webp" }
            : _settings.RadarStyle switch
            {
                "simpleradar" => new[]
                {
                    $"/hud/img/radars/simpleradar/{imageMapName}.webp",
                    ingameImage,
                    cfg.ImagePath
                },
                "JTs" => new[]
                {
                    $"/hud/img/radars/jts/{imageMapName}.png",
                    ingameImage,
                    cfg.ImagePath
                },
                _ => new[] { ingameImage, cfg.ImagePath }
            };

        var userStyleDirectory = Path.Combine(RadarConfigProvider.UserRadarDirectory, _settings.RadarStyle);
        var userStyleExtension = _settings.RadarStyle == "simpleradar" ? ".webp" : ".png";
        if (TryLoadUserRadarImage(Path.Combine(userStyleDirectory, imageMapName + userStyleExtension)))
        {
            return;
        }

        foreach (var imagePath in imageCandidates.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                RadarImage = new Bitmap(AssetLoader.Open(CreateRadarAssetUri(imagePath!)));
                SetRadarImageSize(RadarImage);
                return;
            }
            catch
            {
                // The requested style does not cover this map. Try the next fallback.
            }
        }

        Console.WriteLine($"Failed to load radar image for {mapName}.");
    }

    private bool TryLoadUserRadarImage(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return false;
        }

        var resolvedPath = Path.IsPathRooted(imagePath)
            ? imagePath
            : Path.Combine(RadarConfigProvider.UserRadarDirectory, imagePath.TrimStart('/', '\\'));

        if (!File.Exists(resolvedPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(resolvedPath);
            RadarImage = new Bitmap(stream);
            SetRadarImageSize(RadarImage);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load user radar image '{resolvedPath}': {ex.Message}");
            return false;
        }
    }

    private static Uri CreateRadarAssetUri(string imagePath)
    {
        // Our assets live under Assets/hud/... (no extra /img segment). Normalize if needed.
        if (imagePath.StartsWith("/hud/img/", StringComparison.OrdinalIgnoreCase))
        {
            imagePath = "/hud/" + imagePath.Substring("/hud/img/".Length);
        }

        return new Uri($"avares://HlaeObsTools/Assets/{imagePath.TrimStart('/')}");
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
        _animationTimer?.Stop();
        _animationTimer = null;
        RadarImage?.Dispose();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadarSettings.MarkerScale))
        {
            foreach (var player in Players)
            {
                player.SetBaseScale(_settings.MarkerScale);
            }
            foreach (var dead in DeadPlayers)
            {
                dead.SetBaseScale(_settings.MarkerScale);
            }
            foreach (var droppedDefuser in DroppedDefusers)
            {
                droppedDefuser.SetBaseScale(_settings.MarkerScale);
            }
        }
        else if (e.PropertyName == nameof(RadarSettings.RadarStyle) && !string.IsNullOrWhiteSpace(_currentMap))
        {
            LoadRadarResources(_currentMap);
        }
        else if (e.PropertyName == nameof(RadarSettings.UseAltPlayerBinds))
        {
            foreach (var player in Players)
            {
                player.UseAltBindings = _settings.UseAltPlayerBinds;
            }
        }
        else if (e.PropertyName == nameof(RadarSettings.HeightScaleMultiplier))
        {
            if (string.IsNullOrWhiteSpace(_currentMap))
                return;

            foreach (var player in Players)
            {
                player.SetHeightScale(ResolveHeightScale(_currentMap, player.Altitude, player.Level));
            }
            foreach (var dead in DeadPlayers)
            {
                dead.SetHeightScale(ResolveHeightScale(_currentMap, dead.Altitude, null));
            }
            foreach (var droppedDefuser in DroppedDefusers)
            {
                droppedDefuser.SetHeightScale(ResolveHeightScale(_currentMap, droppedDefuser.Altitude, null));
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
        if (e.PropertyName == nameof(CampathItemViewModel.FilePath)
            || e.PropertyName == nameof(CampathItemViewModel.Name)
            || e.PropertyName == nameof(CampathItemViewModel.ImagePath)
            || e.PropertyName == nameof(CampathItemViewModel.Thumbnail))
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

    private void SetRadarImageSize(Bitmap image)
    {
        _projector.SetRadarImageSize(image.PixelSize.Width, image.PixelSize.Height);
    }

    private void OnCampathGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathGroupViewModel.HideInRadar))
            RefreshCampathOverlay();
    }

    private void OnCampathGroupCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var group in e.OldItems.OfType<CampathGroupViewModel>())
                group.PropertyChanged -= OnCampathGroupChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var group in e.NewItems.OfType<CampathGroupViewModel>())
                group.PropertyChanged += OnCampathGroupChanged;
        }

        RefreshCampathOverlay();
    }

    private void AttachProfile(CampathProfileViewModel? profile)
    {
        if (profile == null)
            return;

        profile.Campaths.CollectionChanged += OnCampathCollectionChanged;
        profile.Groups.CollectionChanged += OnCampathGroupCollectionChanged;
        foreach (var item in profile.Campaths)
        {
            item.PropertyChanged += OnCampathItemChanged;
        }
        foreach (var group in profile.Groups)
        {
            group.PropertyChanged += OnCampathGroupChanged;
        }

        _attachedProfile = profile;
    }

    private void DetachProfile(CampathProfileViewModel? profile)
    {
        var target = profile ?? _attachedProfile;
        if (target == null)
            return;

        target.Campaths.CollectionChanged -= OnCampathCollectionChanged;
        target.Groups.CollectionChanged -= OnCampathGroupCollectionChanged;
        foreach (var item in target.Campaths)
        {
            item.PropertyChanged -= OnCampathItemChanged;
        }
        foreach (var group in target.Groups)
        {
            group.PropertyChanged -= OnCampathGroupChanged;
        }

        if (ReferenceEquals(_attachedProfile, target))
        {
            _attachedProfile = null;
        }
    }

    private void RefreshCampathOverlay()
    {
        CampathPaths.Clear();
        ClearCampathHover();

        if (_campathsVm?.SelectedProfile == null || string.IsNullOrWhiteSpace(_currentMap) || !HasRadar)
            return;

        var hiddenCampathIds = _campathsVm.SelectedProfile.Groups
            .Where(group => group.HideInRadar)
            .SelectMany(group => group.CampathIds)
            .ToHashSet();

        foreach (var campath in _campathsVm.SelectedProfile.Campaths)
        {
            if (hiddenCampathIds.Contains(campath.Id))
                continue;

            if (string.IsNullOrWhiteSpace(campath.FilePath) || !File.Exists(campath.FilePath))
                continue;

            var parsed = CampathFileParser.ParseSet(campath.FilePath);
            if (parsed == null || parsed.Tracks.Count == 0)
                continue;

            foreach (var track in parsed.Tracks)
            {
                if (track.Campath.Points.Count == 0)
                    continue;
                var points = BuildCampathPolyline(track.Campath);
                if (points.Count == 0)
                    continue;

                var forward = track.Campath.Points[0].Forward;
                var angle = NormalizeDegrees(Math.Atan2(forward.X, forward.Y) * 180.0 / Math.PI) - 90;
                var iconX = points[0].X - 12.0; // center 24px icon
                var iconY = points[0].Y - 12.0;
                var displayName = parsed.Tracks.Count > 1
                    ? $"{campath.Name} — {track.Name}"
                    : campath.Name;

                CampathPaths.Add(new CampathPathViewModel(
                    campath.Id, displayName, campath.FilePath, points,
                    iconX, iconY, angle, campath.Thumbnail));
            }
        }
    }

    public async void PlayCampath(CampathPathViewModel? path)
    {
        if (path == null)
            return;

        var campath = _campathsVm?.SelectedProfile?.Campaths.FirstOrDefault(c => c.Id == path.Id);
        if (campath != null)
        {
            await _campathsVm!.PlayCampathAsync(campath);
            return;
        }

        if (_webSocketClient == null || string.IsNullOrWhiteSpace(path.FilePath))
            return;

        await _webSocketClient.SendCampathPlayAsync(path.FilePath, 0);
    }

    public void SetCampathHighlight(CampathPathViewModel? target, bool isHighlighted)
    {
        if (isHighlighted)
        {
            foreach (var p in CampathPaths)
            {
                p.IsHighlighted = target != null && p.Id == target.Id;
            }
            SetCampathHover(target);
        }
        else if (target != null)
        {
            if (_hoveredCampath != null
                && !ReferenceEquals(_hoveredCampath, target)
                && _hoveredCampath.Id == target.Id)
                return;
            foreach (var path in CampathPaths.Where(path => path.Id == target.Id))
                path.IsHighlighted = false;
            if (ReferenceEquals(_hoveredCampath, target))
            {
                ClearCampathHover();
            }
        }
    }

    private void SetCampathHover(CampathPathViewModel? target)
    {
        _hoveredCampath = target;
        HoveredCampathName = target?.Name;
        HoveredCampathThumbnail = target?.Thumbnail;
    }

    private void ClearCampathHover()
    {
        _hoveredCampath = null;
        HoveredCampathName = null;
        HoveredCampathThumbnail = null;
    }

    private AvaloniaList<Point> BuildCampathPolyline(CampathFile parsed)
    {
        var result = new AvaloniaList<Point>();
        if (parsed.Points.Count == 0 || string.IsNullOrWhiteSpace(_currentMap))
            return result;

        var forcedLevel = GetCampathForcedLevel(parsed);
        bool useLinear = parsed.IsLinearPosition || parsed.Points.Count < 3;

        void AddProjected(Vec3 pos)
        {
            if (_projector.TryProject(_currentMap!, pos, forcedLevel, out var px, out var py, out _))
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

    private string? GetCampathForcedLevel(CampathFile parsed)
    {
        if (string.IsNullOrWhiteSpace(_currentMap))
            return null;

        if (!_configProvider.TryGet(_currentMap, out var config) || config.Levels.Count == 0)
            return null;

        RadarLevel? lowest = null;
        foreach (var point in parsed.Points)
        {
            var level = ResolveLevel(config, point.Position.Z);
            if (level == null)
                continue;

            if (lowest == null || level.AltitudeMin < lowest.AltitudeMin)
            {
                lowest = level;
            }
        }

        return lowest?.Name;
    }

    private static RadarLevel? ResolveLevel(RadarConfig config, double altitude)
    {
        foreach (var level in config.Levels)
        {
            if (altitude > level.AltitudeMin)
            {
                return level;
            }
        }

        return null;
    }

    private int ResolveHeightBucket(string playerId, double altitude)
    {
        var bucket = (int)Math.Round(altitude / HeightBucketSize, MidpointRounding.AwayFromZero);
        if (_playerHeightBuckets.TryGetValue(playerId, out var previous))
        {
            if (bucket != previous)
            {
                var previousCenter = previous * HeightBucketSize;
                if (Math.Abs(altitude - previousCenter) < HeightBucketSize * HeightBucketHysteresisRatio)
                {
                    bucket = previous;
                }
            }
        }

        _playerHeightBuckets[playerId] = bucket;
        return bucket;
    }

    private double ResolveHeightScale(string mapName, double altitude, string? levelName)
    {
        if (!_configProvider.TryGet(mapName, out var config))
        {
            return 1.0;
        }

        RadarLevel? level = null;
        if (!string.IsNullOrWhiteSpace(levelName))
        {
            level = config.Levels.FirstOrDefault(l =>
                string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
        }

        level ??= ResolveLevel(config, altitude);

        double? min = level?.ScaleMinAltitude ?? config.ScaleMinAltitude;
        double? max = level?.ScaleMaxAltitude ?? config.ScaleMaxAltitude;

        if (min == null || max == null)
        {
            return 1.0;
        }

        if (max.Value <= min.Value || Math.Abs(max.Value - min.Value) < 0.0001)
        {
            return 1.0;
        }

        var t = (altitude - min.Value) / (max.Value - min.Value);
        t = Math.Clamp(t, 0.0, 1.0);
        var baseScale = HeightScaleMin + t * (HeightScaleMax - HeightScaleMin);
        var multiplier = _settings.HeightScaleMultiplier;
        return 1.0 + (baseScale - 1.0) * multiplier;
    }

    private static void SyncPlayers(ObservableCollection<RadarPlayerViewModel> target, IReadOnlyList<RadarPlayerViewModel> ordered)
    {
        if (ordered.Count == 0)
        {
            target.Clear();
            return;
        }

        var orderedSet = new HashSet<RadarPlayerViewModel>(ordered);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!orderedSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            var desired = ordered[i];
            if (i < target.Count && ReferenceEquals(target[i], desired))
            {
                continue;
            }

            var existingIndex = target.IndexOf(desired);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, desired);
            }
        }
    }

    private static void SyncGrenades(ObservableCollection<RadarGrenadeViewModel> target, IReadOnlyList<RadarGrenadeViewModel> ordered)
    {
        if (ordered.Count == 0)
        {
            target.Clear();
            return;
        }

        var orderedSet = new HashSet<RadarGrenadeViewModel>(ordered);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!orderedSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            var desired = ordered[i];
            if (i < target.Count && ReferenceEquals(target[i], desired))
            {
                continue;
            }

            var existingIndex = target.IndexOf(desired);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, desired);
            }
        }
    }

    private static void SyncDroppedDefusers(ObservableCollection<RadarDroppedDefuserViewModel> target, IReadOnlyList<RadarDroppedDefuserViewModel> ordered)
    {
        if (ordered.Count == 0)
        {
            target.Clear();
            return;
        }

        var orderedSet = new HashSet<RadarDroppedDefuserViewModel>(ordered);
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!orderedSet.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int i = 0; i < ordered.Count; i++)
        {
            var desired = ordered[i];
            if (i < target.Count && ReferenceEquals(target[i], desired))
            {
                continue;
            }

            var existingIndex = target.IndexOf(desired);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, i);
            }
            else
            {
                target.Insert(i, desired);
            }
        }
    }

    private void UpdateInterpolationDelay(long heartbeat, DateTime sampleTimeUtc)
    {
        if (heartbeat == _lastInterpolatedHeartbeat)
            return;

        if (_lastSampleTimeUtc != default)
        {
            var sampleIntervalSeconds = (sampleTimeUtc - _lastSampleTimeUtc).TotalSeconds;
            if (sampleIntervalSeconds > 0)
            {
                var clamped = Math.Clamp(sampleIntervalSeconds, 0.01, MaxInterpolationDelaySeconds);
                _interpolationDelaySeconds = _interpolationDelaySeconds <= 0
                    ? clamped
                    : (_interpolationDelaySeconds * 0.8) + (clamped * 0.2);
            }
        }

        _lastSampleTimeUtc = sampleTimeUtc;
        _lastInterpolatedHeartbeat = heartbeat;
    }

    private static bool ShouldSnapInterpolation(double fromX, double fromY, double toX, double toY)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        return (dx * dx) + (dy * dy) >= TeleportSnapDistancePixels * TeleportSnapDistancePixels;
    }

    private static string GetGrenadeKey(GsiGrenade grenade, string smokeKey)
    {
        if (grenade.Type == "smoke")
        {
            return smokeKey;
        }

        return grenade.Id;
    }

    private static string GetSmokeKey(GsiGrenade grenade)
    {
        return grenade.Id;
    }

    private void TryAddDetonationEffect(RadarGrenadeViewModel grenadeVm, DateTime nowUtc)
    {
        IBrush? fill = grenadeVm.Type switch
        {
            "flashbang" => CreateDetonationBrush(Color.Parse("#F5F5F5")),
            "frag" => CreateDetonationBrush(Color.Parse("#FF5353")),
            _ => null
        };

        if (fill == null)
            return;

        Detonations.Add(new RadarDetonationViewModel(
            grenadeVm.CurrentSampleCanvasX - 18.0,
            grenadeVm.CurrentSampleCanvasY - 18.0,
            fill,
            nowUtc.AddSeconds(DetonationDurationSeconds)));
    }

    private static IBrush CreateDetonationBrush(Color color)
    {
        return new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops = new GradientStops
            {
                new GradientStop(color, 0.0),
                new GradientStop(Color.FromArgb((byte)(color.A * 0.7), color.R, color.G, color.B), 0.45),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0)
            }
        };
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
