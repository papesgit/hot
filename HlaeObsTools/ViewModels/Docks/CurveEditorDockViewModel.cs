using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Viewport3D;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class CurveEditorDockViewModel : Tool, IDisposable
{
    private readonly CampathEditorViewModel _editor;
    private readonly Viewport3DDockViewModel _viewport;
    private CurveEditorViewMode _viewMode;
    private bool _snapEnabled = true;
    private double _snapInterval = 0.1;
    private int _fitAllRequest;
    private int _fitSelectionRequest;
    public CurveEditorDockViewModel(CampathEditorViewModel editor, Viewport3DDockViewModel viewport)
    {
        _editor = editor;
        _viewport = viewport;
        Id = "CurveEditor";
        Title = "Curve Editor";
        CanFloat = true;
        CanPin = true;
        CanClose = false;
        CampathPathConversion.EnsureStandardChannels(Document);
        AllChannels = new CurveChannelGroupViewModel("All", Document.Channels.ToList());
        AllChannelGroups.Add(AllChannels);
        foreach (var group in Document.Channels.GroupBy(channel => channel.Group))
            ChannelGroups.Add(new CurveChannelGroupViewModel(group.Key, group.ToList()));
        _editor.PropertyChanged += OnEditorPropertyChanged;
        HookCurveDocument();
    }

    public CampathCurveDocument Document => _editor.CurveDocument;
    public IReadOnlyList<CampathCurveChannel> Channels => Document.Channels;
    public ObservableCollection<CurveChannelGroupViewModel> ChannelGroups { get; } = new();
    public ObservableCollection<CurveChannelGroupViewModel> AllChannelGroups { get; } = new();
    public CurveChannelGroupViewModel AllChannels { get; }
    public IReadOnlyList<CurveEditorViewMode> ViewModes { get; } = Enum.GetValues<CurveEditorViewMode>();
    public CampathEditorViewModel CampathEditor => _editor;
    public bool IsCurveMode => _editor.IsCurveMode;
    public CurveEditorViewMode ViewMode { get => _viewMode; set => SetProperty(ref _viewMode, value); }
    public bool SnapEnabled { get => _snapEnabled; set => SetProperty(ref _snapEnabled, value); }
    public double SnapInterval { get => _snapInterval; set => SetProperty(ref _snapInterval, Math.Max(0.001, value)); }
    public int FitAllRequest { get => _fitAllRequest; private set => SetProperty(ref _fitAllRequest, value); }
    public int FitSelectionRequest { get => _fitSelectionRequest; private set => SetProperty(ref _fitSelectionRequest, value); }
    public void RequestFitAll() => FitAllRequest++;
    public void RequestFitSelection() => FitSelectionRequest++;
    public void ApplyFreecamPreviewAtTime(double time) => _viewport.ApplyFreecamPreviewAtTime(time);
    public void EndFreecamPreview() => _viewport.EndFreecamPreview();
    public void BeginCampathPreviewOverride() => _viewport.BeginCampathPreviewOverride();
    public void EndCampathPreviewOverride() => _viewport.EndCampathPreviewOverride();
    public void NotifyPlayheadDragEnded() => _viewport.NotifyPlayheadDragEnded();

    public void AddKey(CampathCurveChannel channel, bool useEvaluatedValue) => AddKeys([channel], useEvaluatedValue);
    public void SoloChannel(CampathCurveChannel channel)
    {
        foreach (var candidate in Document.Channels)
            candidate.IsVisible = ReferenceEquals(candidate, channel);
    }
    public void AddKeys(IEnumerable<CampathCurveChannel> channels, bool useEvaluatedValue)
    {
        if (!IsCurveMode)
            return;

        _editor.BeginHistoryTransaction();
        try
        {
            var time = _editor.PlayheadTime;
            var liveState = useEvaluatedValue ? null : _viewport.CampathStateProvider?.Invoke();
            var liveEuler = liveState.HasValue ? QuaternionToEuler(liveState.Value.RawOrientation) : default;
            foreach (var channel in channels)
            {
                var value = useEvaluatedValue && channel.Keys.Count > 0
                    ? channel.Evaluate(time)
                    : GetCurrentValue(channel.Id, liveState, liveEuler, time);
                var existing = channel.Keys.FirstOrDefault(key => Math.Abs(key.Time - time) < 0.0001);
                if (channel.Id.StartsWith("rotation.", StringComparison.Ordinal))
                    value = UnwrapValue(channel, time, value, existing);
                if (existing != null) existing.Value = value;
                else
                {
                    var key = new CampathCurveKey
                    {
                        Time = time, Value = value, Selected = true,
                        Interpolation = channel.Id == "dof.enabled" ? CurveInterpolationMode.Constant : CurveInterpolationMode.Bezier
                    };
                    var index = 0; while (index < channel.Keys.Count && channel.Keys[index].Time < time) index++;
                    channel.Keys.Insert(index, key);
                }
            }
            AutoTangents();
            _editor.NotifyCurveDocumentChanged();
        }
        finally
        {
            _editor.CommitHistoryTransaction();
        }
    }

    private double GetCurrentValue(string id, ViewportFreecamState? state,
        (double pitch, double yaw, double roll) euler, double time)
    {
        var dof = _editor.CurrentDofSettings;
        return id switch
        {
            "position.x" when state.HasValue => state.Value.RawPosition.X,
            "position.y" when state.HasValue => state.Value.RawPosition.Y,
            "position.z" when state.HasValue => state.Value.RawPosition.Z,
            "rotation.pitch" when state.HasValue => euler.pitch,
            "rotation.yaw" when state.HasValue => euler.yaw,
            "rotation.roll" when state.HasValue => euler.roll,
            "fov" when state.HasValue => state.Value.RawFov,
            "dof.enabled" => _viewport.Viewport3DSettings.ViewportCampathDofEnabled ? 1 : 0,
            "dof.nearBlurry" => dof.NearBlurry,
            "dof.nearCrisp" => dof.NearCrisp,
            "dof.farCrisp" => dof.FarCrisp,
            "dof.farBlurry" => dof.FarBlurry,
            "dof.maxBlur" => dof.MaxBlurSize,
            "dof.radiusScale" => dof.RadiusScale,
            _ => Document.Find(id) is { Keys.Count: > 0 } channel ? channel.Evaluate(time) : 0
        };
    }

    private static double UnwrapValue(CampathCurveChannel channel, double time, double value, CampathCurveKey? existing)
    {
        double? reference = existing?.Value;
        if (!reference.HasValue && channel.Keys.Count > 0)
            reference = channel.Evaluate(time);
        if (reference.HasValue)
            value += Math.Round((reference.Value - value) / 360.0) * 360.0;
        return value;
    }

    private void HookCurveDocument()
    {
        foreach (var channel in Document.Channels)
        {
            channel.Keys.CollectionChanged += OnCurveKeysChanged;
            foreach (var key in channel.Keys) key.PropertyChanged += OnCurveKeyChanged;
        }
    }

    private void OnCurveKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (CampathCurveKey key in e.OldItems) key.PropertyChanged -= OnCurveKeyChanged;
        if (e.NewItems != null) foreach (CampathCurveKey key in e.NewItems) key.PropertyChanged += OnCurveKeyChanged;
        _editor.NotifyCurveDocumentChanged();
    }

    private void OnCurveKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathCurveKey.Selected)) return;
        _editor.NotifyCurveDocumentChanged();
    }

    private void AutoTangents() => CampathPathConversion.AutoTangents(Document);

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathEditorViewModel.IsCurveMode))
            OnPropertyChanged(nameof(IsCurveMode));
    }

    private static (double pitch, double yaw, double roll) QuaternionToEuler(Quaternion q)
    {
        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, q));
        var yaw = Math.Atan2(forward.Y, forward.X);
        var pitch = -Math.Asin(Math.Clamp(forward.Z, -1f, 1f));
        var right = new Vector3((float)Math.Sin(yaw), (float)-Math.Cos(yaw), 0);
        var baseUp = Vector3.Normalize(Vector3.Cross(right, forward));
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
        var roll = Math.Atan2(Vector3.Dot(Vector3.Cross(baseUp, up), forward), Vector3.Dot(baseUp, up));
        const double radToDeg = 180.0 / Math.PI;
        return (pitch * radToDeg, yaw * radToDeg, roll * radToDeg);
    }

    public void Dispose()
    {
        _editor.PropertyChanged -= OnEditorPropertyChanged;
        foreach (var channel in Document.Channels)
        {
            channel.Keys.CollectionChanged -= OnCurveKeysChanged;
            foreach (var key in channel.Keys) key.PropertyChanged -= OnCurveKeyChanged;
        }
    }
}

public sealed class CurveChannelGroupViewModel : ViewModelBase
{
    private bool _updating;

    public CurveChannelGroupViewModel(string name, IReadOnlyList<CampathCurveChannel> channels)
    {
        Name = name;
        Channels = channels;
        foreach (var channel in channels) channel.PropertyChanged += OnChannelChanged;
    }

    public string Name { get; }
    public IReadOnlyList<CampathCurveChannel> Channels { get; }
    public bool IsVisible
    {
        get => Channels.Any(channel => channel.IsVisible);
        set
        {
            _updating = true;
            foreach (var channel in Channels) channel.IsVisible = value;
            _updating = false;
            OnPropertyChanged();
        }
    }

    private void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_updating && e.PropertyName == nameof(CampathCurveChannel.IsVisible))
            OnPropertyChanged(nameof(IsVisible));
    }
}
