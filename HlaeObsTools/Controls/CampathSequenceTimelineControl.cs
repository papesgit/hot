using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Views;

namespace HlaeObsTools.Controls;

public sealed class CampathSequenceTimelineControl : Panel
{
    public static readonly StyledProperty<CampathSequenceViewModel?> SequenceProperty =
        AvaloniaProperty.Register<CampathSequenceTimelineControl, CampathSequenceViewModel?>(nameof(Sequence));
    public static readonly StyledProperty<double> ViewStartProperty =
        AvaloniaProperty.Register<CampathSequenceTimelineControl, double>(nameof(ViewStart), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> SecondsPerPixelProperty =
        AvaloniaProperty.Register<CampathSequenceTimelineControl, double>(nameof(SecondsPerPixel), 0.02,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private const double LabelWidth = 280;
    private const double RulerHeight = 28;
    private const double RowHeight = 27;
    private readonly List<RowHit> _rowHits = new();
    private readonly List<KeyHit> _keyHits = new();
    private readonly List<CutHit> _cutHits = new();
    private readonly HashSet<(Guid CameraId, string Group)> _expandedGroups = new();
    private readonly Dictionary<TimelineKey, double> _keyDragOrigins = new();
    private readonly HashSet<TimelineKey> _selectionBeforeMarquee = new();
    private readonly HashSet<ClassicSelection> _classicSelections = new();
    private readonly HashSet<CampathKeyframeViewModel> _linkedClassicDragKeys = new();
    private readonly HashSet<CampathEditorViewModel> _historyEditors = new();
    private readonly HashSet<ObservableCollection<CampathCurveKey>> _observedCurveKeyCollections = new();
    private readonly HashSet<CampathCurveKey> _observedCurveKeys = new();
    private readonly HashSet<TrackSelection> _selectedCurveTracks = new();
    private readonly List<ValueEditorEntry> _valueEditors = new();
    private readonly List<ModeEditorEntry> _modeEditors = new();
    private readonly TimelineDrawingSurface _drawingSurface;
    private bool _updatingValueEditors;
    private bool _updatingModeEditors;
    private bool _rebuildingValueEditors;
    private bool _curveSelectionRefreshPending;
    private bool _panning;
    private bool _scrubbing;
    private bool _marqueeSelecting;
    private bool _marqueeAdditive;
    private bool _draggingKeys;
    private bool _keyDragActivated;
    private CameraCutSectionViewModel? _selectedCut;
    private CameraCutSectionViewModel? _dragCut;
    private ContextMenu? _dofEnabledMenu;
    private CutDragMode _cutDragMode;
    private double _cutDragPointerTime;
    private double _cutDragStart;
    private double _cutDragEnd;
    private Point _marqueeStart;
    private Rect _marqueeRect;
    private Point _keyDragStart;
    private Point _lastPointer;
    private TrackSelectionAnchor? _trackSelectionAnchor;
    private bool _subscriptionsAttached;

    static CampathSequenceTimelineControl()
    {
        AffectsRender<CampathSequenceTimelineControl>(SequenceProperty, ViewStartProperty, SecondsPerPixelProperty);
        SequenceProperty.Changed.AddClassHandler<CampathSequenceTimelineControl>((control, args) =>
            control.OnSequenceChanged(args));
        ViewStartProperty.Changed.AddClassHandler<CampathSequenceTimelineControl>((control, _) =>
            control._drawingSurface.InvalidateVisual());
        SecondsPerPixelProperty.Changed.AddClassHandler<CampathSequenceTimelineControl>((control, _) =>
            control._drawingSurface.InvalidateVisual());
    }

    public CampathSequenceTimelineControl()
    {
        ClipToBounds = true;
        Focusable = true;
        _drawingSurface = new TimelineDrawingSurface(this) { IsHitTestVisible = true };
        Children.Add(_drawingSurface);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
        KeyDown += OnKeyDown;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_subscriptionsAttached)
            return;
        _subscriptionsAttached = true;
        if (Sequence != null)
        {
            Sequence.PropertyChanged -= OnSequencePropertyChanged;
            Sequence.PropertyChanged += OnSequencePropertyChanged;
        }
        RefreshCurveSelectionSubscriptions();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscriptionsAttached && Sequence != null)
            Sequence.PropertyChanged -= OnSequencePropertyChanged;
        _subscriptionsAttached = false;
        RefreshCurveSelectionSubscriptions();
        base.OnDetachedFromVisualTree(e);
    }

    public CampathSequenceViewModel? Sequence
    {
        get => GetValue(SequenceProperty);
        set => SetValue(SequenceProperty, value);
    }

    public double ViewStart
    {
        get => GetValue(ViewStartProperty);
        set => SetValue(ViewStartProperty, Math.Max(0.0, value));
    }

    public double SecondsPerPixel
    {
        get => GetValue(SecondsPerPixelProperty);
        set => SetValue(SecondsPerPixelProperty, Math.Clamp(value, 0.0001, 10.0));
    }

    public event Action<CampathCameraTrackViewModel>? SaveCameraRequested;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = RulerHeight + RowHeight;
        if (Sequence != null)
        {
            foreach (var camera in Sequence.Cameras)
            {
                height += RowHeight;
                if (!camera.IsExpanded || !camera.CanExpand)
                    continue;
                height += GetUngroupedChannels(camera).Count * RowHeight;
                foreach (var group in GetGroups(camera))
                {
                    height += RowHeight;
                    if (_expandedGroups.Contains((camera.Id, group)))
                        height += camera.Editor.CurveDocument.Channels.Count(channel => channel.Group == group) * RowHeight;
                }
            }
        }
        _drawingSurface.Measure(new Size(Math.Max(availableSize.Width, 640), height));
        foreach (var entry in _modeEditors)
            entry.ComboBox.Measure(new Size(entry.Bounds.Width, entry.Bounds.Height));
        foreach (var entry in _valueEditors)
            entry.Field.Measure(new Size(entry.Bounds.Width, entry.Bounds.Height));
        return new Size(Math.Max(availableSize.Width, 640), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _drawingSurface.Arrange(new Rect(finalSize));
        foreach (var entry in _modeEditors)
            entry.ComboBox.Arrange(entry.Bounds);
        foreach (var entry in _valueEditors)
            entry.Field.Arrange(entry.Bounds);
        return finalSize;
    }

    private void RenderTimeline(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111318")), Bounds);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#1C1F26")), new Rect(0, 0, Bounds.Width, RulerHeight));
        DrawRuler(context);

        _rowHits.Clear();
        _keyHits.Clear();
        _cutHits.Clear();
        var y = RulerHeight;
        DrawCutRow(context, ref y);
        if (Sequence != null)
        {
            foreach (var camera in Sequence.Cameras)
            {
                DrawCameraRow(context, camera, ref y);
                if (!camera.IsExpanded || !camera.CanExpand)
                    continue;
                foreach (var channel in GetUngroupedChannels(camera))
                    DrawChannelRow(context, camera, channel, ref y);
                foreach (var group in GetGroups(camera))
                {
                    DrawGroupRow(context, camera, group, ref y);
                    if (!_expandedGroups.Contains((camera.Id, group)))
                        continue;
                    foreach (var channel in camera.Editor.CurveDocument.Channels.Where(channel => channel.Group == group))
                        DrawChannelRow(context, camera, channel, ref y);
                }
            }
        }

        if (_marqueeSelecting)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(42, 92, 155, 220)), _marqueeRect);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#69A9E0"))), _marqueeRect);
        }

        if (Sequence != null)
        {
            var playheadX = TimeToX(Sequence.PlayheadTime);
            if (playheadX >= LabelWidth && playheadX <= Bounds.Width)
            {
                context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#E15D5D")), 1.5),
                    new Point(playheadX, 0), new Point(playheadX, Bounds.Height));
                var head = new StreamGeometry();
                using var path = head.Open();
                path.BeginFigure(new Point(playheadX - 6, 0), true);
                path.LineTo(new Point(playheadX + 6, 0));
                path.LineTo(new Point(playheadX, 10));
                path.EndFigure(true);
                context.DrawGeometry(new SolidColorBrush(Color.Parse("#F06A6A")), null, head);
            }
        }

        // Keep the label/timeline divider above row backgrounds and timeline content.
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#4A505C")), 1.0),
            new Point(LabelWidth, 0), new Point(LabelWidth, Bounds.Height));
    }

    private void DrawRuler(DrawingContext context)
    {
        var width = Math.Max(0, Bounds.Width - LabelWidth - 8);
        var targetStep = SecondsPerPixel * 90;
        var power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(targetStep, 0.0001))));
        var normalized = targetStep / power;
        var step = (normalized < 2 ? 2 : normalized < 5 ? 5 : 10) * power;
        var first = Math.Ceiling(ViewStart / step) * step;
        for (var time = first; time <= ViewStart + width * SecondsPerPixel; time += step)
        {
            var x = TimeToX(time);
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#555B68"))), new Point(x, RulerHeight - 8), new Point(x, RulerHeight));
            var text = new FormattedText($"{time:0.###}s", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, new SolidColorBrush(Color.Parse("#A7ACB7")));
            context.DrawText(text, new Point(x + 3, 5));
        }
    }

    private void DrawCutRow(DrawingContext context, ref double y)
    {
        DrawRowBackground(context, y, 0);
        DrawText(context, "Camera Cuts", 34, y + 6, "#E0E3E8", true);
        DrawRowActions(context, y, Sequence?.Possession.Kind == SequencerPossessionKind.CameraCuts);
        if (Sequence != null)
        {
            using (context.PushClip(new Rect(LabelWidth + 1, y, Math.Max(0, Bounds.Width - LabelWidth - 1), RowHeight)))
            {
                foreach (var cut in Sequence.CameraCuts)
                {
                    var left = TimeToX(cut.StartTime);
                    var right = TimeToX(cut.EndTime);
                    var camera = Sequence.Cameras.FirstOrDefault(candidate => candidate.Id == cut.CameraId);
                    var rect = new Rect(left, y + 4, Math.Max(2, right - left), RowHeight - 8);
                    context.FillRectangle(new SolidColorBrush(Color.Parse("#446B8F")), rect);
                    context.DrawRectangle(null,
                        new Pen(new SolidColorBrush(Color.Parse(
                            ReferenceEquals(cut, _selectedCut) ? "#FFFFFF" : "#78A9D2")),
                            ReferenceEquals(cut, _selectedCut) ? 2.0 : 1.0), rect);
                    if (rect.Width > 45)
                        DrawText(context, camera?.Name ?? "Unassigned", rect.Left + 5, y + 7,
                            camera == null ? "#FFD1D1" : "#EEF6FF", camera == null);
                    _cutHits.Add(new CutHit(rect.Intersect(
                        new Rect(LabelWidth + 1, y, Math.Max(0, Bounds.Width - LabelWidth - 1), RowHeight)), cut));
                }
            }
        }
        _rowHits.Add(new RowHit(new Rect(0, y, Bounds.Width, RowHeight), RowKind.Cuts, null, null));
        y += RowHeight;
    }

    private void DrawCameraRow(DrawingContext context, CampathCameraTrackViewModel camera, ref double y)
    {
        DrawRowBackground(context, y, 0, Sequence?.SelectedCamera == camera);
        if (camera.CanExpand)
            DrawText(context, camera.IsExpanded ? "▼" : "▶", 9, y + 6, "#B8BDC7");
        DrawText(context, camera.Name, 34, y + 6, "#E0E3E8", true);
        DrawRowActions(context, y, Sequence?.Possession == SequencerPossession.Camera(camera.Id));
        DrawKeyBundles(context, BuildBundles(camera, null, null), y, "#A6AAB2", 7);
        _rowHits.Add(new RowHit(new Rect(0, y, Bounds.Width, RowHeight), RowKind.Camera, camera, null));
        y += RowHeight;
    }

    private void DrawGroupRow(DrawingContext context, CampathCameraTrackViewModel camera, string group, ref double y)
    {
        DrawRowBackground(context, y, 1,
            _selectedCurveTracks.Contains(new TrackSelection(camera.Id, RowKind.Group, group)));
        var expanded = _expandedGroups.Contains((camera.Id, group));
        DrawText(context, expanded ? "▼" : "▶", 25, y + 6, "#AEB3BD");
        DrawText(context, group, 50, y + 6, "#C7CBD3", true);
        if (string.Equals(group, "DOF", StringComparison.Ordinal))
            DrawDofEnabledSelector(context, camera, y);
        if (camera.Editor.IsCurveMode)
            DrawAddKeyAction(context, y);
        else
            DrawLinkedTimingAction(context, y);
        DrawKeyBundles(context, BuildBundles(camera, group, null), y, "#9298A3", 6);
        _rowHits.Add(new RowHit(new Rect(0, y, Bounds.Width, RowHeight), RowKind.Group, camera, group));
        y += RowHeight;
    }

    private void DrawChannelRow(DrawingContext context, CampathCameraTrackViewModel camera,
        CampathCurveChannel channel, ref double y)
    {
        DrawRowBackground(context, y, 2,
            _selectedCurveTracks.Contains(new TrackSelection(camera.Id, RowKind.Channel, channel.Id)));
        context.FillRectangle(new SolidColorBrush(Color.Parse(channel.Color)), new Rect(55, y + 10, 7, 7));
        DrawText(context, channel.Name, 68, y + 6, "#D2D5DB");
        if (camera.Editor.IsCurveMode)
            DrawAddKeyAction(context, y);
        else
            DrawLinkedTimingAction(context, y);
        DrawKeyBundles(context, BuildBundles(camera, channel.Group, channel), y, channel.Color, 6);
        _rowHits.Add(new RowHit(new Rect(0, y, Bounds.Width, RowHeight), RowKind.Channel, camera, channel.Id));
        y += RowHeight;
    }

    private void EnsureValueEditorLayout()
    {
        if (_rebuildingValueEditors)
            return;
        var desired = new List<(CampathCameraTrackViewModel Camera, CampathCurveChannel Channel, Rect Bounds)>();
        var desiredModes = new List<(CampathCameraTrackViewModel Camera, Rect Bounds)>();
        var y = RulerHeight + RowHeight;
        if (Sequence != null)
        {
            foreach (var camera in Sequence.Cameras)
            {
                desiredModes.Add((camera, ModeEditorBounds(y)));
                y += RowHeight;
                if (!camera.IsExpanded || !camera.CanExpand)
                    continue;
                foreach (var channel in GetUngroupedChannels(camera))
                {
                    desired.Add((camera, channel, ValueEditorBounds(y)));
                    y += RowHeight;
                }
                foreach (var group in GetGroups(camera))
                {
                    y += RowHeight;
                    if (!_expandedGroups.Contains((camera.Id, group)))
                        continue;
                    foreach (var channel in camera.Editor.CurveDocument.Channels.Where(channel => channel.Group == group))
                    {
                        desired.Add((camera, channel, ValueEditorBounds(y)));
                        y += RowHeight;
                    }
                }
            }
        }

        if (_modeEditors.Count == desiredModes.Count
            && _modeEditors.Zip(desiredModes).All(pair =>
                ReferenceEquals(pair.First.Camera, pair.Second.Camera)
                && pair.First.Bounds == pair.Second.Bounds)
            && _valueEditors.Count == desired.Count
            && _valueEditors.Zip(desired).All(pair =>
                ReferenceEquals(pair.First.Camera, pair.Second.Camera)
                && ReferenceEquals(pair.First.Channel, pair.Second.Channel)
                && pair.First.Bounds == pair.Second.Bounds))
        {
            UpdateModeEditors();
            UpdateValueEditors();
            return;
        }

        _rebuildingValueEditors = true;
        _updatingValueEditors = true;
        _updatingModeEditors = true;
        try
        {
            Children.Clear();
            Children.Add(_drawingSurface);
            _modeEditors.Clear();
            _valueEditors.Clear();
            foreach (var item in desiredModes)
            {
                var comboBox = new ComboBox
                {
                    ItemsSource = TimelineModeLabels,
                    FontSize = 10,
                    Padding = new Thickness(5, 0)
                };
                ToolTip.SetTip(comboBox, "Camera path interpolation mode");
                var entry = new ModeEditorEntry(item.Camera, comboBox, item.Bounds);
                comboBox.SelectionChanged += async (_, _) => await ApplyModeEditorAsync(entry);
                Children.Add(comboBox);
                _modeEditors.Add(entry);
            }
            foreach (var item in desired)
            {
                var field = new ScrubbyNumericField
                {
                    FormatString = "0.###",
                    Step = GetChannelStep(item.Channel.Id),
                    PixelsPerStep = 6,
                    Minimum = GetChannelMinimum(item.Channel.Id),
                    Maximum = GetChannelMaximum(item.Channel.Id)
                };
                var entry = new ValueEditorEntry(item.Camera, item.Channel, field, item.Bounds);
                field.EditStarted += () => BeginValueEditorEdit(entry);
                field.EditCompleted += () => EndValueEditorEdit(entry);
                field.PropertyChanged += (_, e) =>
                {
                    if (e.Property == ScrubbyNumericField.ValueProperty && !_updatingValueEditors)
                        ApplyValueEditor(entry, field.Value);
                };
                Children.Add(field);
                _valueEditors.Add(entry);
            }
        }
        finally
        {
            _updatingValueEditors = false;
            _updatingModeEditors = false;
            _rebuildingValueEditors = false;
        }
        UpdateModeEditors();
        UpdateValueEditors(force: true);
        InvalidateMeasure();
    }

    private new void InvalidateVisual()
    {
        base.InvalidateVisual();
        _drawingSurface.InvalidateVisual();
    }

    private static Rect ValueEditorBounds(double y) => new(174, y + 3, 72, RowHeight - 6);
    private static Rect ModeEditorBounds(double y) => new(146, y + 3, 64, RowHeight - 6);

    private static readonly string[] TimelineModeLabels = ["Curve", "Catmull", "Linear"];

    private void UpdateModeEditors()
    {
        if (_updatingModeEditors)
            return;
        _updatingModeEditors = true;
        try
        {
            foreach (var entry in _modeEditors)
                entry.ComboBox.SelectedIndex = entry.Camera.Editor.EditorMode switch
                {
                    CampathEditorMode.Curves => 0,
                    CampathEditorMode.CatmullRom => 1,
                    _ => 2
                };
        }
        finally
        {
            _updatingModeEditors = false;
        }
    }

    private async System.Threading.Tasks.Task ApplyModeEditorAsync(ModeEditorEntry entry)
    {
        if (_updatingModeEditors || entry.ComboBox.SelectedIndex < 0)
            return;

        var mode = entry.ComboBox.SelectedIndex switch
        {
            0 => CampathEditorMode.Curves,
            1 => CampathEditorMode.CatmullRom,
            _ => CampathEditorMode.Linear
        };
        var editor = entry.Camera.Editor;
        if (mode == editor.EditorMode)
            return;

        var changesModel = (mode == CampathEditorMode.Curves) != editor.IsCurveMode;
        if (changesModel && editor.HasAuthoredKeys)
        {
            entry.ComboBox.IsEnabled = false;
            var targetName = TimelineModeLabels[entry.ComboBox.SelectedIndex];
            var message = mode == CampathEditorMode.Curves
                ? "Convert the classic compound keyframes into independently editable curve channels? This can be undone."
                : $"Convert the editable curves to {targetName}? Independent channel timing and curve handles will be flattened into compound keyframes. This can be undone.";
            var confirmed = await DialogHelpers.ConfirmAsync(this, "Convert camera path", message);
            entry.ComboBox.IsEnabled = true;
            if (!confirmed)
            {
                UpdateModeEditors();
                return;
            }
        }

        if (Sequence != null)
            Sequence.SelectedCamera = entry.Camera;
        editor.SetEditorMode(mode);
        ClearKeySelection();
        UpdateModeEditors();
        EnsureValueEditorLayout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void UpdateValueEditors(bool force = false)
    {
        if (_updatingValueEditors)
            return;
        _updatingValueEditors = true;
        try
        {
            foreach (var entry in _valueEditors)
            {
                var selected = GetSelectedKeys(entry.Camera, entry.Channel);
                var multiple = selected.Count > 1;
                entry.Field.IsReadOnly = multiple;
                entry.Field.IsMixedValue = multiple;
                entry.Field.IsHighlighted = selected.Count == 1;
                if (!force && entry.Field.IsKeyboardFocusWithin && entry.Field.IsEditing)
                    continue;
                if (!multiple && TryGetDisplayedValue(entry.Camera.Editor, entry.Channel,
                        Sequence?.PlayheadTime ?? entry.Camera.Editor.PlayheadTime, selected, out var value))
                    entry.Field.Value = value;
            }
        }
        finally
        {
            _updatingValueEditors = false;
        }
    }

    private IReadOnlyList<TimelineKey> GetSelectedKeys(
        CampathCameraTrackViewModel camera, CampathCurveChannel channel)
    {
        if (camera.Editor.IsCurveMode)
            return channel.Keys.Where(key => key.Selected)
                .Select(key => new TimelineKey(camera.Editor, null, key, channel, null)).ToList();

        return _classicSelections
            .Where(selection => ReferenceEquals(selection.Editor, camera.Editor)
                && ClassicScopeIncludesChannel(selection.Scope, channel))
            .GroupBy(selection => selection.Key)
            .Select(group => new TimelineKey(camera.Editor, group.Key, null, null, group.First().Scope))
            .ToList();
    }

    private static bool ClassicScopeIncludesChannel(string scope, CampathCurveChannel channel) =>
        scope == "camera"
        || scope == $"channel:{channel.Id}"
        || (!string.IsNullOrWhiteSpace(channel.Group) && scope == $"group:{channel.Group}");

    private static bool TryGetDisplayedValue(CampathEditorViewModel editor, CampathCurveChannel channel,
        double playheadTime, IReadOnlyList<TimelineKey> selected, out double value)
    {
        if (selected.Count == 1)
        {
            value = selected[0].CurveKey?.Value
                ?? GetClassicChannelValue(selected[0].ClassicKey!, channel.Id);
            return true;
        }
        if (editor.IsCurveMode && channel.Keys.Count > 0)
        {
            value = channel.Evaluate(playheadTime);
            return true;
        }
        if (editor.CanEvaluate())
        {
            value = GetSampleChannelValue(editor.Evaluate(playheadTime), channel.Id);
            return true;
        }
        value = 0;
        return false;
    }

    private void BeginValueEditorEdit(ValueEditorEntry entry)
    {
        if (entry.EditActive || entry.Field.IsReadOnly || Sequence == null)
            return;
        entry.EditActive = true;
        Sequence.BeginHistoryTransaction();
        entry.Camera.Editor.BeginHistoryTransaction();
    }

    private void ApplyValueEditor(ValueEditorEntry entry, double value)
    {
        if (_updatingValueEditors || entry.Field.IsReadOnly || Sequence == null)
            return;

        var selected = GetSelectedKeys(entry.Camera, entry.Channel);
        if (selected.Count > 1)
            return;

        var implicitEdit = !entry.EditActive;
        if (implicitEdit)
            BeginValueEditorEdit(entry);
        value = Math.Clamp(value, GetChannelMinimum(entry.Channel.Id), GetChannelMaximum(entry.Channel.Id));
        try
        {
            if (selected.Count == 1)
            {
                SetTimelineKeyValue(selected[0], entry.Channel, value);
            }
            else if (entry.Camera.Editor.IsCurveMode)
            {
                SetCurveValueAtPlayhead(entry.Camera, entry.Channel, value);
            }
            else
            {
                SetClassicValueAtPlayhead(entry.Camera, entry.Channel, value);
            }
        }
        finally
        {
            if (implicitEdit)
                EndValueEditorEdit(entry);
        }
        PublishGizmoSelection();
        UpdateValueEditors();
        InvalidateVisual();
    }

    private void EndValueEditorEdit(ValueEditorEntry entry)
    {
        if (!entry.EditActive || Sequence == null)
            return;
        entry.EditActive = false;
        entry.Camera.Editor.CommitHistoryTransaction();
        Sequence.CommitHistoryTransaction();
        UpdateValueEditors(force: true);
        InvalidateVisual();
    }

    private void SetCurveValueAtPlayhead(CampathCameraTrackViewModel camera,
        CampathCurveChannel channel, double value)
    {
        var time = Sequence!.PlayheadTime;
        var key = channel.Keys.FirstOrDefault(candidate => Math.Abs(candidate.Time - time) < 0.0001);
        if (key == null)
        {
            key = new CampathCurveKey
            {
                Time = time,
                Value = value,
                Selected = true,
                Interpolation = CurveInterpolationMode.Bezier
            };
            var index = 0;
            while (index < channel.Keys.Count && channel.Keys[index].Time < time)
                index++;
            channel.Keys.Insert(index, key);
        }
        else
        {
            key.Value = value;
            key.Selected = true;
        }
        CampathPathConversion.AutoTangents(channel);
        camera.Editor.NotifyCurveDocumentChanged();
    }

    private void SetClassicValueAtPlayhead(CampathCameraTrackViewModel camera,
        CampathCurveChannel channel, double value)
    {
        var editor = camera.Editor;
        var time = Sequence!.PlayheadTime;
        var key = editor.Keyframes.FirstOrDefault(candidate => Math.Abs(candidate.Time - time) < 0.0001);
        if (key == null)
        {
            var sample = editor.CanEvaluate()
                ? editor.Evaluate(time)
                : new CampathSample(Vector3.Zero, Quaternion.Identity, 90.0, false,
                    CampathDofSettings.Default with { Enabled = editor.CurveDocument.DofEnabled });
            editor.AddKeyframe(time, sample.Position, sample.Rotation, sample.Fov);
            key = editor.Keyframes.First(candidate => Math.Abs(candidate.Time - time) < 0.0001);
            key.Dof = sample.Dof;
        }
        SetClassicChannelValue(key, channel.Id, value);
        _classicSelections.Add(new ClassicSelection(editor, key, $"channel:{channel.Id}"));
        editor.SelectedKeyframe = key;
    }

    private static void SetTimelineKeyValue(TimelineKey key,
        CampathCurveChannel channel, double value)
    {
        if (key.CurveKey != null)
        {
            key.CurveKey.Value = value;
            CampathPathConversion.AutoTangents(channel);
            key.Editor.NotifyCurveDocumentChanged();
        }
        else if (key.ClassicKey != null)
        {
            SetClassicChannelValue(key.ClassicKey, channel.Id, value);
        }
    }

    private static void SetClassicChannelValue(
        CampathKeyframeViewModel key, string channelId, double value)
    {
        switch (channelId)
        {
            case "position.x": key.Position = key.Position with { X = (float)value }; break;
            case "position.y": key.Position = key.Position with { Y = (float)value }; break;
            case "position.z": key.Position = key.Position with { Z = (float)value }; break;
            case "rotation.pitch":
            case "rotation.yaw":
            case "rotation.roll":
            {
                var (pitch, yaw, roll) = QuaternionToEuler(key.Rotation);
                if (channelId == "rotation.pitch") pitch = value;
                else if (channelId == "rotation.yaw") yaw = value;
                else roll = value;
                key.Rotation = EulerToQuaternion(pitch, yaw, roll);
                break;
            }
            case "fov": key.Fov = value; break;
            case "dof.nearBlurry": key.Dof = key.Dof with { NearBlurry = value }; break;
            case "dof.nearCrisp": key.Dof = key.Dof with { NearCrisp = value }; break;
            case "dof.farCrisp": key.Dof = key.Dof with { FarCrisp = value }; break;
            case "dof.farBlurry": key.Dof = key.Dof with { FarBlurry = value }; break;
            case "dof.maxBlur": key.Dof = key.Dof with { MaxBlurSize = Math.Clamp(value, 0.0, 11.0) }; break;
            case "dof.radiusScale": key.Dof = key.Dof with { RadiusScale = Math.Clamp(value, 0.25, 5.0) }; break;
        }
    }

    private static double GetClassicChannelValue(CampathKeyframeViewModel key, string channelId)
    {
        var (pitch, yaw, roll) = QuaternionToEuler(key.Rotation);
        return channelId switch
        {
            "position.x" => key.Position.X,
            "position.y" => key.Position.Y,
            "position.z" => key.Position.Z,
            "rotation.pitch" => pitch,
            "rotation.yaw" => yaw,
            "rotation.roll" => roll,
            "fov" => key.Fov,
            "dof.nearBlurry" => key.Dof.NearBlurry,
            "dof.nearCrisp" => key.Dof.NearCrisp,
            "dof.farCrisp" => key.Dof.FarCrisp,
            "dof.farBlurry" => key.Dof.FarBlurry,
            "dof.maxBlur" => key.Dof.MaxBlurSize,
            "dof.radiusScale" => key.Dof.RadiusScale,
            _ => 0.0
        };
    }

    private static double GetSampleChannelValue(CampathSample sample, string channelId)
    {
        var (pitch, yaw, roll) = QuaternionToEuler(sample.Rotation);
        return channelId switch
        {
            "position.x" => sample.Position.X,
            "position.y" => sample.Position.Y,
            "position.z" => sample.Position.Z,
            "rotation.pitch" => pitch,
            "rotation.yaw" => yaw,
            "rotation.roll" => roll,
            "fov" => sample.Fov,
            "dof.nearBlurry" => sample.Dof.NearBlurry,
            "dof.nearCrisp" => sample.Dof.NearCrisp,
            "dof.farCrisp" => sample.Dof.FarCrisp,
            "dof.farBlurry" => sample.Dof.FarBlurry,
            "dof.maxBlur" => sample.Dof.MaxBlurSize,
            "dof.radiusScale" => sample.Dof.RadiusScale,
            _ => 0.0
        };
    }

    private static double GetChannelStep(string channelId) => channelId switch
    {
        "rotation.pitch" or "rotation.yaw" or "rotation.roll" => 1.0,
        "dof.radiusScale" => 0.05,
        _ => 0.1
    };

    private static double GetChannelMinimum(string channelId) => channelId switch
    {
        "dof.maxBlur" => 0.0,
        "dof.radiusScale" => 0.25,
        _ => double.NegativeInfinity
    };

    private static double GetChannelMaximum(string channelId) => channelId switch
    {
        "dof.maxBlur" => 11.0,
        "dof.radiusScale" => 5.0,
        _ => double.PositiveInfinity
    };

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

    private static Quaternion EulerToQuaternion(double pitch, double yaw, double roll)
    {
        const double degToRad = Math.PI / 180.0;
        var pitchRad = (float)(pitch * degToRad);
        var yawRad = (float)(yaw * degToRad);
        var rollRad = (float)(roll * degToRad);
        var qPitch = Quaternion.CreateFromAxisAngle(Vector3.UnitY, pitchRad);
        var qYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yawRad);
        var qRoll = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rollRad);
        return Quaternion.Normalize(qYaw * qPitch * qRoll);
    }

    private void DrawKeyBundles(DrawingContext context, IEnumerable<KeyBundle> bundles,
        double y, string color, double radius)
    {
        using var clip = context.PushClip(
            new Rect(LabelWidth + 1, y, Math.Max(0, Bounds.Width - LabelWidth - 1), RowHeight));
        foreach (var bundle in bundles)
        {
            var markerRadius = bundle.MarkerRadius ?? radius;
            var markerColor = bundle.MarkerColor ?? color;
            var brush = new SolidColorBrush(Color.Parse(markerColor));
            var x = TimeToX(bundle.Time);
            if (x < LabelWidth - markerRadius || x > Bounds.Width + markerRadius)
                continue;
            var geometry = new StreamGeometry();
            using var path = geometry.Open();
            path.BeginFigure(new Point(x, y + RowHeight / 2 - markerRadius), true);
            path.LineTo(new Point(x + markerRadius, y + RowHeight / 2));
            path.LineTo(new Point(x, y + RowHeight / 2 + markerRadius));
            path.LineTo(new Point(x - markerRadius, y + RowHeight / 2));
            path.EndFigure(true);
            var selected = bundle.Keys.Any(IsKeySelected)
                || bundle.Keys.Any(key => key.ClassicKey != null
                    && _linkedClassicDragKeys.Contains(key.ClassicKey));
            context.DrawGeometry(brush,
                selected ? new Pen(new SolidColorBrush(Color.Parse("#FFFFFF")), 1.5) : null, geometry);
            _keyHits.Add(new KeyHit(
                new Rect(x - markerRadius - 3, y + RowHeight / 2 - markerRadius - 3,
                    markerRadius * 2 + 6, markerRadius * 2 + 6), bundle));
        }
    }

    private static IReadOnlyList<KeyBundle> BuildBundles(CampathCameraTrackViewModel camera,
        string? group, CampathCurveChannel? channel)
    {
        if (!camera.Editor.IsCurveMode)
        {
            var scope = channel != null
                ? $"channel:{channel.Id}"
                : group != null
                    ? $"group:{group}"
                    : "camera";
            return camera.Editor.Keyframes
                .Select(key => new KeyBundle(key.Time,
                    new[] { new TimelineKey(camera.Editor, key, null, null, scope) }))
                .ToList();
        }

        var channels = channel != null
            ? new[] { channel }
            : camera.Editor.CurveDocument.Channels
                .Where(candidate => group == null || candidate.Group == group)
                .ToArray();
        if (channel != null)
        {
            return channel.Keys.Select(key => new KeyBundle(key.Time,
                new[] { new TimelineKey(camera.Editor, null, key, channel, null) }))
                .ToList();
        }

        var activeChannels = channels.Where(candidate => candidate.Keys.Count > 0).ToList();
        var keys = activeChannels.SelectMany(candidate => candidate.Keys
            .Select(key => new TimelineKey(camera.Editor, null, key, candidate, null)))
            .OrderBy(key => key.Time)
            .ToList();
        var expectedCount = activeChannels.Count;
        var requiredForBundle = Math.Max(2, (expectedCount + 1) / 2);
        var result = new List<KeyBundle>();
        foreach (var cluster in keys.GroupBy(key => Math.Round(key.Time, 4)))
        {
            var center = cluster.Average(key => key.Time);
            var members = cluster.GroupBy(key => key.CurveChannel)
                .Select(items => items.MinBy(key => Math.Abs(key.Time - center))!)
                .ToList();
            if (members.Count >= requiredForBundle)
            {
                var complete = members.Count == expectedCount;
                result.Add(new KeyBundle(center, members,
                    complete ? "#AAAAAA" : "#777A82",
                    complete ? 8.0 : 6.5));
                continue;
            }

            result.Add(new KeyBundle(center, members,
                members.Count == 1
                    ? members[0].CurveChannel?.Color ?? "#AAAAAA"
                    : "#AAAAAA",
                3.5));
        }
        return result.OrderBy(bundle => bundle.Time).ToList();
    }

    private static IReadOnlyList<string> GetGroups(CampathCameraTrackViewModel camera) =>
        camera.Editor.CurveDocument.Channels
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Group))
            .Select(channel => channel.Group).Distinct().ToList();

    private static IReadOnlyList<CampathCurveChannel> GetUngroupedChannels(
        CampathCameraTrackViewModel camera) =>
        camera.Editor.CurveDocument.Channels
            .Where(channel => string.IsNullOrWhiteSpace(channel.Group))
            .ToList();

    private void DrawRowActions(DrawingContext context, double y, bool active)
    {
        var pilotRect = new RoundedRect(new Rect(218, y + 5, 30, RowHeight - 10), 4);
        var pilotBrush = new SolidColorBrush(Color.Parse(active ? "#D7A940" : "#5D6470"));
        context.DrawRectangle(pilotBrush, new Pen(new SolidColorBrush(Color.Parse(active ? "#F0C766" : "#7A828F"))), pilotRect);
        DrawText(context, "P", 229, y + 6, active ? "#17191D" : "#E4E7EC", true);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#D94B4B")),
            new Pen(new SolidColorBrush(Color.Parse("#F07878"))),
            new Point(264, y + RowHeight / 2), 6, 6);
    }

    private static void DrawAddKeyAction(DrawingContext context, double y)
    {
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#D94B4B")),
            new Pen(new SolidColorBrush(Color.Parse("#F07878"))),
            new Point(264, y + RowHeight / 2), 6, 6);
    }

    private static void DrawLinkedTimingAction(DrawingContext context, double y)
    {
        var pen = new Pen(new SolidColorBrush(Color.Parse("#777E8A")), 1.3);
        var centerY = y + RowHeight / 2;
        context.DrawEllipse(null, pen, new Point(260.5, centerY), 4.5, 3.2);
        context.DrawEllipse(null, pen, new Point(267.5, centerY), 4.5, 3.2);
        context.DrawLine(pen, new Point(261.5, centerY), new Point(266.5, centerY));
    }

    private static void DrawDofEnabledSelector(DrawingContext context,
        CampathCameraTrackViewModel camera, double y)
    {
        var rect = new RoundedRect(new Rect(84, y + 4, 62, RowHeight - 8), 3);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#242831")),
            new Pen(new SolidColorBrush(Color.Parse("#4B515E"))), rect);
        DrawText(context, camera.Editor.CurveDocument.DofEnabled ? "On" : "Off",
            92, y + 6, "#D7DAE0");
        DrawText(context, "▼", 130, y + 6, "#9298A3");
    }

    private void DrawRowBackground(DrawingContext context, double y, int depth, bool selected = false)
    {
        var color = selected
            ? "#252C37"
            : depth switch { 0 => "#181B21", 1 => "#15181E", _ => "#12151A" };
        context.FillRectangle(new SolidColorBrush(Color.Parse(color)), new Rect(0, y, Bounds.Width, RowHeight));
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#292D35"))),
            new Point(0, y + RowHeight), new Point(Bounds.Width, y + RowHeight));
    }

    private static void DrawText(DrawingContext context, string text, double x, double y, string color, bool bold = false)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI", bold ? FontStyle.Normal : FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal),
            11, new SolidColorBrush(Color.Parse(color)));
        context.DrawText(formatted, new Point(x, y));
    }

    private double TimeToX(double time) => LabelWidth + 8 + (time - ViewStart) / SecondsPerPixel;
    private double XToTime(double x) => ViewStart + Math.Max(0, x - LabelWidth - 8) * SecondsPerPixel;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_valueEditors.Any(entry => entry.Field.IsPointerOver)
            || _modeEditors.Any(entry => entry.ComboBox.IsPointerOver))
            return;
        Focus();
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsRightButtonPressed && Sequence != null)
        {
            var contextCutHit = _cutHits.LastOrDefault(candidate => candidate.Bounds.Contains(point));
            if (contextCutHit != null)
            {
                _selectedCut = contextCutHit.Cut;
                OpenCutCameraMenu(contextCutHit.Cut);
                e.Handled = true;
                InvalidateVisual();
                return;
            }
            var trackHit = _rowHits.FirstOrDefault(candidate =>
                candidate.Bounds.Contains(point) && candidate.Kind == RowKind.Camera);
            if (trackHit?.Camera != null && point.X < LabelWidth)
            {
                OpenCameraTrackMenu(trackHit.Camera);
                e.Handled = true;
            }
            return;
        }
        if (properties.IsMiddleButtonPressed)
        {
            _panning = true;
            _lastPointer = point;
            e.Pointer.Capture(this);
            return;
        }
        if (!properties.IsLeftButtonPressed || Sequence == null)
            return;

        // Scrubbing may only begin on the ruler. Pointer capture keeps an active
        // scrub going when the cursor subsequently moves into the track rows.
        if (point.X >= LabelWidth && point.Y >= 0 && point.Y < RulerHeight)
        {
            _selectedCut = null;
            _scrubbing = true;
            var time = XToTime(point.X);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                time = SnapTime(time, GetSnapTimes());
            Sequence.PlayheadTime = time;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        var cutHit = _cutHits.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (cutHit != null)
        {
            _selectedCut = cutHit.Cut;
            ClearKeySelection();
            BeginCutDrag(cutHit, point, e.Pointer);
            e.Handled = true;
            return;
        }

        _selectedCut = null;
        var keyHit = _keyHits.LastOrDefault(candidate => candidate.Bounds.Contains(point));
        if (keyHit != null)
        {
            _selectedCut = null;
            BeginKeyDrag(keyHit.Bundle, point, e.KeyModifiers, e.Pointer);
            e.Handled = true;
            return;
        }

        var hit = _rowHits.FirstOrDefault(candidate => candidate.Bounds.Contains(point));
        if (hit == null)
        {
            if (point.X >= LabelWidth && point.Y >= RulerHeight + RowHeight)
            {
                _selectedCut = null;
                BeginMarquee(point, e.KeyModifiers, e.Pointer);
                e.Handled = true;
            }
            else if (point.X < LabelWidth && point.Y >= RulerHeight)
            {
                _selectedCut = null;
                ClearKeySelection();
                ClearCurveTrackSelection();
                e.Handled = true;
                InvalidateVisual();
            }
            return;
        }
        if (hit.Kind == RowKind.Group && hit.Camera != null
            && string.Equals(hit.Value, "DOF", StringComparison.Ordinal)
            && point.X >= 84 && point.X <= 146)
        {
            OpenDofEnabledMenu(hit.Camera);
            e.Handled = true;
            return;
        }
        else if (point.X >= 214 && point.X <= 251)
        {
            if (hit.Kind == RowKind.Cuts) Sequence.PossessCameraCuts();
            else if (hit.Kind == RowKind.Camera && hit.Camera != null) Sequence.PossessCamera(hit.Camera.Id);
        }
        else if (point.X >= 254 && point.X <= 276)
        {
            if (hit.Kind == RowKind.Cuts) Sequence.RequestCameraCut();
            else if (hit.Kind == RowKind.Camera && hit.Camera != null)
            {
                Sequence.SelectedCamera = hit.Camera;
                Sequence.RequestCameraKey(hit.Camera.Id);
            }
            else if (hit.Kind == RowKind.Group && hit.Camera != null && hit.Value != null)
            {
                if (!hit.Camera.Editor.IsCurveMode)
                    return;
                Sequence.SelectedCamera = hit.Camera;
                Sequence.RequestCameraKey(hit.Camera.Id,
                    hit.Camera.Editor.CurveDocument.Channels
                        .Where(channel => channel.Group == hit.Value)
                        .Select(channel => channel.Id)
                        .ToList());
            }
            else if (hit.Kind == RowKind.Channel && hit.Camera != null && hit.Value != null)
            {
                if (!hit.Camera.Editor.IsCurveMode)
                    return;
                Sequence.SelectedCamera = hit.Camera;
                Sequence.RequestCameraKey(hit.Camera.Id, new[] { hit.Value });
            }
        }
        else if (hit.Kind == RowKind.Camera && hit.Camera?.CanExpand == true
            && point.X >= 0 && point.X <= 28)
        {
            ClearCurveTrackSelection();
            hit.Camera.IsExpanded = !hit.Camera.IsExpanded;
            EnsureValueEditorLayout();
            InvalidateMeasure();
        }
        else if (hit.Kind == RowKind.Camera && hit.Camera != null && point.X < LabelWidth)
        {
            ClearKeySelection();
            ClearCurveTrackSelection();
            Sequence.SelectedCamera = hit.Camera;
            ShowAllCurveChannels(hit.Camera);
            e.Handled = true;
        }
        else if (hit.Kind == RowKind.Group && hit.Camera != null && hit.Value != null
            && point.X >= 16 && point.X <= 44)
        {
            var key = (hit.Camera.Id, hit.Value);
            if (!_expandedGroups.Add(key)) _expandedGroups.Remove(key);
            EnsureValueEditorLayout();
            InvalidateMeasure();
        }
        else if ((hit.Kind is RowKind.Group or RowKind.Channel)
            && hit.Camera != null && hit.Value != null && point.X < LabelWidth)
        {
            ClearKeySelection();
            SelectCurveTracks(hit, e.KeyModifiers);
            e.Handled = true;
        }
        else if (point.X >= LabelWidth && point.Y >= RulerHeight + RowHeight)
        {
            BeginMarquee(point, e.KeyModifiers, e.Pointer);
            e.Handled = true;
        }
        else if (point.X < LabelWidth)
        {
            ClearKeySelection();
            ClearCurveTrackSelection();
            e.Handled = true;
        }
        InvalidateVisual();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        if (_scrubbing && Sequence != null)
        {
            var time = XToTime(point.X);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                time = SnapTime(time, GetSnapTimes());
            Sequence.PlayheadTime = time;
            e.Handled = true;
            return;
        }
        if (_dragCut != null)
        {
            UpdateCutDrag(point, e.KeyModifiers);
            e.Handled = true;
            return;
        }
        if (_draggingKeys)
        {
            UpdateKeyDrag(point, e.KeyModifiers);
            e.Handled = true;
            return;
        }
        if (_marqueeSelecting)
        {
            UpdateMarquee(point);
            e.Handled = true;
            return;
        }
        if (!_panning)
            return;
        ViewStart -= (point.X - _lastPointer.X) * SecondsPerPixel;
        _lastPointer = point;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_scrubbing || _dragCut != null || _draggingKeys || _marqueeSelecting)
        {
            var completedScrub = _scrubbing;
            var commitsEdit = _dragCut != null || (_draggingKeys && _keyDragActivated);
            EndKeyDrag();
            if (commitsEdit)
                Sequence?.CommitHistoryTransaction();
            _scrubbing = false;
            _dragCut = null;
            _cutDragMode = CutDragMode.None;
            _marqueeSelecting = false;
            e.Pointer.Capture(null);
            e.Handled = true;
            InvalidateVisual();
            if (completedScrub)
                Sequence?.CommitPlayheadScrub();
            return;
        }
        if (!_panning)
            return;
        _panning = false;
        e.Pointer.Capture(null);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var point = e.GetPosition(this);
        if (point.X < LabelWidth)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ViewStart = Math.Max(0.0, ViewStart - e.Delta.Y * SecondsPerPixel * 80);
        }
        else
        {
            var anchor = XToTime(point.X);
            var factor = Math.Pow(1.15, -e.Delta.Y);
            SecondsPerPixel *= factor;
            if (point.X >= LabelWidth)
                ViewStart = Math.Max(0.0, anchor - (point.X - LabelWidth) * SecondsPerPixel);
        }
        e.Handled = true;
    }

    private void SelectCurveTracks(RowHit clickedRow, KeyModifiers modifiers)
    {
        if (Sequence == null || clickedRow.Camera == null || clickedRow.Value == null)
            return;

        var camera = clickedRow.Camera;
        if (!ReferenceEquals(Sequence.SelectedCamera, camera)
            || _selectedCurveTracks.Any(selection => selection.CameraId != camera.Id))
            ClearCurveTrackSelection();
        Sequence.SelectedCamera = camera;
        var selectableRows = _rowHits
            .Where(row => ReferenceEquals(row.Camera, camera)
                && (row.Kind is RowKind.Group or RowKind.Channel)
                && row.Value != null)
            .ToList();
        var clickedIndex = selectableRows.FindIndex(row => SameTrack(row, clickedRow));
        if (clickedIndex < 0)
            return;

        var control = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var rangeStart = -1;
        if (shift && _trackSelectionAnchor is { } anchor
            && anchor.CameraId == camera.Id)
        {
            rangeStart = selectableRows.FindIndex(row =>
                row.Kind == anchor.Kind && string.Equals(row.Value, anchor.Value, StringComparison.Ordinal));
        }

        if (rangeStart >= 0)
        {
            if (!control)
                _selectedCurveTracks.Clear();

            var first = Math.Min(rangeStart, clickedIndex);
            var last = Math.Max(rangeStart, clickedIndex);
            foreach (var row in selectableRows.Skip(first).Take(last - first + 1))
                _selectedCurveTracks.Add(ToTrackSelection(row));
        }
        else
        {
            var selection = ToTrackSelection(clickedRow);
            if (control)
            {
                if (!_selectedCurveTracks.Add(selection))
                    _selectedCurveTracks.Remove(selection);
            }
            else
            {
                _selectedCurveTracks.Clear();
                _selectedCurveTracks.Add(selection);
            }
            _trackSelectionAnchor = new TrackSelectionAnchor(
                camera.Id, clickedRow.Kind, clickedRow.Value);
        }

        ApplyCurveTrackSelection(camera);
        _drawingSurface.InvalidateVisual();
    }

    private void ApplyCurveTrackSelection(CampathCameraTrackViewModel camera)
    {
        var selections = _selectedCurveTracks
            .Where(selection => selection.CameraId == camera.Id)
            .ToList();
        if (selections.Count == 0)
        {
            ShowAllCurveChannels(camera);
            return;
        }

        var selectedChannels = selections
            .SelectMany(selection => GetChannelsForSelection(camera, selection))
            .ToHashSet();
        foreach (var channel in camera.Editor.CurveDocument.Channels)
            channel.IsVisible = selectedChannels.Contains(channel);
    }

    private void ClearCurveTrackSelection()
    {
        _selectedCurveTracks.Clear();
        _trackSelectionAnchor = null;
        if (Sequence == null)
            return;
        foreach (var camera in Sequence.Cameras)
            ShowAllCurveChannels(camera);
        _drawingSurface.InvalidateVisual();
    }

    private static void ShowAllCurveChannels(CampathCameraTrackViewModel camera)
    {
        foreach (var channel in camera.Editor.CurveDocument.Channels)
            channel.IsVisible = true;
    }

    private static TrackSelection ToTrackSelection(RowHit row) =>
        new(row.Camera!.Id, row.Kind, row.Value!);

    private static bool SameTrack(RowHit first, RowHit second) =>
        ReferenceEquals(first.Camera, second.Camera)
        && first.Kind == second.Kind
        && string.Equals(first.Value, second.Value, StringComparison.Ordinal);

    private static IEnumerable<CampathCurveChannel> GetChannelsForSelection(
        CampathCameraTrackViewModel camera, TrackSelection selection)
    {
        return selection.Kind switch
        {
            RowKind.Group => camera.Editor.CurveDocument.Channels
                .Where(channel => string.Equals(channel.Group, selection.Value, StringComparison.Ordinal)),
            RowKind.Channel => camera.Editor.CurveDocument.Channels
                .Where(channel => string.Equals(channel.Id, selection.Value, StringComparison.Ordinal)),
            _ => []
        };
    }

    private void BeginKeyDrag(KeyBundle bundle, Point point, KeyModifiers modifiers, IPointer pointer)
    {
        var additive = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);
        var allSelected = bundle.Keys.All(IsKeySelected);
        if (!additive && !allSelected)
            ClearKeySelection();

        if (modifiers.HasFlag(KeyModifiers.Control) && allSelected)
        {
            foreach (var key in bundle.Keys)
                SetKeySelected(key, false);
            PublishGizmoSelection();
            UpdateValueEditors();
            InvalidateVisual();
            return;
        }

        foreach (var key in bundle.Keys)
            SetKeySelected(key, true);
        if (Sequence != null)
            Sequence.SelectedCamera = Sequence.Cameras.FirstOrDefault(camera =>
                bundle.Keys.Any(key => ReferenceEquals(key.Editor, camera.Editor)));
        PublishGizmoSelection();

        _keyDragOrigins.Clear();
        foreach (var key in SelectedTimelineKeys())
            _keyDragOrigins[key] = key.Time;
        _historyEditors.Clear();
        foreach (var editor in _keyDragOrigins.Keys.Select(key => key.Editor).Distinct())
            _historyEditors.Add(editor);
        _keyDragStart = point;
        _draggingKeys = true;
        _keyDragActivated = false;
        _linkedClassicDragKeys.Clear();
        pointer.Capture(this);
        UpdateValueEditors();
        InvalidateVisual();
    }

    private void UpdateKeyDrag(Point point, KeyModifiers modifiers)
    {
        if (_keyDragOrigins.Count == 0)
            return;
        if (!_keyDragActivated)
        {
            if (Math.Abs(point.X - _keyDragStart.X) < 3.0)
                return;
            _keyDragActivated = true;
            Sequence?.BeginHistoryTransaction();
            foreach (var editor in _historyEditors)
                editor.BeginHistoryTransaction();
            foreach (var key in _keyDragOrigins.Keys)
                if (key.ClassicKey != null)
                    _linkedClassicDragKeys.Add(key.ClassicKey);
        }
        var delta = (point.X - _keyDragStart.X) * SecondsPerPixel;
        delta = Math.Max(delta, -_keyDragOrigins.Values.Min());
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            var movingTimes = _keyDragOrigins.Values.Select(origin => origin + delta);
            delta += FindSnapAdjustment(movingTimes,
                GetSnapTimes(key => !IsMovingKey(key)), SnapThresholdTime);
            delta = Math.Max(delta, -_keyDragOrigins.Values.Min());
        }
        foreach (var (key, origin) in _keyDragOrigins)
            key.Time = origin + delta;
        foreach (var editor in _historyEditors.Where(editor => editor.IsCurveMode))
        {
            foreach (var channel in GetDraggedCurveChannels(editor))
            {
                SortCurveKeys(channel);
                CampathPathConversion.AutoTangents(channel);
            }
            editor.NotifyCurveDocumentChanged();
        }
        PublishGizmoSelection();
        InvalidateVisual();
    }

    private void EndKeyDrag()
    {
        if (!_draggingKeys)
            return;
        if (_keyDragActivated)
        {
            foreach (var editor in _historyEditors)
            {
                if (editor.IsCurveMode)
                {
                    foreach (var channel in GetDraggedCurveChannels(editor))
                    {
                        SortCurveKeys(channel);
                        CampathPathConversion.AutoTangents(channel);
                    }
                    editor.NotifyCurveDocumentChanged();
                }
                editor.CommitHistoryTransaction();
            }
        }
        _historyEditors.Clear();
        _keyDragOrigins.Clear();
        _linkedClassicDragKeys.Clear();
        _keyDragActivated = false;
        _draggingKeys = false;
        UpdateValueEditors();
    }

    private IEnumerable<CampathCurveChannel> GetDraggedCurveChannels(CampathEditorViewModel editor) =>
        _keyDragOrigins.Keys
            .Where(key => ReferenceEquals(key.Editor, editor) && key.CurveChannel != null)
            .Select(key => key.CurveChannel!)
            .Distinct();

    private static void SortCurveKeys(CampathCurveChannel channel)
    {
        var ordered = channel.Keys.OrderBy(key => key.Time).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = channel.Keys.IndexOf(ordered[index]);
            if (current != index)
                channel.Keys.Move(current, index);
        }
    }

    private void BeginMarquee(Point point, KeyModifiers modifiers, IPointer pointer)
    {
        _marqueeSelecting = true;
        _marqueeAdditive = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Shift);
        _marqueeStart = point;
        _marqueeRect = new Rect(point, point);
        _selectionBeforeMarquee.Clear();
        foreach (var key in SelectedTimelineKeys())
            _selectionBeforeMarquee.Add(key);
        if (!_marqueeAdditive)
            ClearKeySelection();
        pointer.Capture(this);
        InvalidateVisual();
    }

    private void UpdateMarquee(Point point)
    {
        _marqueeRect = RectFromPoints(_marqueeStart, point);
        foreach (var key in AllTimelineKeys())
            SetKeySelected(key, _marqueeAdditive && _selectionBeforeMarquee.Contains(key));
        foreach (var hit in _keyHits.Where(hit => _marqueeRect.Intersects(hit.Bounds)))
            foreach (var key in hit.Bundle.Keys)
                SetKeySelected(key, true);
        PublishGizmoSelection();
        UpdateValueEditors();
        InvalidateVisual();
    }

    private void BeginCutDrag(CutHit hit, Point point, IPointer pointer)
    {
        Sequence?.BeginHistoryTransaction();
        _dragCut = hit.Cut;
        _cutDragPointerTime = XToTime(point.X);
        _cutDragStart = hit.Cut.StartTime;
        _cutDragEnd = hit.Cut.EndTime;
        const double handleWidth = 7;
        _cutDragMode = Math.Abs(point.X - TimeToX(hit.Cut.StartTime)) <= handleWidth
            ? CutDragMode.ResizeStart
            : Math.Abs(point.X - TimeToX(hit.Cut.EndTime)) <= handleWidth
                ? CutDragMode.ResizeEnd
                : CutDragMode.Move;
        pointer.Capture(this);
    }

    private void UpdateCutDrag(Point point, KeyModifiers modifiers)
    {
        if (_dragCut == null || Sequence == null)
            return;
        var ordered = Sequence.CameraCuts.OrderBy(cut => cut.StartTime).ToList();
        var index = ordered.IndexOf(_dragCut);
        var previousEnd = index > 0 ? ordered[index - 1].EndTime : 0.0;
        var nextStart = index >= 0 && index + 1 < ordered.Count
            ? ordered[index + 1].StartTime
            : double.PositiveInfinity;
        var delta = XToTime(point.X) - _cutDragPointerTime;
        var minimumDuration = Math.Max(0.01, SecondsPerPixel * 4);

        if (_cutDragMode == CutDragMode.Move)
        {
            var duration = _cutDragEnd - _cutDragStart;
            var start = Math.Clamp(_cutDragStart + delta, previousEnd,
                double.IsPositiveInfinity(nextStart) ? double.MaxValue : Math.Max(previousEnd, nextStart - duration));
            _dragCut.StartTime = start;
            _dragCut.EndTime = start + duration;
        }
        else if (_cutDragMode == CutDragMode.ResizeStart)
        {
            var start = _cutDragStart + delta;
            if (modifiers.HasFlag(KeyModifiers.Shift))
                start = SnapTime(start, GetSnapTimes(excludedCut: _dragCut));
            _dragCut.StartTime = Math.Clamp(start, previousEnd,
                Math.Max(previousEnd, _cutDragEnd - minimumDuration));
        }
        else if (_cutDragMode == CutDragMode.ResizeEnd)
        {
            var end = _cutDragEnd + delta;
            if (modifiers.HasFlag(KeyModifiers.Shift))
                end = SnapTime(end, GetSnapTimes(excludedCut: _dragCut));
            _dragCut.EndTime = Math.Clamp(end, _cutDragStart + minimumDuration, nextStart);
        }
        InvalidateVisual();
    }

    private void OpenCutCameraMenu(CameraCutSectionViewModel cut)
    {
        if (Sequence == null)
            return;
        var items = new List<Control>();
        var unassigned = new MenuItem { Header = "Unassigned", IsChecked = cut.CameraId == Guid.Empty };
        unassigned.Click += (_, _) => SetCutCamera(cut, Guid.Empty);
        items.Add(unassigned);
        items.Add(new Separator());
        foreach (var camera in Sequence.Cameras)
        {
            var item = new MenuItem { Header = camera.Name, IsChecked = cut.CameraId == camera.Id };
            item.Click += (_, _) => SetCutCamera(cut, camera.Id);
            items.Add(item);
        }
        new ContextMenu { ItemsSource = items }.Open(this);
    }

    private void SetCutCamera(CameraCutSectionViewModel cut, Guid cameraId)
    {
        if (Sequence == null || cut.CameraId == cameraId)
            return;
        Sequence.BeginHistoryTransaction();
        cut.CameraId = cameraId;
        Sequence.CommitHistoryTransaction();
    }

    private void OpenCameraTrackMenu(CampathCameraTrackViewModel camera)
    {
        if (Sequence == null)
            return;
        var duplicate = new MenuItem { Header = "Duplicate" };
        duplicate.Click += (_, _) =>
        {
            Sequence.DuplicateCamera(camera);
            InvalidateMeasure();
            InvalidateVisual();
        };
        var save = new MenuItem { Header = "Save Campath" };
        save.Click += (_, _) => SaveCameraRequested?.Invoke(camera);
        var remove = new MenuItem { Header = "Remove" };
        remove.Click += (_, _) =>
        {
            Sequence.RemoveCamera(camera);
            InvalidateMeasure();
            InvalidateVisual();
        };
        new ContextMenu
        {
            ItemsSource = new Control[] { duplicate, save, new Separator(), remove }
        }.Open(this);
    }

    private void OpenDofEnabledMenu(CampathCameraTrackViewModel camera)
    {
        if (_dofEnabledMenu != null)
        {
            _dofEnabledMenu.Close();
            _dofEnabledMenu = null;
            return;
        }

        var enabled = camera.Editor.CurveDocument.DofEnabled;
        var on = new MenuItem { Header = "On", IsChecked = enabled };
        var off = new MenuItem { Header = "Off", IsChecked = !enabled };
        on.Click += (_, _) => camera.Editor.SetDofEnabled(true);
        off.Click += (_, _) => camera.Editor.SetDofEnabled(false);
        var menu = new ContextMenu { ItemsSource = new Control[] { on, off } };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_dofEnabledMenu, menu))
                _dofEnabledMenu = null;
        };
        _dofEnabledMenu = menu;
        menu.Open(this);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_valueEditors.Any(entry => entry.Field.IsKeyboardFocusWithin)
            || _modeEditors.Any(entry => entry.ComboBox.IsKeyboardFocusWithin))
            return;
        if (e.Key != Key.Delete || Sequence == null)
            return;
        if (_selectedCut != null)
        {
            Sequence.BeginHistoryTransaction();
            Sequence.CameraCuts.Remove(_selectedCut);
            Sequence.CommitHistoryTransaction();
            _selectedCut = null;
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        var selected = SelectedTimelineKeys().ToList();
        if (selected.Count == 0)
            return;
        Sequence.BeginHistoryTransaction();
        foreach (var editorGroup in selected.GroupBy(key => key.Editor))
        {
            var editor = editorGroup.Key;
            editor.BeginHistoryTransaction();
            var keys = editorGroup.ToList();
            if (editor.IsCurveMode)
            {
                var affectedChannels = new List<CampathCurveChannel>();
                foreach (var channel in editor.CurveDocument.Channels)
                {
                    var removedKeys = keys.Select(item => item.CurveKey)
                        .Where(key => key != null && channel.Keys.Contains(key))
                        .Distinct()
                        .ToList();
                    if (removedKeys.Count == 0)
                        continue;
                    foreach (var key in removedKeys)
                        channel.Keys.Remove(key!);
                    affectedChannels.Add(channel);
                }
                foreach (var channel in affectedChannels)
                    CampathPathConversion.AutoTangents(channel);
                editor.NotifyCurveDocumentChanged();
            }
            else
            {
                if (keys.Any(key => ReferenceEquals(key.ClassicKey, editor.SelectedKeyframe)))
                    editor.SelectedKeyframe = null;
                foreach (var key in keys.Select(item => item.ClassicKey)
                             .Where(key => key != null).Distinct().ToList())
                    editor.Keyframes.Remove(key!);
            }
            editor.CommitHistoryTransaction();
        }
        Sequence.CommitHistoryTransaction();
        e.Handled = true;
        PublishGizmoSelection();
        UpdateValueEditors(force: true);
        InvalidateVisual();
    }

    private IEnumerable<TimelineKey> AllTimelineKeys()
    {
        if (Sequence == null)
            yield break;
        foreach (var camera in Sequence.Cameras)
        {
            if (camera.Editor.IsCurveMode)
            {
                foreach (var channel in camera.Editor.CurveDocument.Channels)
                    foreach (var key in channel.Keys)
                        yield return new TimelineKey(camera.Editor, null, key, channel, null);
            }
            else
            {
                foreach (var key in camera.Editor.Keyframes)
                {
                    yield return new TimelineKey(camera.Editor, key, null, null, "camera");
                    foreach (var group in GetGroups(camera))
                        yield return new TimelineKey(camera.Editor, key, null, null, $"group:{group}");
                    foreach (var channel in camera.Editor.CurveDocument.Channels)
                        yield return new TimelineKey(camera.Editor, key, null, null, $"channel:{channel.Id}");
                }
            }
        }
    }

    private IEnumerable<TimelineKey> SelectedTimelineKeys()
    {
        if (Sequence == null)
            yield break;
        PruneClassicSelections();
        foreach (var camera in Sequence.Cameras)
        {
            if (camera.Editor.IsCurveMode)
            {
                foreach (var channel in camera.Editor.CurveDocument.Channels)
                    foreach (var key in channel.Keys)
                    {
                        var timelineKey = new TimelineKey(camera.Editor, null, key, channel, null);
                        if (IsKeySelected(timelineKey))
                            yield return timelineKey;
                    }
            }
            else
            {
                foreach (var selection in _classicSelections
                             .Where(selection => ReferenceEquals(selection.Editor, camera.Editor)))
                    yield return new TimelineKey(selection.Editor, selection.Key, null, null, selection.Scope);
            }
        }
    }

    private bool IsKeySelected(TimelineKey key) =>
        key.ClassicKey != null
            ? _classicSelections.Contains(new ClassicSelection(key.Editor, key.ClassicKey, key.ClassicScope ?? "camera"))
            : key.CurveKey?.Selected == true;

    private bool IsMovingKey(TimelineKey candidate) =>
        _keyDragOrigins.Keys.Any(moving =>
            candidate.ClassicKey != null
                ? ReferenceEquals(candidate.ClassicKey, moving.ClassicKey)
                : ReferenceEquals(candidate.CurveKey, moving.CurveKey));

    private void PruneClassicSelections()
    {
        _classicSelections.RemoveWhere(selection => !selection.Editor.Keyframes.Contains(selection.Key));
    }

    private void SetKeySelected(TimelineKey key, bool selected)
    {
        if (key.ClassicKey != null)
        {
            var selection = new ClassicSelection(key.Editor, key.ClassicKey, key.ClassicScope ?? "camera");
            if (selected)
            {
                _classicSelections.Add(selection);
                key.Editor.SelectedKeyframe = key.ClassicKey;
            }
            else
            {
                _classicSelections.Remove(selection);
            }
            return;
        }
        if (key.CurveKey != null)
            key.CurveKey.Selected = selected;
    }

    private void ClearKeySelection()
    {
        if (Sequence == null)
            return;
        _classicSelections.Clear();
        _linkedClassicDragKeys.Clear();
        foreach (var camera in Sequence.Cameras)
        {
            if (!camera.Editor.IsCurveMode)
                camera.Editor.SelectedKeyframe = null;
            foreach (var key in camera.Editor.Keyframes)
                key.Selected = false;
            foreach (var key in camera.Editor.CurveDocument.Channels.SelectMany(channel => channel.Keys))
                key.Selected = false;
        }
        PublishGizmoSelection();
        UpdateValueEditors();
    }

    private void PublishGizmoSelection()
    {
        if (Sequence == null)
            return;

        var selected = SelectedTimelineKeys().ToList();
        if (selected.Count == 0)
        {
            Sequence.SetGizmoSelection(null);
            return;
        }

        var editor = selected[0].Editor;
        if (selected.Any(key => !ReferenceEquals(key.Editor, editor)))
        {
            Sequence.SetGizmoSelection(null);
            return;
        }

        const double timeEpsilon = 0.0001;
        var clusters = selected
            .GroupBy(key => Math.Round(key.Time, 4))
            .OrderBy(cluster => cluster.Key)
            .Select(cluster => cluster.ToList())
            .ToList();

        var targets = new List<SequencerGizmoTarget>();
        var allTranslationAxes = CampathGizmoAxes.None;
        var allRotationAxes = CampathGizmoAxes.None;
        foreach (var cluster in clusters)
        {
            var translationAxes = CampathGizmoAxes.None;
            var rotationAxes = CampathGizmoAxes.None;
            var curveKeys = new Dictionary<string, CampathCurveKey>();
            CampathKeyframeViewModel? classicKey = null;
            foreach (var key in cluster)
            {
                if (key.ClassicKey != null)
                {
                    if (classicKey != null && !ReferenceEquals(classicKey, key.ClassicKey))
                    {
                        Sequence.SetGizmoSelection(null);
                        return;
                    }
                    classicKey = key.ClassicKey;
                    AddScopeAxes(key.ClassicScope, ref translationAxes, ref rotationAxes);
                }
                else if (key.CurveKey != null && key.CurveChannel != null)
                {
                    curveKeys[key.CurveChannel.Id] = key.CurveKey;
                    AddChannelAxes(key.CurveChannel.Id, ref translationAxes, ref rotationAxes);
                }
            }

            if (translationAxes == CampathGizmoAxes.None && rotationAxes == CampathGizmoAxes.None)
                continue;
            var target = new SequencerGizmoTarget(
                cluster.Average(key => key.Time), classicKey, curveKeys,
                translationAxes, rotationAxes);
            targets.Add(target);
            allTranslationAxes |= translationAxes;
            allRotationAxes |= rotationAxes;
        }

        if (targets.Count == 0)
        {
            Sequence.SetGizmoSelection(null);
            return;
        }

        var previous = Sequence.GizmoSelection;
        double? pivotAnchorTime = null;
        if (targets.Count == 1)
        {
            pivotAnchorTime = targets[0].Time;
        }
        else if (previous?.PivotAnchorTime is { } previousAnchor
            && targets.Any(target => Math.Abs(target.Time - previousAnchor) <= timeEpsilon))
        {
            pivotAnchorTime = previousAnchor;
        }
        var centerRotation = pivotAnchorTime == null
            && previous is { PivotAnchorTime: null, Targets.Count: > 1 }
            ? previous.CenterRotation
            : Quaternion.Identity;

        Sequence.SetGizmoSelection(new SequencerGizmoSelection(
            editor, targets, allTranslationAxes, allRotationAxes, pivotAnchorTime, centerRotation));
    }

    private static void AddScopeAxes(string? scope,
        ref CampathGizmoAxes translationAxes, ref CampathGizmoAxes rotationAxes)
    {
        if (scope == "camera")
        {
            translationAxes = CampathGizmoAxes.All;
            rotationAxes = CampathGizmoAxes.All;
            return;
        }
        if (scope == "group:Position")
        {
            translationAxes = CampathGizmoAxes.All;
            return;
        }
        if (scope == "group:Rotation")
        {
            rotationAxes = CampathGizmoAxes.All;
            return;
        }
        const string channelPrefix = "channel:";
        if (scope?.StartsWith(channelPrefix, StringComparison.Ordinal) == true)
            AddChannelAxes(scope[channelPrefix.Length..], ref translationAxes, ref rotationAxes);
    }

    private static void AddChannelAxes(string channelId,
        ref CampathGizmoAxes translationAxes, ref CampathGizmoAxes rotationAxes)
    {
        switch (channelId)
        {
            case "position.x": translationAxes |= CampathGizmoAxes.X; break;
            case "position.y": translationAxes |= CampathGizmoAxes.Y; break;
            case "position.z": translationAxes |= CampathGizmoAxes.Z; break;
            case "rotation.roll": rotationAxes |= CampathGizmoAxes.X; break;
            case "rotation.pitch": rotationAxes |= CampathGizmoAxes.Y; break;
            case "rotation.yaw": rotationAxes |= CampathGizmoAxes.Z; break;
        }
    }

    private double SnapThresholdTime => SecondsPerPixel * 10.0;

    private IEnumerable<double> GetSnapTimes(
        Func<TimelineKey, bool>? keyPredicate = null,
        CameraCutSectionViewModel? excludedCut = null)
    {
        foreach (var key in AllTimelineKeys())
            if (keyPredicate?.Invoke(key) != false)
                yield return key.Time;
        if (Sequence == null)
            yield break;
        foreach (var cut in Sequence.CameraCuts)
        {
            if (ReferenceEquals(cut, excludedCut))
                continue;
            yield return cut.StartTime;
            yield return cut.EndTime;
        }
    }

    private double SnapTime(double time, IEnumerable<double> candidates)
    {
        var best = time;
        var bestDistance = SnapThresholdTime;
        foreach (var candidate in candidates)
        {
            var distance = Math.Abs(candidate - time);
            if (distance > bestDistance)
                continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    private static double FindSnapAdjustment(IEnumerable<double> movingTimes,
        IEnumerable<double> candidates, double threshold)
    {
        var targets = candidates.Distinct().ToList();
        var bestAdjustment = 0.0;
        var bestDistance = threshold;
        foreach (var movingTime in movingTimes)
        {
            foreach (var target in targets)
            {
                var adjustment = target - movingTime;
                var distance = Math.Abs(adjustment);
                if (distance > bestDistance)
                    continue;
                bestAdjustment = adjustment;
                bestDistance = distance;
            }
        }
        return bestAdjustment;
    }

    private static Rect RectFromPoints(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void OnSequenceChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is CampathSequenceViewModel oldSequence)
        {
            if (_subscriptionsAttached)
                oldSequence.PropertyChanged -= OnSequencePropertyChanged;
            oldSequence.SetGizmoSelection(null);
        }
        if (_subscriptionsAttached && e.NewValue is CampathSequenceViewModel newSequence)
        {
            newSequence.PropertyChanged += OnSequencePropertyChanged;
            foreach (var camera in newSequence.Cameras)
                ShowAllCurveChannels(camera);
        }
        _selectedCurveTracks.Clear();
        _trackSelectionAnchor = null;
        RefreshCurveSelectionSubscriptions();
        PublishGizmoSelection();
        EnsureValueEditorLayout();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnSequencePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathSequenceViewModel.ContentEnd))
            RefreshCurveSelectionSubscriptions();
        if (e.PropertyName == nameof(CampathSequenceViewModel.SelectedCamera))
        {
            ClearCurveTrackSelection();
            if (Sequence?.SelectedCamera is { } selectedCamera)
                ShowAllCurveChannels(selectedCamera);
            PublishGizmoSelection();
        }
        EnsureValueEditorLayout();
        UpdateValueEditors();
        if (e.PropertyName != nameof(CampathSequenceViewModel.PlayheadTime)
            && e.PropertyName != nameof(CampathSequenceViewModel.IsPlaying)
            && e.PropertyName != nameof(CampathSequenceViewModel.IsPiloting)
            && e.PropertyName != nameof(CampathSequenceViewModel.Possession)
            && e.PropertyName != nameof(CampathSequenceViewModel.PossessionKind)
            && e.PropertyName != nameof(CampathSequenceViewModel.SelectedCamera))
            InvalidateMeasure();
        InvalidateVisual();
    }

    private void RefreshCurveSelectionSubscriptions()
    {
        var desiredCollections = (_subscriptionsAttached ? Sequence : null)?.Cameras
            .SelectMany(camera => camera.Editor.CurveDocument.Channels)
            .Select(channel => channel.Keys)
            .ToHashSet() ?? [];
        foreach (var collection in _observedCurveKeyCollections
                     .Where(collection => !desiredCollections.Contains(collection)).ToList())
        {
            collection.CollectionChanged -= OnObservedCurveKeysChanged;
            _observedCurveKeyCollections.Remove(collection);
        }
        foreach (var collection in desiredCollections
                     .Where(collection => !_observedCurveKeyCollections.Contains(collection)))
        {
            collection.CollectionChanged += OnObservedCurveKeysChanged;
            _observedCurveKeyCollections.Add(collection);
        }

        var desiredKeys = desiredCollections.SelectMany(collection => collection).ToHashSet();
        foreach (var key in _observedCurveKeys.Where(key => !desiredKeys.Contains(key)).ToList())
        {
            key.PropertyChanged -= OnObservedCurveKeyChanged;
            _observedCurveKeys.Remove(key);
        }
        foreach (var key in desiredKeys.Where(key => !_observedCurveKeys.Contains(key)))
        {
            key.PropertyChanged += OnObservedCurveKeyChanged;
            _observedCurveKeys.Add(key);
        }
    }

    private void OnObservedCurveKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (CampathCurveKey key in e.OldItems)
            {
                key.PropertyChanged -= OnObservedCurveKeyChanged;
                _observedCurveKeys.Remove(key);
            }
        if (e.NewItems != null)
            foreach (CampathCurveKey key in e.NewItems)
            {
                key.PropertyChanged += OnObservedCurveKeyChanged;
                _observedCurveKeys.Add(key);
            }
        QueueCurveSelectionRefresh();
    }

    private void OnObservedCurveKeyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CampathCurveKey.Selected))
            return;
        QueueCurveSelectionRefresh();
    }

    private void QueueCurveSelectionRefresh()
    {
        if (_curveSelectionRefreshPending)
            return;
        _curveSelectionRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _curveSelectionRefreshPending = false;
            PublishGizmoSelection();
            UpdateValueEditors();
            InvalidateVisual();
        }, DispatcherPriority.Input);
    }

    private enum RowKind { Cuts, Camera, Group, Channel }
    private enum CutDragMode { None, Move, ResizeStart, ResizeEnd }
    private sealed record TrackSelection(Guid CameraId, RowKind Kind, string Value);
    private sealed record TrackSelectionAnchor(Guid CameraId, RowKind Kind, string Value);
    private sealed record RowHit(Rect Bounds, RowKind Kind, CampathCameraTrackViewModel? Camera, string? Value);
    private sealed record KeyHit(Rect Bounds, KeyBundle Bundle);
    private sealed record CutHit(Rect Bounds, CameraCutSectionViewModel Cut);
    private sealed record KeyBundle(
        double Time,
        IReadOnlyList<TimelineKey> Keys,
        string? MarkerColor = null,
        double? MarkerRadius = null);
    private sealed record TimelineKey(
        CampathEditorViewModel Editor,
        CampathKeyframeViewModel? ClassicKey,
        CampathCurveKey? CurveKey,
        CampathCurveChannel? CurveChannel,
        string? ClassicScope)
    {
        public double Time
        {
            get => ClassicKey?.Time ?? CurveKey?.Time ?? 0.0;
            set
            {
                if (ClassicKey != null)
                    ClassicKey.Time = value;
                else if (CurveKey != null)
                    CurveKey.Time = value;
            }
        }

    }

    private sealed record ClassicSelection(
        CampathEditorViewModel Editor,
        CampathKeyframeViewModel Key,
        string Scope);

    private sealed class ValueEditorEntry(
        CampathCameraTrackViewModel camera,
        CampathCurveChannel channel,
        ScrubbyNumericField field,
        Rect bounds)
    {
        public CampathCameraTrackViewModel Camera { get; } = camera;
        public CampathCurveChannel Channel { get; } = channel;
        public ScrubbyNumericField Field { get; } = field;
        public Rect Bounds { get; } = bounds;
        public bool EditActive { get; set; }
    }

    private sealed class ModeEditorEntry(
        CampathCameraTrackViewModel camera,
        ComboBox comboBox,
        Rect bounds)
    {
        public CampathCameraTrackViewModel Camera { get; } = camera;
        public ComboBox ComboBox { get; } = comboBox;
        public Rect Bounds { get; } = bounds;
    }

    private sealed class TimelineDrawingSurface(CampathSequenceTimelineControl owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            owner.RenderTimeline(context);
        }
    }
}
