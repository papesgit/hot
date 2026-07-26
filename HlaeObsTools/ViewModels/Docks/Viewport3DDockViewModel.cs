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
    private bool _gizmoDragActive;
    private CurveEditorDockViewModel? _curveEditor;
    private CampathSequenceViewModel? _sequence;
    private readonly string _campathSyncDirectory;
    private readonly DispatcherTimer _campathSyncTimer;
    private bool _campathSyncPending;
    private bool _campathSyncTimerScheduled;
    private DateTime _lastCampathSyncUtc = DateTime.MinValue;
    private readonly Dictionary<int, ViewportPlayerStatus> _retainedDeadPlayerStatusesBySlot = new();
    private string? _lastPlayerStatusMapName;
    private int _lastPlayerStatusRoundNumber = -1;

    private static readonly string[] AltBindLabels = { "Q", "E", "R", "T", "Z" };

    public event Action<IReadOnlyList<ViewportPin>>? PinsUpdated;
    public event Action<IReadOnlyList<ViewportPlayerStatus>>? PlayerStatusesUpdated;
    public event Action<CampathSample?>? SequencerPreviewChanged;
    public bool IsSequencerPiloting => _sequence?.IsPiloting == true;
    public bool IsSequencerPlaying => _sequence?.IsPlaying == true;

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
        _campathSyncTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(175)
        };
        _campathSyncTimer.Tick += OnCampathSyncTimerTick;

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
    public bool HasSequencerPossession => _sequence?.Possession.Kind != SequencerPossessionKind.None;

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
        _campathSyncTimer.Stop();
        _campathSyncTimer.Tick -= OnCampathSyncTimerTick;
        if (_gsiServer != null)
            _gsiServer.GameStateUpdated -= OnGameStateUpdated;
        _settings.PropertyChanged -= OnViewportSettingsChanged;
        CampathEditor.PropertyChanged -= OnCampathEditorChanged;
        CampathEditor.Keyframes.CollectionChanged -= OnCampathKeyframesChanged;
        if (_sequence != null)
        {
            _sequence.PreviewChanged -= OnSequencePreviewChanged;
            _sequence.CameraKeyRequested -= OnSequenceCameraKeyRequested;
            _sequence.PropertyChanged -= OnSequencePropertyChanged;
            _sequence.PlayheadScrubCompleted -= OnPlayheadScrubCompleted;
        }
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

    private void AddKeyframeFromViewport(CampathEditorViewModel? requestedEditor = null,
        IReadOnlyList<string>? requestedChannelIds = null)
    {
        var state = CampathStateProvider?.Invoke();
        if (state == null)
            return;

        var targetEditor = requestedEditor ?? _sequence?.GetDirectlyPossessedCamera()?.Editor ?? CampathEditor;
        targetEditor.BeginHistoryTransaction();
        try
        {
            if (targetEditor.IsCurveMode && _curveEditor != null
                && ReferenceEquals(targetEditor, _curveEditor.CampathEditor))
            {
                IEnumerable<CampathCurveChannel> channels = requestedChannelIds == null
                    ? _curveEditor.Document.Channels
                    : _curveEditor.Document.Channels
                        .Where(channel => requestedChannelIds.Contains(channel.Id))
                        .ToList();
                _curveEditor.AddKeys(channels, useEvaluatedValue: false);
            }
            else
            {
                targetEditor.AddKeyframe(
                    targetEditor.PlayheadTime,
                    state.Value.RawPosition,
                    state.Value.RawOrientation,
                    state.Value.RawFov);
            }
        }
        finally
        {
            targetEditor.CommitHistoryTransaction();
            _sequence?.EndPilotingAndEvaluate();
        }
    }

    public void SetCurveEditor(CurveEditorDockViewModel curveEditor) => _curveEditor = curveEditor;

    public void SetSequence(CampathSequenceViewModel sequence)
    {
        if (_sequence != null)
        {
            _sequence.PreviewChanged -= OnSequencePreviewChanged;
            _sequence.CameraKeyRequested -= OnSequenceCameraKeyRequested;
            _sequence.PropertyChanged -= OnSequencePropertyChanged;
            _sequence.PlayheadScrubCompleted -= OnPlayheadScrubCompleted;
        }
        _sequence = sequence;
        _sequence.UseExternalPlaybackTicks = true;
        _sequence.PreviewChanged += OnSequencePreviewChanged;
        _sequence.CameraKeyRequested += OnSequenceCameraKeyRequested;
        _sequence.PropertyChanged += OnSequencePropertyChanged;
        _sequence.PlayheadScrubCompleted += OnPlayheadScrubCompleted;
    }

    private void OnSequencePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathSequenceViewModel.IsPlaying) && IsHlaeSyncActive())
        {
            _ = _webSocketClient?.SendExecCommandAsync(
                _sequence?.IsPlaying == true ? "demo_resume" : "demo_pause");
        }
        if (e.PropertyName is nameof(CampathSequenceViewModel.ContentEnd)
            or nameof(CampathSequenceViewModel.PlaybackEnd))
            RequestCampathSync();
    }

    private void OnPlayheadScrubCompleted()
    {
        if (!IsHlaeSyncActive() || _sequence == null)
            return;
        var offset = _sequence.Cameras.FirstOrDefault()?.Editor.TimeOffset ?? 0.0;
        var seconds = _sequence.PlayheadTime + offset;
        var command = $"mirv_skip time toGame {seconds.ToString("G", CultureInfo.InvariantCulture)}";
        _ = _webSocketClient?.SendExecCommandAsync(command);
    }

    private void OnSequencePreviewChanged(CampathSample? sample) => SequencerPreviewChanged?.Invoke(sample);

    private void OnSequenceCameraKeyRequested(Guid cameraId, IReadOnlyList<string>? channelIds)
    {
        var editor = _sequence?.Cameras.FirstOrDefault(camera => camera.Id == cameraId)?.Editor;
        if (editor != null)
            AddKeyframeFromViewport(editor, channelIds);
    }

    public void BeginSequencerPiloting() => _sequence?.BeginPiloting();
    public CampathDofSettings GetSequencerDepthOfField() =>
        _sequence?.EvaluatePossession(_sequence.PlayheadTime)?.Dof ?? CampathDofSettings.Default;
    public void AdvanceSequencerPlayback(double delta) => _sequence?.AdvancePlayback(delta);
    public void SetSequencerExternalPlaybackTicks(bool value)
    {
        if (_sequence != null)
            _sequence.UseExternalPlaybackTicks = value;
    }

    private void OnViewportSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Viewport3DSettings.ViewportCampathMode) && !_settings.ViewportCampathMode)
        {
            _sequence?.StopPlayback();
        }
        else if (e.PropertyName == nameof(Viewport3DSettings.ViewportCampathSyncEnabled))
        {
            if (_settings.ViewportCampathSyncEnabled)
                RequestCampathSync();
            else
            {
                _campathSyncTimer.Stop();
                _campathSyncPending = false;
                _campathSyncTimerScheduled = false;
            }
        }
    }

    private void OnCampathEditorChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathEditorViewModel.SelectedKeyframe))
            _removeSelectedKeyframeCommand.RaiseCanExecuteChanged();

        if (!IsHlaeSyncActive())
            return;

        if (e.PropertyName == nameof(CampathEditorViewModel.IsTimeDragActive))
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

        if (e.PropertyName is nameof(CampathKeyframeViewModel.Time)
            or nameof(CampathKeyframeViewModel.Position)
            or nameof(CampathKeyframeViewModel.Rotation)
            or nameof(CampathKeyframeViewModel.Fov))
        {
            RequestCampathSync();
        }
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
        {
            _campathSyncTimer.Stop();
            _campathSyncPending = false;
            _campathSyncTimerScheduled = false;
            return;
        }

        _campathSyncPending = true;
        if (CampathEditor.IsHistoryTransactionActive
            || _sequence?.Cameras.Any(camera => camera.Editor.IsHistoryTransactionActive) == true)
        {
            // During a drag, keep feedback live but cap publication at twenty loads per
            // second. Repeated key notifications do not postpone the scheduled update.
            if (_campathSyncTimerScheduled)
                return;
            var elapsed = DateTime.UtcNow - _lastCampathSyncUtc;
            ScheduleCampathSync(Math.Max(1.0, 50.0 - elapsed.TotalMilliseconds));
            return;
        }

        // Outside a drag, use trailing-edge debounce so an undo/redo restoration or
        // multi-key command becomes one load of the final document.
        ScheduleCampathSync(175.0);
    }

    private void ScheduleCampathSync(double delayMilliseconds)
    {
        _campathSyncTimer.Stop();
        _campathSyncTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
        _campathSyncTimer.Start();
        _campathSyncTimerScheduled = true;
    }

    private void OnCampathSyncTimerTick(object? sender, EventArgs e)
    {
        _campathSyncTimer.Stop();
        _campathSyncTimerScheduled = false;
        if (!IsHlaeSyncActive())
        {
            _campathSyncPending = false;
            return;
        }
        if (!_campathSyncPending)
            return;

        _campathSyncPending = false;
        _lastCampathSyncUtc = DateTime.UtcNow;
        if (_sequence?.CanEvaluateForExport() == false
            || _sequence == null && !CampathEditor.CanEvaluate())
        {
            _ = _webSocketClient?.SendExecCommandAsync("mirv_campath clear");
            return;
        }

        Directory.CreateDirectory(_campathSyncDirectory);
        var syncPath = GetSyncPath();
        if (_sequence != null)
            CampathFileIo.Save(syncPath, _sequence);
        else
            CampathFileIo.Save(syncPath, CampathEditor);
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
        const int keepCount = 20;
        var safeDeleteBefore = DateTime.UtcNow - TimeSpan.FromMinutes(2);
        try
        {
            var files = Directory.GetFiles(_campathSyncDirectory, "viewport-campath-*.xml")
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .ToList();

            for (var i = keepCount; i < files.Count; i++)
            {
                if (files[i].LastWriteTimeUtc >= safeDeleteBefore)
                    continue;
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
