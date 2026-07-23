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
    private bool _rebuildingCurves;
    private bool _independentCurveEdits;

    private static readonly (string id, string name, string group, string color)[] Definitions =
    [
        ("position.x", "X", "Position", "#F05A5A"), ("position.y", "Y", "Position", "#62C96B"),
        ("position.z", "Z", "Position", "#5C8FF0"), ("rotation.pitch", "Pitch", "Rotation", "#E68A45"),
        ("rotation.yaw", "Yaw", "Rotation", "#AF6BE8"), ("rotation.roll", "Roll", "Rotation", "#47C6CE"),
        ("fov", "FOV", "Camera", "#F1D65C"), ("dof.enabled", "Enabled", "DOF", "#F18AB8"),
        ("dof.nearBlurry", "Near blurry", "DOF", "#EF6AA8"),
        ("dof.nearCrisp", "Near crisp", "DOF", "#D981B5"), ("dof.farCrisp", "Far crisp", "DOF", "#67B7E8"),
        ("dof.farBlurry", "Far blurry", "DOF", "#438BC7"), ("dof.maxBlur", "Max blur", "DOF", "#C9A65C"),
        ("dof.radiusScale", "Radius scale", "DOF", "#8FCB71")
    ];

    public CurveEditorDockViewModel(CampathEditorViewModel editor, Viewport3DDockViewModel viewport)
    {
        _editor = editor;
        _viewport = viewport;
        Id = "CurveEditor";
        Title = "Curve Editor";
        CanFloat = true;
        CanPin = true;
        CanClose = false;
        foreach (var definition in Definitions)
            if (Document.Find(definition.id) == null)
                Document.Channels.Add(new CampathCurveChannel { Id = definition.id, Name = definition.name, Group = definition.group, Color = definition.color });
        AllChannels = new CurveChannelGroupViewModel("All", Document.Channels.ToList());
        AllChannelGroups.Add(AllChannels);
        foreach (var group in Document.Channels.GroupBy(channel => channel.Group))
            ChannelGroups.Add(new CurveChannelGroupViewModel(group.Key, group.ToList()));
        _editor.Keyframes.CollectionChanged += OnKeyframesChanged;
        HookKeys(_editor.Keyframes);
        HookCurveDocument();
        RebuildFromCampath();
    }

    public CampathCurveDocument Document => _editor.CurveDocument;
    public IReadOnlyList<CampathCurveChannel> Channels => Document.Channels;
    public ObservableCollection<CurveChannelGroupViewModel> ChannelGroups { get; } = new();
    public ObservableCollection<CurveChannelGroupViewModel> AllChannelGroups { get; } = new();
    public CurveChannelGroupViewModel AllChannels { get; }
    public IReadOnlyList<CurveEditorViewMode> ViewModes { get; } = Enum.GetValues<CurveEditorViewMode>();
    public CampathEditorViewModel CampathEditor => _editor;
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

    private void OnKeyframesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) _independentCurveEdits = false;
        if (e.OldItems != null) HookKeys(e.OldItems.Cast<CampathKeyframeViewModel>(), false);
        if (e.NewItems != null) HookKeys(e.NewItems.Cast<CampathKeyframeViewModel>());
        RebuildFromCampath();
    }

    private void HookKeys(IEnumerable<CampathKeyframeViewModel> keys, bool hook = true)
    {
        foreach (var key in keys)
            if (hook) key.PropertyChanged += OnSourceKeyChanged; else key.PropertyChanged -= OnSourceKeyChanged;
    }

    private void OnSourceKeyChanged(object? sender, PropertyChangedEventArgs e) => RebuildFromCampath();

    private void RebuildFromCampath()
    {
        if (_independentCurveEdits) return;
        _rebuildingCurves = true;
        foreach (var channel in Document.Channels) channel.Keys.Clear();
        foreach (var key in _editor.Keyframes.OrderBy(k => k.Time))
        {
            var (pitch, yaw, roll) = QuaternionToEuler(key.Rotation);
            Add("position.x", key.Time, key.Position.X); Add("position.y", key.Time, key.Position.Y); Add("position.z", key.Time, key.Position.Z);
            Add("rotation.pitch", key.Time, pitch); Add("rotation.yaw", key.Time, yaw); Add("rotation.roll", key.Time, roll);
            Add("fov", key.Time, key.Fov); Add("dof.nearBlurry", key.Time, key.Dof.NearBlurry);
            Add("dof.enabled", key.Time, key.Dof.Enabled ? 1 : 0);
            Add("dof.nearCrisp", key.Time, key.Dof.NearCrisp); Add("dof.farCrisp", key.Time, key.Dof.FarCrisp);
            Add("dof.farBlurry", key.Time, key.Dof.FarBlurry); Add("dof.maxBlur", key.Time, key.Dof.MaxBlurSize);
            Add("dof.radiusScale", key.Time, key.Dof.RadiusScale);
        }
        UnwrapAngles("rotation.pitch");
        UnwrapAngles("rotation.yaw");
        UnwrapAngles("rotation.roll");
        AutoTangents();
        _rebuildingCurves = false;
        _editor.NotifyCurveDocumentChanged();
        OnPropertyChanged(nameof(Channels));
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
        if (_rebuildingCurves) return;
        _independentCurveEdits = true;
        _editor.NotifyCurveDocumentChanged();
    }

    private void OnCurveKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_rebuildingCurves || e.PropertyName == nameof(CampathCurveKey.Selected)) return;
        _independentCurveEdits = true;
        _editor.NotifyCurveDocumentChanged();
    }

    private void Add(string id, double time, double value) => Document.Find(id)!.Keys.Add(new CampathCurveKey
    {
        Time = time,
        Value = value,
        Interpolation = id == "dof.enabled" ? CurveInterpolationMode.Constant : CurveInterpolationMode.Bezier
    });

    private void UnwrapAngles(string id)
    {
        var keys = Document.Find(id)?.Keys;
        if (keys == null) return;
        for (var i = 1; i < keys.Count; i++)
        {
            while (keys[i].Value - keys[i - 1].Value > 180) keys[i].Value -= 360;
            while (keys[i].Value - keys[i - 1].Value < -180) keys[i].Value += 360;
        }
    }

    private void AutoTangents()
    {
        foreach (var channel in Document.Channels)
            for (var i = 0; i < channel.Keys.Count; i++)
            {
                if (channel.Keys[i].TangentMode != CurveTangentMode.Auto) continue;
                var prev = channel.Keys[Math.Max(0, i - 1)];
                var next = channel.Keys[Math.Min(channel.Keys.Count - 1, i + 1)];
                var slope = Math.Abs(next.Time - prev.Time) < 1e-9 ? 0 : (next.Value - prev.Value) / (next.Time - prev.Time);
                channel.Keys[i].InTangent = channel.Keys[i].OutTangent = slope;
                channel.Keys[i].InWeight = i > 0 ? Math.Max(0.001, (channel.Keys[i].Time - channel.Keys[i - 1].Time) / 3.0) : 0.25;
                channel.Keys[i].OutWeight = i + 1 < channel.Keys.Count ? Math.Max(0.001, (channel.Keys[i + 1].Time - channel.Keys[i].Time) / 3.0) : 0.25;
            }
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
        _editor.Keyframes.CollectionChanged -= OnKeyframesChanged;
        HookKeys(_editor.Keyframes, false);
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
