using System;
using System.Collections.Generic;
using Avalonia.Threading;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.Services.Viewport3D;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.LiveLink;
using System.Numerics;
using System.Windows.Input;
using System.Threading.Tasks;
using HlaeObsTools.Services.Campaths;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class Viewport3DDockViewModel : Tool, IDisposable
{
    private readonly Viewport3DSettings _settings;
    private readonly FreecamSettings _freecamSettings;
    private HlaeInputSender? _inputSender;
    private readonly HlaeWebSocketClient? _webSocketClient;
    private readonly VideoDisplayDockViewModel? _videoDisplay;
    private readonly GsiServer? _gsiServer;
    private readonly Cs2LiveLinkReceiver? _liveLinkReceiver;
    private long _lastHeartbeat;
    private bool _awaitFreecamRelease;
    private readonly DelegateCommand _addKeyframeFromViewportCommand;
    private readonly DelegateCommand _removeSelectedKeyframeCommand;
    private bool _freecamPreviewActive;
    private bool _campathPreviewOverrideActive;
    private bool _gizmoDragActive;
    private CurveEditorDockViewModel? _curveEditor;
    private readonly string _campathSyncDirectory;
    private readonly Dictionary<int, ViewportPlayerStatus> _retainedDeadPlayerStatusesBySlot = new();
    private string? _lastPlayerStatusMapName;
    private int _lastPlayerStatusRoundNumber = -1;

    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public event Action<IReadOnlyList<ViewportPin>>? PinsUpdated;
    public event Action<IReadOnlyList<ViewportPlayerStatus>>? PlayerStatusesUpdated;

    public Viewport3DDockViewModel(Viewport3DSettings settings, FreecamSettings freecamSettings, CampathEditorViewModel? campathEditor = null, HlaeWebSocketClient? webSocketClient = null, VideoDisplayDockViewModel? videoDisplay = null, GsiServer? gsiServer = null, Cs2LiveLinkReceiver? liveLinkReceiver = null)
    {
        _settings = settings;
        _freecamSettings = freecamSettings;
        _webSocketClient = webSocketClient;
        _videoDisplay = videoDisplay;
        _gsiServer = gsiServer;
        _liveLinkReceiver = liveLinkReceiver;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated += OnGameStateUpdated;
        _settings.PropertyChanged += OnViewportSettingsChanged;

        CampathEditor = campathEditor ?? new CampathEditorViewModel();
        _campathSyncDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HlaeObsTools",
            "campath-sync");

        Title = "3D Viewport";
        CanFloat = true;
        CanPin = true;
        CanClose = false;
        CampathEditor.PropertyChanged += OnCampathEditorChanged;
        CampathEditor.Keyframes.CollectionChanged += OnCampathKeyframesChanged;
        foreach (var keyframe in CampathEditor.Keyframes)
        {
            keyframe.PropertyChanged += OnCampathKeyframePropertyChanged;
        }
        _addKeyframeFromViewportCommand = new DelegateCommand(_ =>
        {
            AddKeyframeFromViewport();
            return Task.CompletedTask;
        });
        _removeSelectedKeyframeCommand = new DelegateCommand(_ =>
        {
            CampathEditor.RemoveSelectedKeyframe();
            return Task.CompletedTask;
        }, _ => CampathEditor.SelectedKeyframe != null);
    }

    public Viewport3DSettings Viewport3DSettings => _settings;
    public FreecamSettings FreecamSettings => _freecamSettings;
    public HlaeInputSender? InputSender => _inputSender;
    public Cs2LiveLinkReceiver? LiveLinkReceiver => _liveLinkReceiver;
    public CampathEditorViewModel CampathEditor { get; }
    public Func<ViewportFreecamState?>? CampathStateProvider { get; set; }

    public ICommand AddKeyframeFromViewportCommand => _addKeyframeFromViewportCommand;

    public ICommand RemoveSelectedKeyframeCommand => _removeSelectedKeyframeCommand;

    public void SetInputSender(HlaeInputSender sender)
    {
        _inputSender = sender;
        OnPropertyChanged(nameof(InputSender));
    }

    public async void HandoffFreecam(ViewportFreecamState state)
    {
        if (_webSocketClient == null)
            return;

        if (state.RawForward.LengthSquared() < 0.0001f)
            return;

        var pitch = state.RawPitch;
        var yaw = state.RawYaw;
        var roll = state.RawRoll;
        var smoothQuat = Quaternion.Normalize(state.SmoothedOrientation);

        var args = new
        {
            posX = state.RawPosition.X,
            posY = state.RawPosition.Y,
            posZ = state.RawPosition.Z,
            pitch,
            yaw,
            roll,
            fov = state.RawFov,
            smoothPosX = state.SmoothedPosition.X,
            smoothPosY = state.SmoothedPosition.Y,
            smoothPosZ = state.SmoothedPosition.Z,
            smoothQuatW = smoothQuat.W,
            smoothQuatX = smoothQuat.X,
            smoothQuatY = smoothQuat.Y,
            smoothQuatZ = smoothQuat.Z,
            smoothFov = state.SmoothedFov,
            speedScalar = state.SpeedScalar,
            walkModeEnabled = state.WalkModeEnabled,
            handheldEffectsEnabled = state.HandheldEffectsEnabled,
            walkVelocityX = state.WalkVelocity.X,
            walkVelocityY = state.WalkVelocity.Y,
            walkVerticalVelocity = state.WalkVerticalVelocity,
            walkOnGround = state.WalkOnGround,
            walkCrouchAmount = state.WalkCrouchAmount,
            walkBobPhase = state.WalkBobPhase,
            walkEffectTime = state.WalkEffectTime,
            walkTargetPitch = state.WalkTargetPitch,
            walkTargetYaw = state.WalkTargetYaw,
            walkTargetFov = state.WalkTargetFov,
            mouseSensitivity = (float)_freecamSettings.MouseSensitivity,
            moveSpeed = (float)_freecamSettings.MoveSpeed,
            sprintMultiplier = (float)_freecamSettings.SprintMultiplier,
            verticalSpeed = (float)_freecamSettings.VerticalSpeed,
            speedAdjustRate = (float)_freecamSettings.SpeedAdjustRate,
            speedMinMultiplier = (float)_freecamSettings.SpeedMinMultiplier,
            speedMaxMultiplier = (float)_freecamSettings.SpeedMaxMultiplier,
            rollSpeed = (float)_freecamSettings.RollSpeed,
            rollSmoothing = (float)_freecamSettings.RollSmoothing,
            leanStrength = (float)_freecamSettings.LeanStrength,
            leanAccelScale = (float)_freecamSettings.LeanAccelScale,
            leanVelocityScale = (float)_freecamSettings.LeanVelocityScale,
            leanMaxAngle = (float)_freecamSettings.LeanMaxAngle,
            leanHalfTime = (float)_freecamSettings.LeanHalfTime,
            clampPitch = _freecamSettings.ClampPitch,
            fovMin = (float)_freecamSettings.FovMin,
            fovMax = (float)_freecamSettings.FovMax,
            fovStep = (float)_freecamSettings.FovStep,
            defaultFov = (float)_freecamSettings.DefaultFov,
            smoothEnabled = _freecamSettings.SmoothEnabled,
            halfVec = (float)_freecamSettings.HalfVec,
            halfRot = (float)_freecamSettings.HalfRot,
            lockHalfRot = (float)_freecamSettings.LockHalfRot,
            lockHalfRotTransition = (float)_freecamSettings.LockHalfRotTransition,
            halfFov = (float)_freecamSettings.HalfFov,
            rotCriticalDamping = _freecamSettings.RotCriticalDamping,
            rotDampingRatio = (float)_freecamSettings.RotDampingRatio,
            walkMoveSpeed = (float)_freecamSettings.WalkMoveSpeed,
            walkMoveAcceleration = (float)_freecamSettings.WalkMoveAcceleration,
            walkMoveDeceleration = (float)_freecamSettings.WalkMoveDeceleration,
            walkRunMultiplier = (float)_freecamSettings.WalkRunMultiplier,
            walkCrouchSpeedMultiplier = (float)_freecamSettings.WalkCrouchSpeedMultiplier,
            walkLookHalfTime = (float)_freecamSettings.WalkLookHalfTime,
            walkFovHalfTime = (float)_freecamSettings.WalkFovHalfTime,
            walkGravity = (float)_freecamSettings.WalkGravity,
            walkJumpSpeed = (float)_freecamSettings.WalkJumpSpeed,
            walkHullRadius = (float)_freecamSettings.WalkHullRadius,
            walkHullHalfHeight = (float)_freecamSettings.WalkHullHalfHeight,
            walkCrouchHullHalfHeight = (float)_freecamSettings.WalkCrouchHullHalfHeight,
            walkCameraTopInset = (float)_freecamSettings.WalkCameraTopInset,
            walkStepHeight = (float)_freecamSettings.WalkStepHeight,
            walkGroundProbe = (float)_freecamSettings.WalkGroundProbe,
            walkMinGroundNormalZ = (float)_freecamSettings.WalkMinGroundNormalZ,
            walkModeDefaultEnabled = _freecamSettings.WalkModeDefaultEnabled,
            handheldDefaultEnabled = _freecamSettings.HandheldDefaultEnabled,
            walkBobAmplitudeZ = (float)_freecamSettings.WalkBobAmplitudeZ,
            walkBobAmplitudeSide = (float)_freecamSettings.WalkBobAmplitudeSide,
            walkBobAmplitudeRoll = (float)_freecamSettings.WalkBobAmplitudeRoll,
            walkBobFrequency = (float)_freecamSettings.WalkBobFrequency,
            handheldShakePosAmplitude = (float)_freecamSettings.HandheldShakePosAmplitude,
            handheldShakeAngAmplitude = (float)_freecamSettings.HandheldShakeAngAmplitude,
            handheldShakeFrequency = (float)_freecamSettings.HandheldShakeFrequency,
            handheldDriftPosAmplitude = (float)_freecamSettings.HandheldDriftPosAmplitude,
            handheldDriftAngAmplitude = (float)_freecamSettings.HandheldDriftAngAmplitude,
            handheldDriftFrequency = (float)_freecamSettings.HandheldDriftFrequency
        };

        await _webSocketClient.SendCommandAsync("freecam_handoff", args);
        _videoDisplay?.RequestFreecamInputLock();
        _awaitFreecamRelease = true;
    }

    public void ReleaseHandoffFreecamInput()
    {
        if (!_awaitFreecamRelease)
            return;

        _awaitFreecamRelease = false;
        _videoDisplay?.RequestFreecamInputRelease();
    }

    public void Dispose()
    {
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _settings.PropertyChanged -= OnViewportSettingsChanged;
        CampathEditor.PropertyChanged -= OnCampathEditorChanged;
        CampathEditor.Keyframes.CollectionChanged -= OnCampathKeyframesChanged;
        foreach (var keyframe in CampathEditor.Keyframes)
        {
            keyframe.PropertyChanged -= OnCampathKeyframePropertyChanged;
        }
    }

    private void OnGameStateUpdated(object? sender, GsiGameState state)
    {
        if (state.Heartbeat == _lastHeartbeat)
            return;
        _lastHeartbeat = state.Heartbeat;

        if (!string.Equals(_lastPlayerStatusMapName, state.MapName, StringComparison.OrdinalIgnoreCase) ||
            _lastPlayerStatusRoundNumber != state.RoundNumber)
        {
            _retainedDeadPlayerStatusesBySlot.Clear();
            _lastPlayerStatusMapName = state.MapName;
            _lastPlayerStatusRoundNumber = state.RoundNumber;
        }

        var pins = new List<ViewportPin>();
        var playerStatusesBySlot = new Dictionary<int, ViewportPlayerStatus>();
        foreach (var p in state.Players)
        {
            if (p == null)
                continue;

            if (p.Slot is >= 0 and <= 9)
            {
                var playerStatus = new ViewportPlayerStatus
                {
                    Slot = p.Slot,
                    IsAlive = p.IsAlive,
                    Health = p.Health,
                    Team = p.Team,
                    Name = p.Name
                };
                playerStatusesBySlot[p.Slot] = playerStatus;
                if (p.IsAlive)
                {
                    _retainedDeadPlayerStatusesBySlot.Remove(p.Slot);
                }
                else
                {
                    _retainedDeadPlayerStatusesBySlot[p.Slot] = playerStatus;
                }
            }

            if (!p.IsAlive)
                continue;

            var label = GetSlotLabel(p.Slot, _settings.UseAltPlayerBinds);
            pins.Add(new ViewportPin
            {
                Position = p.Position,
                Forward = p.Forward,
                Team = p.Team,
                Slot = p.Slot,
                Label = label,
                IsAlive = p.IsAlive
            });
        }

        foreach (var retained in _retainedDeadPlayerStatusesBySlot)
        {
            if (!playerStatusesBySlot.TryGetValue(retained.Key, out var currentStatus) || !currentStatus.IsAlive)
            {
                playerStatusesBySlot[retained.Key] = retained.Value;
            }
        }

        var playerStatuses = playerStatusesBySlot.Values.ToList();

        Dispatcher.UIThread.Post(() =>
        {
            PinsUpdated?.Invoke(pins);
            PlayerStatusesUpdated?.Invoke(playerStatuses);
        });
    }

    private void AddKeyframeFromViewport()
    {
        var state = CampathStateProvider?.Invoke();
        if (state == null)
            return;

        CampathEditor.AddKeyframe(
            CampathEditor.PlayheadTime,
            state.Value.RawPosition,
            state.Value.RawOrientation,
            state.Value.RawFov);
        if (_curveEditor != null)
            _curveEditor.AddKeys(_curveEditor.Document.Channels, useEvaluatedValue: false);
    }

    public void SetCurveEditor(CurveEditorDockViewModel curveEditor) => _curveEditor = curveEditor;

    public void ApplyFreecamPreviewAtTime(double time)
    {
        if (CampathStateProvider == null)
            return;

        var sample = CampathEditor.CanEvaluate()
            ? CampathEditor.Evaluate(time)
            : (CampathSample?)null;
        if (sample == null)
            return;

        _freecamPreviewActive = true;
        PreviewFreecamPose?.Invoke(sample.Value.Position, sample.Value.Rotation, (float)sample.Value.Fov);
    }

    public void EndFreecamPreview()
    {
        if (!_freecamPreviewActive)
            return;

        _freecamPreviewActive = false;
        PreviewFreecamEnded?.Invoke();
    }

    public bool IsFreecamPreviewActive => _freecamPreviewActive;

    public event Action<Vector3, Quaternion, float>? PreviewFreecamPose;
    public event Action? PreviewFreecamEnded;
    public event Action? CampathPreviewOverrideChanged;

    public bool IsCampathPreviewOverrideActive => _campathPreviewOverrideActive;

    public void BeginCampathPreviewOverride()
    {
        if (_campathPreviewOverrideActive)
            return;

        _campathPreviewOverrideActive = true;
        CampathPreviewOverrideChanged?.Invoke();
    }

    public void EndCampathPreviewOverride()
    {
        if (!_campathPreviewOverrideActive)
            return;

        _campathPreviewOverrideActive = false;
        CampathPreviewOverrideChanged?.Invoke();
    }

    private void OnViewportSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.ViewportCampathMode) && !_settings.ViewportCampathMode)
        {
            CampathEditor.StopPlayback();
        }
        else if (e.PropertyName == nameof(Viewport3DSettings.ViewportCampathSyncEnabled))
        {
            if (_settings.ViewportCampathSyncEnabled)
                RequestCampathSync();
        }
    }

    private void OnCampathEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathEditorViewModel.SelectedKeyframe))
            _removeSelectedKeyframeCommand.RaiseCanExecuteChanged();

        if (!IsHlaeSyncActive())
            return;

        if (e.PropertyName == nameof(CampathEditorViewModel.IsPlaying))
        {
            var cmd = CampathEditor.IsPlaying ? "demo_resume" : "demo_pause";
            _ = _webSocketClient?.SendExecCommandAsync(cmd);
        }
        else if (e.PropertyName == nameof(CampathEditorViewModel.IsTimeDragActive))
        {
            if (!CampathEditor.IsTimeDragActive)
                RequestCampathSync();
        }
        else if (e.PropertyName == nameof(CampathEditorViewModel.TimeOffset))
        {
            RequestCampathSync();
        }
        else if (e.PropertyName == nameof(CampathEditorViewModel.CurveDocumentRevision))
        {
            RequestCampathSync();
        }
    }

    private void OnCampathKeyframesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (CampathKeyframeViewModel keyframe in e.OldItems)
            {
                keyframe.PropertyChanged -= OnCampathKeyframePropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (CampathKeyframeViewModel keyframe in e.NewItems)
            {
                keyframe.PropertyChanged += OnCampathKeyframePropertyChanged;
            }
        }

        if (!IsHlaeSyncActive())
            return;

        if (_gizmoDragActive || CampathEditor.IsTimeDragActive)
            return;

        RequestCampathSync();
    }

    private void OnCampathKeyframePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsHlaeSyncActive())
            return;

        if (_gizmoDragActive)
            return;

        if (e.PropertyName == nameof(CampathKeyframeViewModel.Time) && CampathEditor.IsTimeDragActive)
            return;

        if (e.PropertyName is nameof(CampathKeyframeViewModel.Time)
            or nameof(CampathKeyframeViewModel.Position)
            or nameof(CampathKeyframeViewModel.Rotation)
            or nameof(CampathKeyframeViewModel.Fov))
        {
            RequestCampathSync();
        }
    }

    public void NotifyPlayheadDragEnded()
    {
        if (!IsHlaeSyncActive())
            return;

        var seconds = CampathEditor.PlayheadTime + CampathEditor.TimeOffset;
        var cmd = $"mirv_skip time toGame {seconds.ToString("G", CultureInfo.InvariantCulture)}";
        _ = _webSocketClient?.SendExecCommandAsync(cmd);
    }

    public void NotifyGizmoDragActive()
    {
        _gizmoDragActive = true;
    }

    public void NotifyGizmoDragEnded()
    {
        if (_gizmoDragActive)
            _gizmoDragActive = false;

        if (!IsHlaeSyncActive())
            return;

        RequestCampathSync();
    }

    private bool IsHlaeSyncActive()
    {
        return _settings.ViewportCampathMode
               && _settings.ViewportCampathSyncEnabled
               && _webSocketClient != null
               && _webSocketClient.IsConnected;
    }

    private void RequestCampathSync()
    {
        if (!IsHlaeSyncActive())
            return;

        if (!CampathEditor.CanEvaluate())
        {
            _ = _webSocketClient?.SendExecCommandAsync("mirv_campath clear");
            return;
        }

        Directory.CreateDirectory(_campathSyncDirectory);
        var syncPath = GetSyncPath();
        CampathFileIo.Save(syncPath, CampathEditor, includeLegacyCompatibility: false);
        CleanupSyncFiles();
        var cmd = $"mirv_campath load \"{syncPath}\"";
        _ = _webSocketClient?.SendExecCommandAsync(cmd);
    }

    private string GetSyncPath()
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return Path.Combine(_campathSyncDirectory, $"viewport-campath-{stamp}-{id}.xml");
    }

    private void CleanupSyncFiles()
    {
        const int keepCount = 10;
        try
        {
            var files = Directory.GetFiles(_campathSyncDirectory, "viewport-campath-*.xml")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            for (var i = keepCount; i < files.Count; i++)
            {
                try
                {
                    files[i].Delete();
                }
                catch
                {
                    // Ignore cleanup failures.
                }
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    private static string GetSlotLabel(int slot, bool useAlt)
    {
        if (slot < 0 || slot > 9)
            return string.Empty;

        if (useAlt && slot >= 5)
            return AltBindLabels[slot - 5];

        return ((slot + 1) % 10).ToString();
    }

}
