using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using Avalonia.Threading;
using HlaeObsTools.Services.Campaths;

namespace HlaeObsTools.ViewModels;

public enum SequencerPossessionKind
{
    None,
    Camera,
    CameraCuts
}

public readonly record struct SequencerPossession(SequencerPossessionKind Kind, Guid CameraId)
{
    public static SequencerPossession None => new(SequencerPossessionKind.None, Guid.Empty);
    public static SequencerPossession Camera(Guid cameraId) => new(SequencerPossessionKind.Camera, cameraId);
    public static SequencerPossession CameraCuts => new(SequencerPossessionKind.CameraCuts, Guid.Empty);
}

public sealed record SequencerGizmoTarget(
    double Time,
    CampathKeyframeViewModel? ClassicKey,
    IReadOnlyDictionary<string, CampathCurveKey> CurveKeys,
    CampathGizmoAxes TranslationAxes,
    CampathGizmoAxes RotationAxes);

public sealed record SequencerGizmoSelection(
    CampathEditorViewModel Editor,
    IReadOnlyList<SequencerGizmoTarget> Targets,
    CampathGizmoAxes TranslationAxes,
    CampathGizmoAxes RotationAxes,
    double? PivotAnchorTime,
    Quaternion CenterRotation)
{
    public bool HasHandles =>
        TranslationAxes != CampathGizmoAxes.None || RotationAxes != CampathGizmoAxes.None;
}

public sealed class CampathCameraTrackViewModel : ViewModelBase, IDisposable
{
    private string _name;
    private bool _isExpanded;

    public CampathCameraTrackViewModel(string name, CampathEditorViewModel editor)
    {
        Id = Guid.NewGuid();
        _name = name;
        Editor = editor;
        Editor.PropertyChanged += OnEditorChanged;
        Editor.Keyframes.CollectionChanged += OnClassicKeysChanged;
        foreach (var key in Editor.Keyframes)
            key.PropertyChanged += OnClassicKeyChanged;
    }

    public Guid Id { get; }
    public CampathEditorViewModel Editor { get; }
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
    public bool CanExpand => true;

    public event Action? ContentChanged;

    public IReadOnlyList<double> GetSummaryKeyTimes() => Editor.IsCurveMode
        ? Editor.CurveDocument.Channels.SelectMany(channel => channel.Keys)
            .Select(key => key.Time).Distinct().OrderBy(time => time).ToList()
        : Editor.Keyframes.Select(key => key.Time).OrderBy(time => time).ToList();

    public IReadOnlyList<double> GetGroupKeyTimes(string group) => Editor.CurveDocument.Channels
        .Where(channel => string.Equals(channel.Group, group, StringComparison.Ordinal))
        .SelectMany(channel => channel.Keys).Select(key => key.Time)
        .Distinct().OrderBy(time => time).ToList();

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CampathEditorViewModel.EditorMode)
            or nameof(CampathEditorViewModel.CurveDocumentRevision))
        {
            OnPropertyChanged(nameof(CanExpand));
            ContentChanged?.Invoke();
        }
    }

    private void OnClassicKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (CampathKeyframeViewModel key in e.OldItems)
                key.PropertyChanged -= OnClassicKeyChanged;
        if (e.NewItems != null)
            foreach (CampathKeyframeViewModel key in e.NewItems)
                key.PropertyChanged += OnClassicKeyChanged;
        ContentChanged?.Invoke();
    }

    private void OnClassicKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CampathKeyframeViewModel.Selected))
            ContentChanged?.Invoke();
    }

    public void Dispose()
    {
        Editor.PropertyChanged -= OnEditorChanged;
        Editor.Keyframes.CollectionChanged -= OnClassicKeysChanged;
        foreach (var key in Editor.Keyframes)
            key.PropertyChanged -= OnClassicKeyChanged;
    }
}

public sealed class CameraCutSectionViewModel : ViewModelBase
{
    private double _startTime;
    private double _endTime;
    private Guid _cameraId;

    public CameraCutSectionViewModel(Guid cameraId, double startTime, double endTime)
    {
        Id = Guid.NewGuid();
        _cameraId = cameraId;
        _startTime = startTime;
        _endTime = Math.Max(startTime, endTime);
    }

    public Guid Id { get; }
    public Guid CameraId { get => _cameraId; set => SetProperty(ref _cameraId, value); }
    public double StartTime { get => _startTime; set { if (SetProperty(ref _startTime, value) && EndTime < value) EndTime = value; } }
    public double EndTime { get => _endTime; set => SetProperty(ref _endTime, Math.Max(StartTime, value)); }
}

public sealed class CampathSequenceViewModel : ViewModelBase, IDisposable
{
    private readonly DispatcherTimer _playTimer;
    private DateTime _lastPlayTick;
    private double _playheadTime;
    private bool _isPlaying;
    private bool _limitPlaybackToContent;
    private bool _isPiloting;
    private bool _useExternalPlaybackTicks;
    private bool _historyReady;
    private bool _restoringHistory;
    private int _historyTransactionDepth;
    private SequenceSnapshot? _pendingHistorySnapshot;
    private SequenceSnapshot? _currentHistorySnapshot;
    private readonly List<SequenceSnapshot> _undoHistory = new();
    private readonly List<SequenceSnapshot> _redoHistory = new();
    private readonly HashSet<CampathCameraTrackViewModel> _knownCameras = new();
    private CampathCameraTrackViewModel? _selectedCamera;
    private SequencerPossession _possession = SequencerPossession.None;
    private SequencerGizmoSelection? _gizmoSelection;
    private bool _gizmoEditActive;
    private CampathEditorViewModel? _gizmoEditEditor;
    private List<GizmoTargetOrigin>? _gizmoTargetOrigins;
    private Vector3 _gizmoPivotOrigin;
    private Quaternion _gizmoPivotRotationOrigin = Quaternion.Identity;
    private Vector3 _activeGizmoPosition;
    private Quaternion _activeGizmoRotation = Quaternion.Identity;

    public CampathSequenceViewModel(
        CampathEditorViewModel initialCamera,
        CampathEditorMode defaultCameraMode = CampathEditorMode.Curves)
    {
        DefaultCameraMode = defaultCameraMode;
        if (!initialCamera.HasAuthoredKeys)
            initialCamera.SetEditorMode(DefaultCameraMode);
        Cameras.CollectionChanged += OnCamerasChanged;
        CameraCuts.CollectionChanged += OnCutsChanged;
        AddCamera("Camera 1", initialCamera);
        _playTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _playTimer.Tick += OnPlayTick;
        _historyReady = true;
        _currentHistorySnapshot = CaptureHistorySnapshot();
    }

    public ObservableCollection<CampathCameraTrackViewModel> Cameras { get; } = new();
    public ObservableCollection<CameraCutSectionViewModel> CameraCuts { get; } = new();
    public CampathEditorMode DefaultCameraMode { get; set; }
    public SequencerGizmoSelection? GizmoSelection
    {
        get => _gizmoSelection;
        private set => SetProperty(ref _gizmoSelection, value);
    }
    public CampathCameraTrackViewModel? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (value != null && !Cameras.Contains(value))
                return;
            if (SetProperty(ref _selectedCamera, value) && value != null)
                SyncEditorPlayhead(value.Editor, PlayheadTime);
        }
    }
    public SequencerPossession Possession => _possession;
    public SequencerPossessionKind PossessionKind => Possession.Kind;
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }
    public bool LimitPlaybackToContent
    {
        get => _limitPlaybackToContent;
        set => SetProperty(ref _limitPlaybackToContent, value);
    }
    public bool IsPiloting { get => _isPiloting; private set => SetProperty(ref _isPiloting, value); }
    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;
    public bool UseExternalPlaybackTicks
    {
        get => _useExternalPlaybackTicks;
        set
        {
            if (!SetProperty(ref _useExternalPlaybackTicks, value) || !IsPlaying)
                return;
            if (value)
                _playTimer.Stop();
            else
            {
                _lastPlayTick = DateTime.UtcNow;
                _playTimer.Start();
            }
        }
    }

    public void SetGizmoSelection(SequencerGizmoSelection? selection)
    {
        if (selection != null &&
            Cameras.All(camera => !ReferenceEquals(camera.Editor, selection.Editor)))
            selection = null;
        GizmoSelection = selection?.HasHandles == true ? selection : null;
    }

    public CampathGizmoState? GetGizmoState(bool useLocalSpace)
    {
        var selection = GizmoSelection;
        if (selection == null)
            return null;

        // Do not feed curve reevaluation back into a gizmo that is already being
        // dragged. Sparse/partial channels can move the evaluated center by tiny
        // amounts while rotating, which otherwise makes the manipulator jitter.
        if (_gizmoEditActive)
        {
            var activeLocalSpace = selection.Targets.Count == 1 && useLocalSpace &&
                (selection.TranslationAxes is CampathGizmoAxes.None or CampathGizmoAxes.All);
            return new CampathGizmoState(true, _activeGizmoPosition, _activeGizmoRotation,
                activeLocalSpace, selection.TranslationAxes, selection.RotationAxes);
        }

        var samples = selection.Targets.Select(target => (
            Target: target,
            Sample: EvaluateGizmoTarget(selection.Editor, target))).ToList();
        if (samples.Count == 0)
            return null;
        var anchor = selection.PivotAnchorTime is { } anchorTime
            ? samples.FirstOrDefault(item => Math.Abs(item.Target.Time - anchorTime) <= 0.000001)
            : default;
        var hasAnchor = anchor.Target != null;
        var position = hasAnchor
            ? anchor.Sample.Position
            : new Vector3(
                samples.Average(item => item.Sample.Position.X),
                samples.Average(item => item.Sample.Position.Y),
                samples.Average(item => item.Sample.Position.Z));
        var rotation = hasAnchor ? anchor.Sample.Rotation : selection.CenterRotation;
        // A partial position-channel selection represents world-space scalar values.
        // Keeping it in world space prevents a local axis drag from changing hidden channels.
        var effectiveLocalSpace = selection.Targets.Count == 1 && useLocalSpace &&
            (selection.TranslationAxes is CampathGizmoAxes.None or CampathGizmoAxes.All);
        return new CampathGizmoState(true, position, rotation, effectiveLocalSpace,
            selection.TranslationAxes, selection.RotationAxes);
    }

    public void BeginGizmoEdit()
    {
        if (_gizmoEditActive || GizmoSelection == null)
            return;
        var state = GetGizmoState(useLocalSpace: false);
        if (state == null)
            return;
        _gizmoEditActive = true;
        _gizmoEditEditor = GizmoSelection.Editor;
        _gizmoPivotOrigin = state.Value.Position;
        _gizmoPivotRotationOrigin = state.Value.Rotation;
        _activeGizmoPosition = state.Value.Position;
        _activeGizmoRotation = state.Value.Rotation;
        _gizmoTargetOrigins = GizmoSelection.Targets
            .Select(target => new GizmoTargetOrigin(
                target, EvaluateGizmoTarget(GizmoSelection.Editor, target)))
            .ToList();
        BeginHistoryTransaction();
        _gizmoEditEditor.BeginHistoryTransaction();
    }

    public void ApplyGizmoPose(Vector3 position, Quaternion rotation)
    {
        var selection = GizmoSelection;
        if (selection == null)
            return;
        BeginGizmoEdit();

        if (_gizmoTargetOrigins == null)
            return;

        _activeGizmoPosition = position;
        _activeGizmoRotation = Quaternion.Normalize(rotation);
        var changedChannels = new HashSet<CampathCurveChannel>();
        var translation = position - _gizmoPivotOrigin;
        var groupRotation = selection.Targets.Count == 1
            ? Quaternion.Normalize(rotation)
            : Quaternion.Normalize(rotation * Quaternion.Inverse(_gizmoPivotRotationOrigin));
        foreach (var origin in _gizmoTargetOrigins)
        {
            var target = origin.Target;
            var transformedPosition = origin.Sample.Position + translation;
            var transformedRotation = rotation;
            if (selection.Targets.Count > 1)
            {
                if (target.TranslationAxes != CampathGizmoAxes.None)
                {
                    transformedPosition = _gizmoPivotOrigin
                        + Vector3.Transform(origin.Sample.Position - _gizmoPivotOrigin, groupRotation)
                        + translation;
                }
                transformedRotation = Quaternion.Normalize(groupRotation * origin.Sample.Rotation);
            }

            if (target.ClassicKey is { } classic)
            {
                classic.Position = ApplyPositionAxes(
                    origin.Sample.Position, transformedPosition, target.TranslationAxes);
                classic.Rotation = ApplyRotationAxes(
                    origin.Sample.Rotation, transformedRotation, target.RotationAxes);
                continue;
            }

            var euler = QuaternionToEuler(transformedRotation);
            foreach (var (channelId, key) in target.CurveKeys)
            {
                var value = channelId switch
                {
                    "position.x" => transformedPosition.X,
                    "position.y" => transformedPosition.Y,
                    "position.z" => transformedPosition.Z,
                    "rotation.pitch" => ClosestEquivalentAngle(euler.Pitch, key.Value),
                    "rotation.yaw" => ClosestEquivalentAngle(euler.Yaw, key.Value),
                    "rotation.roll" => ClosestEquivalentAngle(euler.Roll, key.Value),
                    _ => key.Value
                };
                if (Math.Abs(key.Value - value) < 1e-9)
                    continue;
                key.Value = value;
                var channel = selection.Editor.CurveDocument.Find(channelId);
                if (channel != null)
                    changedChannels.Add(channel);
            }
        }
        foreach (var channel in changedChannels)
            CampathPathConversion.AutoTangents(channel);
        if (changedChannels.Count > 0)
            selection.Editor.NotifyCurveDocumentChanged();
    }

    public void EndGizmoEdit()
    {
        if (!_gizmoEditActive)
            return;
        _gizmoEditActive = false;
        _gizmoEditEditor?.CommitHistoryTransaction();
        _gizmoEditEditor = null;
        _gizmoTargetOrigins = null;
        CommitHistoryTransaction();
        if (GizmoSelection is { PivotAnchorTime: null, Targets.Count: > 1 } selection)
            GizmoSelection = selection with { CenterRotation = _activeGizmoRotation };
    }

    private static Vector3 ApplyPositionAxes(
        Vector3 current, Vector3 updated, CampathGizmoAxes axes) => new(
        axes.HasFlag(CampathGizmoAxes.X) ? updated.X : current.X,
        axes.HasFlag(CampathGizmoAxes.Y) ? updated.Y : current.Y,
        axes.HasFlag(CampathGizmoAxes.Z) ? updated.Z : current.Z);

    private static Quaternion ApplyRotationAxes(
        Quaternion current, Quaternion updated, CampathGizmoAxes axes)
    {
        if (axes == CampathGizmoAxes.None)
            return current;
        if (axes == CampathGizmoAxes.All)
            return Quaternion.Normalize(updated);
        var oldEuler = QuaternionToEuler(current);
        var newEuler = QuaternionToEuler(updated);
        return EulerToQuaternion(
            axes.HasFlag(CampathGizmoAxes.Y) ? newEuler.Pitch : oldEuler.Pitch,
            axes.HasFlag(CampathGizmoAxes.Z) ? newEuler.Yaw : oldEuler.Yaw,
            axes.HasFlag(CampathGizmoAxes.X) ? newEuler.Roll : oldEuler.Roll);
    }

    private static (double Pitch, double Yaw, double Roll) QuaternionToEuler(Quaternion rotation)
    {
        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
        var yaw = Math.Atan2(forward.Y, forward.X);
        var pitch = -Math.Asin(Math.Clamp(forward.Z, -1f, 1f));
        var right = new Vector3((float)Math.Sin(yaw), (float)-Math.Cos(yaw), 0);
        var baseUp = Vector3.Normalize(Vector3.Cross(right, forward));
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
        var roll = Math.Atan2(Vector3.Dot(Vector3.Cross(baseUp, up), forward), Vector3.Dot(baseUp, up));
        const double toDegrees = 180.0 / Math.PI;
        return (pitch * toDegrees, yaw * toDegrees, roll * toDegrees);
    }

    private static Quaternion EulerToQuaternion(double pitch, double yaw, double roll)
    {
        const double toRadians = Math.PI / 180.0;
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(roll * toRadians));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(pitch * toRadians));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(yaw * toRadians));
        return Quaternion.Normalize(qz * qy * qx);
    }

    private static double ClosestEquivalentAngle(double angle, double reference)
    {
        while (angle - reference > 180.0) angle -= 360.0;
        while (angle - reference < -180.0) angle += 360.0;
        return angle;
    }

    private static CampathSample EvaluateGizmoTarget(
        CampathEditorViewModel editor, SequencerGizmoTarget target) =>
        target.ClassicKey is { } classic
            ? new CampathSample(classic.Position, classic.Rotation, classic.Fov, true, classic.Dof)
            : editor.Evaluate(target.Time);

    private sealed record GizmoTargetOrigin(
        SequencerGizmoTarget Target, CampathSample Sample);

    public double PlayheadTime
    {
        get => _playheadTime;
        set
        {
            var clamped = Math.Max(0.0, value);
            if (_playheadTime.Equals(clamped))
                return;
            _playheadTime = clamped;
            IsPiloting = false;
            if (SelectedCamera != null)
                SyncEditorPlayhead(SelectedCamera.Editor, clamped);
            EvaluatePossessedCamera();
            OnPropertyChanged();
        }
    }

    public double ContentStart => 0.0;
    public double ContentEnd
    {
        get
        {
            var cameraEnd = Cameras.SelectMany(camera => camera.GetSummaryKeyTimes())
                .DefaultIfEmpty(0.0).Max();
            var cutEnd = CameraCuts.Select(cut => cut.EndTime).DefaultIfEmpty(0.0).Max();
            return Math.Max(cameraEnd, cutEnd);
        }
    }
    public double PlaybackEnd => Math.Max(0.01, ContentEnd);

    public bool CanEvaluateForExport()
    {
        if (Cameras.Count == 1)
            return Cameras[0].Editor.CanEvaluate();
        if (CameraCuts.Count == 0)
            return false;
        return CameraCuts.All(cut => cut.CameraId != Guid.Empty
            && Cameras.FirstOrDefault(camera => camera.Id == cut.CameraId)?.Editor.CanEvaluate() == true);
    }

    public event Action<CampathSample?>? PreviewChanged;
    public event Action<Guid, IReadOnlyList<string>?>? CameraKeyRequested;
    public event Action? PlayheadScrubCompleted;

    public CampathCameraTrackViewModel AddCamera(string? name = null, CampathEditorViewModel? editor = null)
    {
        BeginHistoryTransaction();
        if (editor == null)
        {
            editor = new CampathEditorViewModel();
            editor.SetEditorMode(DefaultCameraMode);
        }
        var camera = new CampathCameraTrackViewModel(
            name ?? $"Camera {Cameras.Count + 1}", editor);
        _knownCameras.Add(camera);
        Cameras.Add(camera);
        SelectedCamera ??= camera;
        CommitHistoryTransaction();
        return camera;
    }

    public CampathCameraTrackViewModel? DuplicateCamera(CampathCameraTrackViewModel source)
    {
        var sourceIndex = Cameras.IndexOf(source);
        if (sourceIndex < 0)
            return null;

        var editor = new CampathEditorViewModel();
        editor.RestoreHistorySnapshot(source.Editor.CaptureHistorySnapshot());
        editor.Hold = source.Editor.Hold;
        editor.TimeOffset = source.Editor.TimeOffset;
        editor.SelectedKeyframe = null;
        foreach (var key in editor.CurveDocument.Channels.SelectMany(channel => channel.Keys))
            key.Selected = false;

        BeginHistoryTransaction();
        var duplicate = new CampathCameraTrackViewModel(GetDuplicateCameraName(source.Name), editor);
        _knownCameras.Add(duplicate);
        Cameras.Insert(sourceIndex + 1, duplicate);
        SelectedCamera = duplicate;
        CommitHistoryTransaction();
        return duplicate;
    }

    private string GetDuplicateCameraName(string sourceName)
    {
        var baseName = $"{sourceName} Copy";
        if (Cameras.All(camera => !string.Equals(camera.Name, baseName, StringComparison.OrdinalIgnoreCase)))
            return baseName;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (Cameras.All(camera =>
                    !string.Equals(camera.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    public void LoadFromData(CampathFileIo.CampathSequenceFileData data)
    {
        if (data.Cameras.Count == 0)
            return;

        BeginHistoryTransaction();
        try
        {
            StopPlayback();
            ClearPossession();
            var reusableCamera = Cameras.FirstOrDefault();
            CameraCuts.Clear();
            Cameras.Clear();

            var cameraIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < data.Cameras.Count; index++)
            {
                var source = data.Cameras[index];
                var camera = index == 0 && reusableCamera != null
                    ? reusableCamera
                    : new CampathCameraTrackViewModel(source.Name, new CampathEditorViewModel());
                camera.Name = source.Name;
                source.Campath.TimeOffset = data.TimeOffset;
                camera.Editor.LoadFromData(source.Campath);
                _knownCameras.Add(camera);
                Cameras.Add(camera);
                cameraIds[source.Id] = camera.Id;
            }

            foreach (var cut in data.CameraCuts)
            {
                CameraCuts.Add(new CameraCutSectionViewModel(
                    cameraIds.TryGetValue(cut.CameraId, out var cameraId) ? cameraId : Guid.Empty,
                    cut.StartTime, cut.EndTime));
            }
            SelectedCamera = Cameras.FirstOrDefault();
            PlayheadTime = ContentStart;
        }
        finally
        {
            CommitHistoryTransaction();
        }
    }

    public CameraCutSectionViewModel AddCut(Guid cameraId, double startTime, double endTime)
    {
        BeginHistoryTransaction();
        var cut = new CameraCutSectionViewModel(cameraId, startTime, endTime);
        CameraCuts.Add(cut);
        CommitHistoryTransaction();
        return cut;
    }

    public void RemoveCamera(CampathCameraTrackViewModel camera)
    {
        if (!Cameras.Contains(camera))
            return;
        BeginHistoryTransaction();
        if (Possession == SequencerPossession.Camera(camera.Id))
            ClearPossession();
        foreach (var cut in CameraCuts.Where(cut => cut.CameraId == camera.Id))
            cut.CameraId = Guid.Empty;
        Cameras.Remove(camera);
        CommitHistoryTransaction();
    }

    public void BeginHistoryTransaction()
    {
        if (!_historyReady || _restoringHistory)
            return;
        if (_historyTransactionDepth++ == 0)
            _pendingHistorySnapshot = CaptureHistorySnapshot();
    }

    public void CommitHistoryTransaction()
    {
        if (!_historyReady || _restoringHistory || _historyTransactionDepth == 0)
            return;
        if (--_historyTransactionDepth != 0)
            return;
        var before = _pendingHistorySnapshot;
        _pendingHistorySnapshot = null;
        var after = CaptureHistorySnapshot();
        if (before == null || HistoryEquals(before, after))
        {
            _currentHistorySnapshot = after;
            return;
        }
        PushUndo(before);
        _redoHistory.Clear();
        _currentHistorySnapshot = after;
        RaiseHistoryProperties();
    }

    public void Undo()
    {
        if (_historyTransactionDepth != 0 || _undoHistory.Count == 0)
            return;
        var snapshot = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(CaptureHistorySnapshot());
        RestoreHistorySnapshot(snapshot);
        RaiseHistoryProperties();
    }

    public void Redo()
    {
        if (_historyTransactionDepth != 0 || _redoHistory.Count == 0)
            return;
        var snapshot = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(CaptureHistorySnapshot());
        RestoreHistorySnapshot(snapshot);
        RaiseHistoryProperties();
    }

    public void RequestCameraKey(Guid cameraId, IReadOnlyList<string>? channelIds = null)
    {
        var camera = Cameras.FirstOrDefault(candidate => candidate.Id == cameraId);
        if (camera == null)
            return;
        camera.Editor.PlayheadTime = PlayheadTime;
        BeginHistoryTransaction();
        try
        {
            CameraKeyRequested?.Invoke(cameraId, channelIds);
        }
        finally
        {
            CommitHistoryTransaction();
        }
    }

    public void RequestCameraCut()
    {
        var start = CameraCuts.Select(cut => cut.EndTime).DefaultIfEmpty(0.0).Max();
        AddCut(Guid.Empty, start, start + 5.0);
    }

    public void PossessCamera(Guid cameraId) =>
        SetPossession(Possession == SequencerPossession.Camera(cameraId)
            ? SequencerPossession.None
            : SequencerPossession.Camera(cameraId));

    public void PossessCameraCuts() =>
        SetPossession(Possession.Kind == SequencerPossessionKind.CameraCuts
            ? SequencerPossession.None
            : SequencerPossession.CameraCuts);

    public void ClearPossession() => SetPossession(SequencerPossession.None);

    public void CommitPlayheadScrub() => PlayheadScrubCompleted?.Invoke();

    public void TogglePlayback()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        if (LimitPlaybackToContent && PlayheadTime >= PlaybackEnd)
            PlayheadTime = ContentStart;
        IsPiloting = false;
        _lastPlayTick = DateTime.UtcNow;
        IsPlaying = true;
        if (!UseExternalPlaybackTicks)
            _playTimer.Start();
    }

    public void StopPlayback()
    {
        _playTimer.Stop();
        IsPlaying = false;
    }

    public void BeginPiloting()
    {
        if (GetDirectlyPossessedCamera() == null)
            return;
        StopPlayback();
        IsPiloting = true;
        PreviewChanged?.Invoke(null);
    }

    public void EndPilotingAndEvaluate()
    {
        if (!IsPiloting)
            return;
        IsPiloting = false;
        EvaluatePossessedCamera();
    }

    public CampathSample? EvaluatePossession(double time)
    {
        var camera = GetEvaluatedCamera(time);
        if (camera != null)
            SyncEditorPlayhead(camera.Editor, time);

        return camera?.Editor.CanEvaluate() == true ? camera.Editor.Evaluate(time) : null;
    }

    private static void SyncEditorPlayhead(CampathEditorViewModel editor, double time)
    {
        editor.PlayheadTime = time;
    }

    public CampathCameraTrackViewModel? GetDirectlyPossessedCamera() =>
        Possession.Kind == SequencerPossessionKind.Camera
            ? Cameras.FirstOrDefault(candidate => candidate.Id == Possession.CameraId)
            : null;

    private CampathCameraTrackViewModel? GetEvaluatedCamera(double time)
    {
        if (Possession.Kind == SequencerPossessionKind.Camera)
            return Cameras.FirstOrDefault(candidate => candidate.Id == Possession.CameraId);
        if (Possession.Kind != SequencerPossessionKind.CameraCuts)
            return null;

        var cut = CameraCuts.Where(candidate => candidate.StartTime <= time && time < candidate.EndTime)
            .OrderByDescending(candidate => candidate.StartTime).FirstOrDefault();
        return cut == null ? null : Cameras.FirstOrDefault(candidate => candidate.Id == cut.CameraId);
    }

    private void SetPossession(SequencerPossession possession)
    {
        if (_possession == possession)
            return;
        _possession = possession;
        IsPiloting = false;
        OnPropertyChanged(nameof(Possession));
        OnPropertyChanged(nameof(PossessionKind));
        EvaluatePossessedCamera();
    }

    private void EvaluatePossessedCamera()
    {
        if (IsPiloting)
        {
            PreviewChanged?.Invoke(null);
            return;
        }

        PreviewChanged?.Invoke(EvaluatePossession(PlayheadTime));
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var delta = (now - _lastPlayTick).TotalSeconds;
        _lastPlayTick = now;
        AdvancePlayback(delta);
    }

    public void AdvancePlayback(double delta)
    {
        if (!IsPlaying)
            return;
        if (delta <= 0.0)
            return;
        PlayheadTime += delta;
        if (LimitPlaybackToContent && PlayheadTime >= PlaybackEnd)
        {
            PlayheadTime = PlaybackEnd;
            StopPlayback();
        }
    }

    private void OnCamerasChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (CampathCameraTrackViewModel camera in e.OldItems)
            {
                camera.ContentChanged -= OnTrackContentChanged;
                camera.Editor.HistoryCommitted -= OnEditorHistoryCommitted;
            }
        if (e.NewItems != null)
            foreach (CampathCameraTrackViewModel camera in e.NewItems)
            {
                _knownCameras.Add(camera);
                camera.ContentChanged += OnTrackContentChanged;
                camera.Editor.HistoryCommitted += OnEditorHistoryCommitted;
            }
        if (SelectedCamera == null || !Cameras.Contains(SelectedCamera))
            SelectedCamera = Cameras.FirstOrDefault();
        if (GizmoSelection != null &&
            Cameras.All(camera => !ReferenceEquals(camera.Editor, GizmoSelection.Editor)))
            SetGizmoSelection(null);
        NotifyRangeChanged();
        RecordExternalMutation();
    }

    private void OnCutsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (CameraCutSectionViewModel cut in e.OldItems)
                cut.PropertyChanged -= OnCutChanged;
        if (e.NewItems != null)
            foreach (CameraCutSectionViewModel cut in e.NewItems)
                cut.PropertyChanged += OnCutChanged;
        NotifyRangeChanged();
        EvaluatePossessedCamera();
        RecordExternalMutation();
    }

    private void OnTrackContentChanged()
    {
        if (_gizmoEditActive)
        {
            // Gizmo edits only change the selected transform values. They do not
            // change the sequence range and must not take preview ownership away
            // from a camera the user is actively piloting.
            OnPropertyChanged(nameof(GizmoSelection));
            return;
        }
        NotifyRangeChanged();
        EvaluatePossessedCamera();
        if (!Cameras.Any(camera => camera.Editor.IsHistoryTransactionActive))
            RecordExternalMutation();
    }

    private void OnEditorHistoryCommitted() => RecordExternalMutation();

    private void OnCutChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyRangeChanged();
        EvaluatePossessedCamera();
        RecordExternalMutation();
    }

    private void NotifyRangeChanged()
    {
        OnPropertyChanged(nameof(ContentEnd));
        OnPropertyChanged(nameof(PlaybackEnd));
        IsPiloting = false;
        EvaluatePossessedCamera();
    }

    private void RecordExternalMutation()
    {
        if (!_historyReady || _restoringHistory || _historyTransactionDepth != 0)
            return;
        var after = CaptureHistorySnapshot();
        if (_currentHistorySnapshot == null)
        {
            _currentHistorySnapshot = after;
            return;
        }
        if (HistoryEquals(_currentHistorySnapshot, after))
            return;
        PushUndo(_currentHistorySnapshot);
        _redoHistory.Clear();
        _currentHistorySnapshot = after;
        RaiseHistoryProperties();
    }

    private void PushUndo(SequenceSnapshot snapshot)
    {
        _undoHistory.Add(snapshot);
        if (_undoHistory.Count > 100)
            _undoHistory.RemoveAt(0);
    }

    private void RaiseHistoryProperties()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private SequenceSnapshot CaptureHistorySnapshot() => new(
        Cameras.Select(camera => new CameraTrackSnapshot(
            camera,
            camera.Name,
            camera.Editor.CaptureHistorySnapshot())).ToList(),
        CameraCuts.Select(cut => new CutSnapshot(cut.CameraId, cut.StartTime, cut.EndTime)).ToList());

    private void RestoreHistorySnapshot(SequenceSnapshot snapshot)
    {
        StopPlayback();
        _restoringHistory = true;
        var selectedBeforeRestore = SelectedCamera;
        try
        {
            Cameras.Clear();
            foreach (var state in snapshot.Cameras)
            {
                state.Camera.Name = state.Name;
                state.Camera.Editor.RestoreHistorySnapshot(state.Editor);
                Cameras.Add(state.Camera);
            }

            CameraCuts.Clear();
            foreach (var cut in snapshot.Cuts)
                CameraCuts.Add(new CameraCutSectionViewModel(cut.CameraId, cut.StartTime, cut.EndTime));

            SelectedCamera = selectedBeforeRestore != null && Cameras.Contains(selectedBeforeRestore)
                ? selectedBeforeRestore
                : Cameras.FirstOrDefault();
            if (_possession.Kind == SequencerPossessionKind.Camera
                && Cameras.All(camera => camera.Id != _possession.CameraId))
                _possession = SequencerPossession.None;
            IsPiloting = false;
            OnPropertyChanged(nameof(Possession));
            OnPropertyChanged(nameof(PossessionKind));
        }
        finally
        {
            _restoringHistory = false;
        }
        NotifyRangeChanged();
        _currentHistorySnapshot = CaptureHistorySnapshot();
    }

    private static bool HistoryEquals(SequenceSnapshot left, SequenceSnapshot right)
    {
        if (left.Cameras.Count != right.Cameras.Count || !left.Cuts.SequenceEqual(right.Cuts))
            return false;
        for (var index = 0; index < left.Cameras.Count; index++)
        {
            var a = left.Cameras[index];
            var b = right.Cameras[index];
            if (!ReferenceEquals(a.Camera, b.Camera) || a.Name != b.Name
                || !CampathEditorViewModel.HistorySnapshotsEqual(a.Editor, b.Editor))
                return false;
        }
        return true;
    }

    public void Dispose()
    {
        _playTimer.Stop();
        _playTimer.Tick -= OnPlayTick;
        foreach (var camera in Cameras)
            camera.Editor.HistoryCommitted -= OnEditorHistoryCommitted;
        foreach (var camera in _knownCameras)
            camera.Dispose();
        Cameras.CollectionChanged -= OnCamerasChanged;
        CameraCuts.CollectionChanged -= OnCutsChanged;
    }

    private sealed record SequenceSnapshot(
        List<CameraTrackSnapshot> Cameras,
        List<CutSnapshot> Cuts);
    private sealed record CameraTrackSnapshot(
        CampathCameraTrackViewModel Camera,
        string Name,
        CampathEditorViewModel.EditorHistorySnapshot Editor);
    private sealed record CutSnapshot(Guid CameraId, double StartTime, double EndTime);
}
