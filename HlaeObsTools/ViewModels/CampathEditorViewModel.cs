using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using System.Numerics;
using HlaeObsTools.Services.Campaths;

namespace HlaeObsTools.ViewModels;

public sealed record CampathEditorModeOption(CampathEditorMode Mode, string DisplayName);

public sealed class CampathEditorViewModel : ViewModelBase
{
    private readonly CampathCurve _curve = new();
    private double _playheadTime;
    private CameraPathModel _pathModel = CameraPathModel.Curves;
    private ClassicCampathInterpolation _classicInterpolation = ClassicCampathInterpolation.CatmullRom;
    private CampathKeyframeViewModel? _selectedKeyframe;
    private bool _suppressCollectionEvents;
    private bool _hold = true;
    private double _timeOffset;
    private CampathDofSettings _currentDofSettings = CampathDofSettings.Default;
    private int _curveDocumentRevision;
    private bool _isDofEditorOpen;
    private bool _dofOverride;
    private readonly List<EditorHistorySnapshot> _undoHistory = new();
    private readonly List<EditorHistorySnapshot> _redoHistory = new();
    private EditorHistorySnapshot? _pendingHistorySnapshot;
    private int _historyTransactionDepth;
    private bool _restoringHistory;
    private bool _sequencerCurvesUnlocked;

    public CampathEditorViewModel()
    {
        Keyframes.CollectionChanged += OnKeyframesChanged;
        ClearCommand = new RelayCommand(_ => Clear());
        ApplyClassicInterpolation();
        CampathPathConversion.EnsureStandardChannels(CurveDocument);
    }

    public ObservableCollection<CampathKeyframeViewModel> Keyframes { get; } = new();

    public CampathCurve Curve => _curve;
    public CampathCurveDocument CurveDocument { get; } = new();
    public CampathCurveDocument? ActiveCurveDocument => IsCurveMode ? CurveDocument : null;

    public double PlayheadTime
    {
        get => _playheadTime;
        set
        {
            if (SetProperty(ref _playheadTime, value))
            {
                if (ClampPlayhead())
                    OnPropertyChanged();
                OnPropertyChanged(nameof(PlayheadSample));
                RaiseDofPropertiesChanged();
            }
        }
    }

    public CameraPathModel PathModel => _pathModel;
    public ClassicCampathInterpolation ClassicInterpolation => _classicInterpolation;
    public CampathEditorMode EditorMode => PathModel == CameraPathModel.Curves
        ? CampathEditorMode.Curves
        : ClassicInterpolation == ClassicCampathInterpolation.Linear
            ? CampathEditorMode.Linear
            : CampathEditorMode.CatmullRom;
    public IReadOnlyList<CampathEditorMode> EditorModes { get; } = Enum.GetValues<CampathEditorMode>();
    public IReadOnlyList<CampathEditorModeOption> EditorModeOptions { get; } =
    [
        new(CampathEditorMode.Linear, "Linear"),
        new(CampathEditorMode.CatmullRom, "Catmull–Rom"),
        new(CampathEditorMode.Curves, "Editable Curves")
    ];
    public CampathEditorModeOption SelectedEditorModeOption =>
        EditorModeOptions.First(option => option.Mode == EditorMode);
    public bool IsCurveMode => PathModel == CameraPathModel.Curves;
    public bool IsClassicMode => PathModel == CameraPathModel.Classic;
    public bool HasAuthoredKeys => PathModel == CameraPathModel.Curves
        ? CurveDocument.Channels.Any(channel => channel.Keys.Count > 0)
        : Keyframes.Count > 0;

    public void SetEditorMode(CampathEditorMode mode)
    {
        if (mode == EditorMode)
            return;

        BeginHistoryTransaction();
        try
        {
            if (mode == CampathEditorMode.Curves)
            {
                var dofEnabled = CurveDocument.DofEnabled;
                CampathPathConversion.ClassicToCurves(
                    Keyframes.Select(key => key.ToModel()), ClassicInterpolation, CurveDocument);
                CurveDocument.DofEnabled = dofEnabled;
                ClearClassicKeyframes();
                _pathModel = CameraPathModel.Curves;
                NotifyModeChanged();
                NotifyCurveDocumentChanged();
                return;
            }

            var targetInterpolation = mode == CampathEditorMode.Linear
                ? ClassicCampathInterpolation.Linear
                : ClassicCampathInterpolation.CatmullRom;
            if (PathModel == CameraPathModel.Curves)
            {
                ReplaceClassicKeyframes(CampathPathConversion.CurvesToClassic(CurveDocument));
                foreach (var channel in CurveDocument.Channels)
                    channel.Keys.Clear();
                _pathModel = CameraPathModel.Classic;
            }

            _classicInterpolation = targetInterpolation;
            ApplyClassicInterpolation();
            RebuildCurve();
            NotifyModeChanged();
            NotifyCurveDocumentChanged();
        }
        finally
        {
            CommitHistoryTransaction();
        }
    }

    public bool Hold
    {
        get => _hold;
        set => SetProperty(ref _hold, value);
    }

    public bool IsHistoryTransactionActive => _historyTransactionDepth > 0;

    public double TimeOffset
    {
        get => _timeOffset;
        set => SetProperty(ref _timeOffset, value);
    }

    public CampathKeyframeViewModel? SelectedKeyframe
    {
        get => _selectedKeyframe;
        set
        {
            if (SetProperty(ref _selectedKeyframe, value))
            {
                foreach (var key in Keyframes)
                    key.Selected = key == _selectedKeyframe;
            }
        }
    }

    public CampathSample? PlayheadSample
    {
        get
        {
            if (!CanEvaluate())
                return null;
            return Evaluate(PlayheadTime);
        }
    }

    public CampathDofSettings CurrentDofSettings =>
        DofOverride ? _currentDofSettings : PlayheadSample?.Dof ?? _currentDofSettings;
    public int CurveDocumentRevision => _curveDocumentRevision;
    public bool IsDofEditorOpen { get => _isDofEditorOpen; set => SetProperty(ref _isDofEditorOpen, value); }
    public bool DofOverride
    {
        get => _dofOverride;
        set
        {
            if (_dofOverride == value) return;
            var displayedSettings = CurrentDofSettings;
            _dofOverride = value;
            OnPropertyChanged();
            if (value)
                _currentDofSettings = displayedSettings;
            RaiseDofPropertiesChanged();
        }
    }
    public bool SequencerCurvesUnlocked
    {
        get => _sequencerCurvesUnlocked;
        set
        {
            if (SetProperty(ref _sequencerCurvesUnlocked, value))
                OnPropertyChanged(nameof(SequencerCurveLockLabel));
        }
    }
    public string SequencerCurveLockLabel => SequencerCurvesUnlocked ? "EDIT" : "LOCK";
    public double DofNearBlurry { get => CurrentDofSettings.NearBlurry; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { NearBlurry = value }); } }
    public double DofNearCrisp { get => CurrentDofSettings.NearCrisp; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { NearCrisp = value }); } }
    public double DofFarCrisp { get => CurrentDofSettings.FarCrisp; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { FarCrisp = value }); } }
    public double DofFarBlurry { get => CurrentDofSettings.FarBlurry; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { FarBlurry = value }); } }
    public double DofMaxBlurSize { get => CurrentDofSettings.MaxBlurSize; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { MaxBlurSize = Math.Clamp(value, 0.0, 11.0) }); } }
    public double DofRadiusScale { get => CurrentDofSettings.RadiusScale; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { RadiusScale = Math.Clamp(value, 0.25, 5.0) }); } }

    private void SetCurrentDof(CampathDofSettings value)
    {
        if (_currentDofSettings == value) return;
        _currentDofSettings = value;
        RaiseDofPropertiesChanged();
    }

    private void RaiseDofPropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentDofSettings));
        OnPropertyChanged(nameof(DofNearBlurry)); OnPropertyChanged(nameof(DofNearCrisp));
        OnPropertyChanged(nameof(DofFarCrisp)); OnPropertyChanged(nameof(DofFarBlurry));
        OnPropertyChanged(nameof(DofMaxBlurSize)); OnPropertyChanged(nameof(DofRadiusScale));
    }

    public bool CanEvaluate() => PathModel == CameraPathModel.Curves
        ? CurveDocument.CanEvaluateCamera
        : _curve.CanEvaluate();

    public CampathSample Evaluate(double time)
    {
        if (PathModel == CameraPathModel.Curves)
            return CurveDocument.Evaluate(time);
        var sample = _curve.Evaluate(time);
        return new CampathSample(sample.Position, sample.Rotation, sample.Fov, sample.Selected,
            sample.Dof with { Enabled = CurveDocument.DofEnabled });
    }

    public void NotifyCurveDocumentChanged()
    {
        _curveDocumentRevision++;
        OnPropertyChanged(nameof(CurveDocumentRevision));
        OnPropertyChanged(nameof(PlayheadSample));
        RaiseDofPropertiesChanged();
    }

    public void BeginHistoryTransaction()
    {
        if (_restoringHistory) return;
        if (_historyTransactionDepth++ == 0)
            _pendingHistorySnapshot = CaptureHistorySnapshot();
    }

    public void CommitHistoryTransaction()
    {
        if (_restoringHistory || _historyTransactionDepth == 0) return;
        if (--_historyTransactionDepth != 0) return;

        var before = _pendingHistorySnapshot;
        _pendingHistorySnapshot = null;
        if (before == null || HistoryEquals(before, CaptureHistorySnapshot())) return;
        _undoHistory.Add(before);
        if (_undoHistory.Count > 100) _undoHistory.RemoveAt(0);
        _redoHistory.Clear();
        HistoryCommitted?.Invoke();
    }

    public event Action? HistoryCommitted;

    public void Undo()
    {
        if (_historyTransactionDepth != 0 || _undoHistory.Count == 0) return;
        var index = _undoHistory.Count - 1;
        var snapshot = _undoHistory[index];
        _undoHistory.RemoveAt(index);
        _redoHistory.Add(CaptureHistorySnapshot());
        RestoreHistorySnapshot(snapshot);
    }

    public void Redo()
    {
        if (_historyTransactionDepth != 0 || _redoHistory.Count == 0) return;
        var index = _redoHistory.Count - 1;
        var snapshot = _redoHistory[index];
        _redoHistory.RemoveAt(index);
        _undoHistory.Add(CaptureHistorySnapshot());
        RestoreHistorySnapshot(snapshot);
    }

    public ICommand ClearCommand { get; }

    public void AddKeyframe(double time, Vector3 position, Quaternion rotation, double fov)
    {
        if (PathModel != CameraPathModel.Classic)
            return;

        const double timeEpsilon = 0.0001;
        var existing = Keyframes.FirstOrDefault(k => Math.Abs(k.Time - time) <= timeEpsilon);
        if (existing != null)
        {
            existing.Position = position;
            existing.Rotation = rotation;
            existing.Fov = fov;
            RebuildCurve();
            return;
        }

        var vm = new CampathKeyframeViewModel
        {
            Time = time,
            Position = position,
            Rotation = rotation,
            Fov = fov
        };
        InsertKeyframeSorted(vm);
    }

    public void RemoveSelectedKeyframe()
    {
        if (PathModel != CameraPathModel.Classic || SelectedKeyframe == null)
            return;
        BeginHistoryTransaction();
        Keyframes.Remove(SelectedKeyframe);
        SelectedKeyframe = Keyframes.FirstOrDefault();
        CommitHistoryTransaction();
    }

    public void Clear()
    {
        BeginHistoryTransaction();
        if (PathModel == CameraPathModel.Curves)
        {
            foreach (var channel in CurveDocument.Channels)
                channel.Keys.Clear();
        }
        else
        {
            Keyframes.Clear();
            SelectedKeyframe = null;
        }
        RebuildCurve();
        NotifyCurveDocumentChanged();
        CommitHistoryTransaction();
    }

    public void LoadFromData(CampathFileIo.CampathFileData data)
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _pendingHistorySnapshot = null;
        _historyTransactionDepth = 0;
        foreach (var key in Keyframes)
            key.PropertyChanged -= OnKeyframePropertyChanged;
        _suppressCollectionEvents = true;
        Keyframes.Clear();
        foreach (var key in data.Keyframes.OrderBy(k => k.Time))
        {
            Keyframes.Add(new CampathKeyframeViewModel
            {
                Time = key.Time,
                Position = key.Position,
                Rotation = key.Rotation,
                Fov = key.Fov,
                Selected = key.Selected,
                Dof = key.Dof
            });
        }
        _suppressCollectionEvents = false;
        HookKeyframeHandlers();

        _pathModel = data.PathModel;
        _classicInterpolation = data.ClassicInterpolation;
        CurveDocument.DofEnabled = data.DofEnabled;
        ApplyClassicInterpolation();
        NotifyModeChanged();
        Hold = data.Hold;
        TimeOffset = data.TimeOffset;

        SelectedKeyframe = Keyframes.FirstOrDefault(k => k.Selected) ?? Keyframes.FirstOrDefault();
        PlayheadTime = SelectedKeyframe?.Time ?? 0.0;
        RebuildCurve();
        foreach (var channel in CurveDocument.Channels)
            channel.Keys.Clear();
        if (data.CurveDocument != null)
        {
            CurveDocument.DofEnabled = data.CurveDocument.DofEnabled;
            foreach (var source in data.CurveDocument.Channels)
            {
                var target = CurveDocument.Find(source.Id);
                if (target == null)
                {
                    target = new CampathCurveChannel { Id = source.Id, Name = source.Name, Group = source.Group, Color = source.Color };
                    CurveDocument.Channels.Add(target);
                }
                target.Keys.Clear();
                foreach (var key in source.Keys)
                    target.Keys.Add(new CampathCurveKey
                    {
                        Time = key.Time, Value = key.Value, InTangent = key.InTangent, OutTangent = key.OutTangent,
                        InWeight = key.InWeight, OutWeight = key.OutWeight, WeightedTangents = key.WeightedTangents,
                        Interpolation = key.Interpolation, TangentMode = key.TangentMode
                    });
            }
            CampathPathConversion.EnsureStandardChannels(CurveDocument);
        }
        NotifyCurveDocumentChanged();
    }

    internal EditorHistorySnapshot CaptureHistorySnapshot()
    {
        var classicKeys = Keyframes.Select(key => new ClassicKeySnapshot(
            key.Time, key.Position, key.Rotation, key.Fov, key.Selected, key.Dof)).ToList();
        var channels = CurveDocument.Channels.Select(channel => new CurveChannelSnapshot(
            channel.Id, channel.Name, channel.Group, channel.Color,
            channel.Keys.Select(key => new CurveKeySnapshot(
                key.Time, key.Value, key.InTangent, key.OutTangent, key.InWeight, key.OutWeight,
                key.Selected, key.Interpolation, key.TangentMode, key.WeightedTangents)).ToList())).ToList();
        return new EditorHistorySnapshot(PathModel, ClassicInterpolation, classicKeys, channels,
            CurveDocument.DofEnabled);
    }

    internal void RestoreHistorySnapshot(EditorHistorySnapshot snapshot)
    {
        _restoringHistory = true;
        try
        {
            _pathModel = snapshot.PathModel;
            _classicInterpolation = snapshot.ClassicInterpolation;
            ApplyClassicInterpolation();
            foreach (var key in Keyframes)
                key.PropertyChanged -= OnKeyframePropertyChanged;
            _suppressCollectionEvents = true;
            Keyframes.Clear();
            foreach (var key in snapshot.ClassicKeys)
                Keyframes.Add(new CampathKeyframeViewModel
                {
                    Time = key.Time, Position = key.Position, Rotation = key.Rotation,
                    Fov = key.Fov, Selected = key.Selected, Dof = key.Dof
                });
            _suppressCollectionEvents = false;
            HookKeyframeHandlers();
            _selectedKeyframe = Keyframes.FirstOrDefault(key => key.Selected);
            OnPropertyChanged(nameof(SelectedKeyframe));
            RebuildCurve();

            foreach (var channel in CurveDocument.Channels)
                channel.Keys.Clear();
            CurveDocument.DofEnabled = snapshot.CurveDofEnabled;
            foreach (var source in snapshot.Channels)
            {
                var target = CurveDocument.Find(source.Id);
                if (target == null)
                {
                    target = new CampathCurveChannel
                    {
                        Id = source.Id, Name = source.Name, Group = source.Group, Color = source.Color
                    };
                    CurveDocument.Channels.Add(target);
                }
                foreach (var key in source.Keys)
                    target.Keys.Add(new CampathCurveKey
                    {
                        Time = key.Time, Value = key.Value, InTangent = key.InTangent,
                        OutTangent = key.OutTangent, InWeight = key.InWeight, OutWeight = key.OutWeight,
                        Selected = key.Selected, Interpolation = key.Interpolation,
                        TangentMode = key.TangentMode, WeightedTangents = key.WeightedTangents
                    });
            }
            NotifyModeChanged();
            NotifyCurveDocumentChanged();
        }
        finally
        {
            _suppressCollectionEvents = false;
            _restoringHistory = false;
        }
    }

    private static bool HistoryEquals(EditorHistorySnapshot left, EditorHistorySnapshot right)
    {
        if (left.PathModel != right.PathModel || left.ClassicInterpolation != right.ClassicInterpolation
            || left.CurveDofEnabled != right.CurveDofEnabled
            || !left.ClassicKeys.SequenceEqual(right.ClassicKeys)
            || left.Channels.Count != right.Channels.Count) return false;
        for (var i = 0; i < left.Channels.Count; i++)
        {
            var a = left.Channels[i];
            var b = right.Channels[i];
            if (a.Id != b.Id || a.Name != b.Name || a.Group != b.Group || a.Color != b.Color
                || !a.Keys.SequenceEqual(b.Keys)) return false;
        }
        return true;
    }

    internal static bool HistorySnapshotsEqual(EditorHistorySnapshot left, EditorHistorySnapshot right) =>
        HistoryEquals(left, right);

    internal sealed record EditorHistorySnapshot(
        CameraPathModel PathModel, ClassicCampathInterpolation ClassicInterpolation,
        List<ClassicKeySnapshot> ClassicKeys, List<CurveChannelSnapshot> Channels,
        bool CurveDofEnabled);
    internal sealed record ClassicKeySnapshot(double Time, Vector3 Position, Quaternion Rotation,
        double Fov, bool Selected, CampathDofSettings Dof);
    internal sealed record CurveChannelSnapshot(string Id, string Name, string Group, string Color,
        List<CurveKeySnapshot> Keys);
    internal sealed record CurveKeySnapshot(double Time, double Value, double InTangent,
        double OutTangent, double InWeight, double OutWeight, bool Selected,
        CurveInterpolationMode Interpolation, CurveTangentMode TangentMode, bool WeightedTangents);

    private void ApplyClassicInterpolation()
    {
        var cubic = ClassicInterpolation == ClassicCampathInterpolation.CatmullRom;
        _curve.PositionInterp = cubic ? CampathDoubleInterp.Cubic : CampathDoubleInterp.Linear;
        _curve.RotationInterp = cubic ? CampathQuaternionInterp.SCubic : CampathQuaternionInterp.SLinear;
        _curve.FovInterp = cubic ? CampathDoubleInterp.Cubic : CampathDoubleInterp.Linear;
    }

    private void NotifyModeChanged()
    {
        OnPropertyChanged(nameof(PathModel));
        OnPropertyChanged(nameof(ClassicInterpolation));
        OnPropertyChanged(nameof(EditorMode));
        OnPropertyChanged(nameof(SelectedEditorModeOption));
        OnPropertyChanged(nameof(IsCurveMode));
        OnPropertyChanged(nameof(IsClassicMode));
        OnPropertyChanged(nameof(ActiveCurveDocument));
        OnPropertyChanged(nameof(HasAuthoredKeys));
        OnPropertyChanged(nameof(PlayheadSample));
        RaiseDofPropertiesChanged();
    }

    public void SetDofEnabled(bool enabled)
    {
        if (CurveDocument.DofEnabled == enabled)
            return;
        BeginHistoryTransaction();
        CurveDocument.DofEnabled = enabled;
        NotifyCurveDocumentChanged();
        CommitHistoryTransaction();
    }

    private void ClearClassicKeyframes()
    {
        foreach (var key in Keyframes)
            key.PropertyChanged -= OnKeyframePropertyChanged;
        _suppressCollectionEvents = true;
        Keyframes.Clear();
        _suppressCollectionEvents = false;
        _selectedKeyframe = null;
        OnPropertyChanged(nameof(SelectedKeyframe));
        RebuildCurve();
    }

    private void ReplaceClassicKeyframes(IEnumerable<CampathKeyframe> keyframes)
    {
        foreach (var key in Keyframes)
            key.PropertyChanged -= OnKeyframePropertyChanged;
        _suppressCollectionEvents = true;
        Keyframes.Clear();
        foreach (var key in keyframes.OrderBy(key => key.Time))
        {
            Keyframes.Add(new CampathKeyframeViewModel
            {
                Time = key.Time,
                Position = key.Position,
                Rotation = key.Rotation,
                Fov = key.Fov,
                Dof = key.Dof
            });
        }
        _suppressCollectionEvents = false;
        HookKeyframeHandlers();
        _selectedKeyframe = Keyframes.FirstOrDefault();
        if (_selectedKeyframe != null)
            _selectedKeyframe.Selected = true;
        OnPropertyChanged(nameof(SelectedKeyframe));
    }

    public void ShiftAllTimes(double delta)
    {
        if (PathModel == CameraPathModel.Curves)
        {
            var keys = CurveDocument.Channels.SelectMany(channel => channel.Keys).ToList();
            if (keys.Count == 0)
                return;
            foreach (var key in keys)
                key.Time += delta;
            NotifyCurveDocumentChanged();
            return;
        }

        if (Keyframes.Count == 0)
            return;
        _suppressCollectionEvents = true;
        foreach (var key in Keyframes)
            key.Time += delta;
        _suppressCollectionEvents = false;
        SortByTimeDeferred();
        RebuildCurve();
    }

    public void SnapPlayheadToKeyframe()
    {
        if (SelectedKeyframe == null)
            return;
        PlayheadTime = SelectedKeyframe.Time;
    }

    private void OnKeyframesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_suppressCollectionEvents)
            return;

        if (e.OldItems != null)
        {
            foreach (CampathKeyframeViewModel key in e.OldItems)
                key.PropertyChanged -= OnKeyframePropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (CampathKeyframeViewModel key in e.NewItems)
                key.PropertyChanged += OnKeyframePropertyChanged;
        }

        RebuildCurve();
    }


    private void OnKeyframePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressCollectionEvents)
            return;

        if (e.PropertyName == nameof(CampathKeyframeViewModel.Time))
            Dispatcher.UIThread.Post(SortByTimeDeferred);

        RebuildCurve();
    }

    private void HookKeyframeHandlers()
    {
        foreach (var key in Keyframes)
            key.PropertyChanged += OnKeyframePropertyChanged;
    }

    private void SortByTimeDeferred()
    {
        if (_suppressCollectionEvents)
            return;

        if (Keyframes.Count < 2)
            return;

        var ordered = Keyframes.OrderBy(k => k.Time).ToList();
        var changed = false;

        _suppressCollectionEvents = true;
        for (var i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            if (ReferenceEquals(Keyframes[i], target))
                continue;

            var currentIndex = Keyframes.IndexOf(target);
            if (currentIndex < 0)
                continue;

            Keyframes.Move(currentIndex, i);
            changed = true;
        }
        _suppressCollectionEvents = false;

        if (changed)
            RebuildCurve();
    }

    private void InsertKeyframeSorted(CampathKeyframeViewModel vm)
    {
        if (Keyframes.Count == 0)
        {
            Keyframes.Add(vm);
            return;
        }

        var index = 0;
        while (index < Keyframes.Count && Keyframes[index].Time <= vm.Time)
            index++;

        Keyframes.Insert(index, vm);
    }

    private void RebuildCurve()
    {
        _curve.SetKeyframes(Keyframes.Select(k => k.ToModel()));
        OnPropertyChanged(nameof(PlayheadSample));
    }

    private bool ClampPlayhead()
    {
        var changed = false;
        if (_playheadTime < 0)
        {
            _playheadTime = 0;
            changed = true;
        }
        return changed;
    }

    private sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class CampathKeyframeViewModel : ViewModelBase
{
    private double _time;
    private Vector3 _position;
    private Quaternion _rotation = Quaternion.Identity;
    private double _fov = 90.0;
    private bool _selected;
    private CampathDofSettings _dof = CampathDofSettings.Default;

    public double Time
    {
        get => _time;
        set => SetProperty(ref _time, value);
    }

    public Vector3 Position
    {
        get => _position;
        set => SetProperty(ref _position, value);
    }

    public Quaternion Rotation
    {
        get => _rotation;
        set => SetProperty(ref _rotation, value);
    }

    public double Fov
    {
        get => _fov;
        set => SetProperty(ref _fov, value);
    }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public CampathDofSettings Dof
    {
        get => _dof;
        set
        {
            if (SetProperty(ref _dof, value))
                OnPropertyChanged(nameof(DofEnabled));
        }
    }

    public bool DofEnabled
    {
        get => Dof.Enabled;
        set => SetDof(Dof with { Enabled = value });
    }

    public double DofNearBlurry
    {
        get => Dof.NearBlurry;
        set => SetDof(Dof with { NearBlurry = value });
    }

    public double DofNearCrisp
    {
        get => Dof.NearCrisp;
        set => SetDof(Dof with { NearCrisp = value });
    }

    public double DofFarCrisp
    {
        get => Dof.FarCrisp;
        set => SetDof(Dof with { FarCrisp = value });
    }

    public double DofFarBlurry
    {
        get => Dof.FarBlurry;
        set => SetDof(Dof with { FarBlurry = value });
    }

    public double DofMaxBlurSize
    {
        get => Dof.MaxBlurSize;
        set => SetDof(Dof with { MaxBlurSize = Math.Clamp(value, 0.0, 11.0) });
    }

    public double DofRadiusScale
    {
        get => Dof.RadiusScale;
        set => SetDof(Dof with { RadiusScale = Math.Clamp(value, 0.25, 5.0) });
    }

    private void SetDof(CampathDofSettings value)
    {
        if (!SetProperty(ref _dof, value, nameof(Dof)))
            return;

        OnPropertyChanged(nameof(DofEnabled));
        OnPropertyChanged(nameof(DofNearBlurry));
        OnPropertyChanged(nameof(DofNearCrisp));
        OnPropertyChanged(nameof(DofFarCrisp));
        OnPropertyChanged(nameof(DofFarBlurry));
        OnPropertyChanged(nameof(DofMaxBlurSize));
        OnPropertyChanged(nameof(DofRadiusScale));
    }

    public CampathKeyframe ToModel()
    {
        return new CampathKeyframe
        {
            Time = Time,
            Position = Position,
            Rotation = Rotation,
            Fov = Fov,
            Selected = Selected,
            Dof = Dof
        };
    }
}
