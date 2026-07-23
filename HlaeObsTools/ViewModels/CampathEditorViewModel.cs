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

public sealed class CampathEditorViewModel : ViewModelBase
{
    private readonly CampathCurve _curve = new();
    private double _playheadTime;
    private double _duration = 20.0;
    private bool _useCubic = true;
    private CampathKeyframeViewModel? _selectedKeyframe;
    private bool _suppressCollectionEvents;
    private bool _isPlaying;
    private bool _lockPreview;
    private bool _previewDuringPlayback = true;
    private double _playbackRate = 1.0;
    private readonly DispatcherTimer _playTimer;
    private DateTime _lastPlayTick;
    private bool _useExternalPlaybackTicks;
    private bool _hold = true;
    private double _timeOffset;
    private bool _timeDragActive;
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
        _playTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _playTimer.Tick += OnPlayTick;
        TogglePlayCommand = new RelayCommand(_ => TogglePlay());
        ClearCommand = new RelayCommand(_ => Clear());
    }

    public ObservableCollection<CampathKeyframeViewModel> Keyframes { get; } = new();

    public CampathCurve Curve => _curve;
    public CampathCurveDocument CurveDocument { get; } = new();

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

    public double Duration
    {
        get => _duration;
        set
        {
            if (value <= 0)
                value = 0.01;
            if (SetProperty(ref _duration, value))
            {
                if (ClampPlayhead())
                    OnPropertyChanged(nameof(PlayheadTime));
            }
        }
    }

    public bool UseCubic
    {
        get => _useCubic;
        set
        {
            if (SetProperty(ref _useCubic, value))
            {
                _curve.PositionInterp = value ? CampathDoubleInterp.Cubic : CampathDoubleInterp.Linear;
                _curve.RotationInterp = value ? CampathQuaternionInterp.SCubic : CampathQuaternionInterp.SLinear;
                _curve.FovInterp = value ? CampathDoubleInterp.Cubic : CampathDoubleInterp.Linear;
                RebuildCurve();
            }
        }
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool LockPreview
    {
        get => _lockPreview;
        set => SetProperty(ref _lockPreview, value);
    }

    public bool PreviewDuringPlayback
    {
        get => _previewDuringPlayback;
        set => SetProperty(ref _previewDuringPlayback, value);
    }

    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            if (value <= 0.0)
                value = 0.01;
            SetProperty(ref _playbackRate, value);
        }
    }

    public bool UseExternalPlaybackTicks
    {
        get => _useExternalPlaybackTicks;
        set => SetProperty(ref _useExternalPlaybackTicks, value);
    }

    public bool Hold
    {
        get => _hold;
        set => SetProperty(ref _hold, value);
    }

    public bool IsTimeDragActive => _timeDragActive;
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
    public double DofRadiusScale { get => CurrentDofSettings.RadiusScale; set { if (DofOverride) SetCurrentDof(_currentDofSettings with { RadiusScale = Math.Clamp(value, 0.25, 10.0) }); } }

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

    public bool CanEvaluate() => CurveDocument.CanEvaluateCamera || _curve.CanEvaluate();

    public CampathSample Evaluate(double time) => CurveDocument.CanEvaluateCamera ? CurveDocument.Evaluate(time) : _curve.Evaluate(time);

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
    }

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

    public ICommand TogglePlayCommand { get; }
    public ICommand ClearCommand { get; }

    public void AddKeyframe(double time, Vector3 position, Quaternion rotation, double fov)
    {
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
        if (SelectedKeyframe == null)
            return;
        BeginHistoryTransaction();
        Keyframes.Remove(SelectedKeyframe);
        SelectedKeyframe = Keyframes.FirstOrDefault();
        CommitHistoryTransaction();
    }

    public void Clear()
    {
        BeginHistoryTransaction();
        Keyframes.Clear();
        SelectedKeyframe = null;
        foreach (var channel in CurveDocument.Channels)
            channel.Keys.Clear();
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

        UseCubic = data.UseCubic;
        Hold = data.Hold;
        TimeOffset = data.TimeOffset;

        SelectedKeyframe = Keyframes.FirstOrDefault(k => k.Selected) ?? Keyframes.FirstOrDefault();
        Duration = GetKeyframeDuration();
        PlayheadTime = SelectedKeyframe?.Time ?? 0.0;
        RebuildCurve();
        foreach (var channel in CurveDocument.Channels)
            channel.Keys.Clear();
        if (data.CurveDocument != null)
        {
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
            Duration = Math.Max(Duration, CurveDocument.Channels.SelectMany(channel => channel.Keys).Select(key => key.Time).DefaultIfEmpty(0).Max());
        }
        NotifyCurveDocumentChanged();
    }

    private EditorHistorySnapshot CaptureHistorySnapshot()
    {
        var legacyKeys = Keyframes.Select(key => new LegacyKeySnapshot(
            key.Time, key.Position, key.Rotation, key.Fov, key.Selected, key.Dof)).ToList();
        var channels = CurveDocument.Channels.Select(channel => new CurveChannelSnapshot(
            channel.Id, channel.Name, channel.Group, channel.Color,
            channel.Keys.Select(key => new CurveKeySnapshot(
                key.Time, key.Value, key.InTangent, key.OutTangent, key.InWeight, key.OutWeight,
                key.Selected, key.Interpolation, key.TangentMode, key.WeightedTangents)).ToList())).ToList();
        return new EditorHistorySnapshot(legacyKeys, channels, Duration);
    }

    private void RestoreHistorySnapshot(EditorHistorySnapshot snapshot)
    {
        _restoringHistory = true;
        try
        {
            foreach (var key in Keyframes)
                key.PropertyChanged -= OnKeyframePropertyChanged;
            _suppressCollectionEvents = true;
            Keyframes.Clear();
            foreach (var key in snapshot.LegacyKeys)
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
            Duration = snapshot.Duration;
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
        if (left.Duration != right.Duration || !left.LegacyKeys.SequenceEqual(right.LegacyKeys)
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

    private sealed record EditorHistorySnapshot(
        List<LegacyKeySnapshot> LegacyKeys, List<CurveChannelSnapshot> Channels, double Duration);
    private sealed record LegacyKeySnapshot(double Time, Vector3 Position, Quaternion Rotation,
        double Fov, bool Selected, CampathDofSettings Dof);
    private sealed record CurveChannelSnapshot(string Id, string Name, string Group, string Color,
        List<CurveKeySnapshot> Keys);
    private sealed record CurveKeySnapshot(double Time, double Value, double InTangent,
        double OutTangent, double InWeight, double OutWeight, bool Selected,
        CurveInterpolationMode Interpolation, CurveTangentMode TangentMode, bool WeightedTangents);

    public void ShiftAllTimes(double delta)
    {
        if (Keyframes.Count == 0)
            return;

        _suppressCollectionEvents = true;
        foreach (var key in Keyframes)
            key.Time += delta;
        _suppressCollectionEvents = false;
        SortByTimeDeferred();
        RebuildCurve();
    }

    public void SetDuration(double newDuration)
    {
        if (newDuration <= 0)
            newDuration = 0.01;

        var currentDuration = GetKeyframeDuration();
        if (currentDuration <= 0.0)
        {
            Duration = newDuration;
            return;
        }

        var scale = newDuration / currentDuration;
        _suppressCollectionEvents = true;
        foreach (var key in Keyframes)
            key.Time *= scale;
        _suppressCollectionEvents = false;

        Duration = newDuration;
        SortByTimeDeferred();
        RebuildCurve();
    }

    public void SnapPlayheadToKeyframe()
    {
        if (SelectedKeyframe == null)
            return;
        PlayheadTime = SelectedKeyframe.Time;
    }

    public double GetKeyframeDuration()
    {
        if (Keyframes.Count == 0)
            return 0.0;
        var min = Keyframes.Min(k => k.Time);
        var max = Keyframes.Max(k => k.Time);
        return Math.Max(0.0, max - min);
    }

    public void StopPlayback()
    {
        if (!IsPlaying)
            return;

        _playTimer.Stop();
        IsPlaying = false;
    }

    private void TogglePlay()
    {
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }

        if (Duration <= 0.0)
            return;

        if (PlayheadTime >= Duration)
            PlayheadTime = 0.0;

        _lastPlayTick = DateTime.UtcNow;
        IsPlaying = true;
        if (!UseExternalPlaybackTicks)
            _playTimer.Start();
    }

    private void OnPlayTick(object? sender, EventArgs e)
    {
        AdvancePlaybackInternal((DateTime.UtcNow - _lastPlayTick).TotalSeconds, updateTimestamp: true);
    }

    public void AdvancePlayback(double deltaSeconds)
    {
        AdvancePlaybackInternal(deltaSeconds, updateTimestamp: false);
    }

    private void AdvancePlaybackInternal(double deltaSeconds, bool updateTimestamp)
    {
        if (!IsPlaying)
            return;

        if (deltaSeconds <= 0.0)
            return;

        if (updateTimestamp)
            _lastPlayTick = DateTime.UtcNow;

        PlayheadTime += deltaSeconds * PlaybackRate;

        if (PlayheadTime >= Duration)
        {
            PlayheadTime = Duration;
            StopPlayback();
        }
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
        {
            if (_timeDragActive)
            {
                RebuildCurve();
                return;
            }

            Dispatcher.UIThread.Post(SortByTimeDeferred);
        }

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

    public void BeginTimeDrag()
    {
        if (_timeDragActive)
            return;

        _timeDragActive = true;
        OnPropertyChanged(nameof(IsTimeDragActive));
    }

    public void EndTimeDrag()
    {
        if (!_timeDragActive)
            return;

        _timeDragActive = false;
        OnPropertyChanged(nameof(IsTimeDragActive));
        SortByTimeDeferred();
        RebuildCurve();
    }

    private bool ClampPlayhead()
    {
        var changed = false;
        if (_playheadTime < 0)
        {
            _playheadTime = 0;
            changed = true;
        }
        if (_playheadTime > Duration)
        {
            _playheadTime = Duration;
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
        set => SetDof(Dof with { MaxBlurSize = value });
    }

    public double DofRadiusScale
    {
        get => Dof.RadiusScale;
        set => SetDof(Dof with { RadiusScale = value });
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
