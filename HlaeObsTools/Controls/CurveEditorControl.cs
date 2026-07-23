using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HlaeObsTools.Services.Campaths;

namespace HlaeObsTools.Controls;

public enum CurveWeightSelectionState { None, Unweighted, Mixed, Weighted }

public sealed class CurveEditorControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<CampathCurveChannel>?> ChannelsProperty =
        AvaloniaProperty.Register<CurveEditorControl, IReadOnlyList<CampathCurveChannel>?>(nameof(Channels));
    public static readonly StyledProperty<double> PlayheadTimeProperty =
        AvaloniaProperty.Register<CurveEditorControl, double>(nameof(PlayheadTime), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<CurveEditorViewMode> ViewModeProperty =
        AvaloniaProperty.Register<CurveEditorControl, CurveEditorViewMode>(nameof(ViewMode));
    public static readonly StyledProperty<bool> SnapEnabledProperty =
        AvaloniaProperty.Register<CurveEditorControl, bool>(nameof(SnapEnabled), true);
    public static readonly StyledProperty<double> SnapIntervalProperty =
        AvaloniaProperty.Register<CurveEditorControl, double>(nameof(SnapInterval), 0.1);
    public static readonly StyledProperty<int> FitAllRequestProperty =
        AvaloniaProperty.Register<CurveEditorControl, int>(nameof(FitAllRequest));
    public static readonly StyledProperty<int> FitSelectionRequestProperty =
        AvaloniaProperty.Register<CurveEditorControl, int>(nameof(FitSelectionRequest));
    public static readonly StyledProperty<double> StackedChannelHeightProperty =
        AvaloniaProperty.Register<CurveEditorControl, double>(nameof(StackedChannelHeight), 80);

    private const double LeftGutter = 55;
    private const double BottomGutter = 22;
    private double _timeMin;
    private double _timeMax = 10;
    private double _valueMin = -1;
    private double _valueMax = 1;
    private double _normalizedCenter;
    private double _normalizedSpan = 2;
    private double _stackedScrollOffset;
    private Point _lastPointer;
    private bool _panning;
    private bool _draggingPlayhead;
    private bool _freecamPreviewActive;
    private bool _campathPreviewActive;
    private Rect _playheadHandleRect;
    private bool _boxSelecting;
    private bool _boxAdditive;
    private Point _boxStart;
    private Rect _selectionBox;
    private CampathCurveKey? _dragKey;
    private CampathCurveChannel? _dragChannel;
    private TangentSide _dragTangent;
    private Point _dragStartPoint;
    private DragAxis _dragAxis;
    private bool _historyEditActive;
    private readonly Dictionary<CampathCurveKey, (CampathCurveChannel channel, double time, double value)> _dragOrigins = new();
    private readonly HashSet<CampathCurveKey> _selectionBeforeBox = new();
    private readonly List<(Rect rect, CampathCurveChannel channel, CampathCurveKey key)> _keyHits = new();
    private readonly List<(Rect rect, CampathCurveChannel channel, CampathCurveKey key, TangentSide side)> _tangentHits = new();

    private enum TangentSide { None, In, Out }
    private enum DragAxis { Free, Horizontal, Vertical }

    static CurveEditorControl()
    {
        AffectsRender<CurveEditorControl>(ChannelsProperty, PlayheadTimeProperty, ViewModeProperty, SnapEnabledProperty,
            SnapIntervalProperty, FitAllRequestProperty, FitSelectionRequestProperty, StackedChannelHeightProperty);
        ChannelsProperty.Changed.AddClassHandler<CurveEditorControl>((c, e) => c.OnChannelsChanged(e));
        FitAllRequestProperty.Changed.AddClassHandler<CurveEditorControl>((c, _) => c.FitAll());
        FitSelectionRequestProperty.Changed.AddClassHandler<CurveEditorControl>((c, _) => c.FitSelection());
    }

    public CurveEditorControl() { Focusable = true; ClipToBounds = true; }
    public IReadOnlyList<CampathCurveChannel>? Channels { get => GetValue(ChannelsProperty); set => SetValue(ChannelsProperty, value); }
    public double PlayheadTime { get => GetValue(PlayheadTimeProperty); set => SetValue(PlayheadTimeProperty, value); }
    public CurveEditorViewMode ViewMode { get => GetValue(ViewModeProperty); set => SetValue(ViewModeProperty, value); }
    public bool SnapEnabled { get => GetValue(SnapEnabledProperty); set => SetValue(SnapEnabledProperty, value); }
    public double SnapInterval { get => GetValue(SnapIntervalProperty); set => SetValue(SnapIntervalProperty, value); }
    public int FitAllRequest { get => GetValue(FitAllRequestProperty); set => SetValue(FitAllRequestProperty, value); }
    public int FitSelectionRequest { get => GetValue(FitSelectionRequestProperty); set => SetValue(FitSelectionRequestProperty, value); }
    public double StackedChannelHeight { get => GetValue(StackedChannelHeightProperty); set => SetValue(StackedChannelHeightProperty, value); }
    public event Action? SelectionChanged;
    public event Action<double>? FreecamPreviewRequested;
    public event Action? FreecamPreviewEnded;
    public event Action? CampathPreviewRequested;
    public event Action? CampathPreviewEnded;
    public event Action? PlayheadDragEnded;
    public event Action? HistoryEditStarted;
    public event Action? HistoryEditCompleted;

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#111318")), new Rect(Bounds.Size));
        if (Bounds.Width <= LeftGutter + 20 || Bounds.Height <= BottomGutter + 20) return;
        var plot = new Rect(LeftGutter, 5, Bounds.Width - LeftGutter - 5, Bounds.Height - BottomGutter - 5);
        var visible = Channels?.Where(c => c.IsVisible).ToList() ?? [];
        ClampStackedScroll(plot, visible.Count);
        DrawGrid(context, plot, visible);
        _keyHits.Clear(); _tangentHits.Clear();
        for (var i = 0; i < visible.Count; i++) DrawChannel(context, plot, visible[i], i, visible.Count);
        var playX = TimeToX(PlayheadTime, plot);
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#FFB84D")), 1.3), new Point(playX, plot.Top), new Point(playX, plot.Bottom));
        var playheadHead = new StreamGeometry();
        using (var geometry = playheadHead.Open())
        {
            geometry.BeginFigure(new Point(playX - 7, plot.Top), true);
            geometry.LineTo(new Point(playX + 7, plot.Top));
            geometry.LineTo(new Point(playX, plot.Top + 12));
            geometry.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#FFB84D")), null, playheadHead);
        _playheadHandleRect = new Rect(playX - 9, plot.Top - 2, 18, 17);
        if (_boxSelecting)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(35, 100, 170, 255)), _selectionBox);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#64AAFF")), 1), _selectionBox);
        }
    }

    private void DrawGrid(DrawingContext context, Rect plot, IReadOnlyList<CampathCurveChannel> visible)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#292C33")), 1);
        var majorPen = new Pen(new SolidColorBrush(Color.Parse("#3A3E47")), 1);
        var timeStep = NiceStep((_timeMax - _timeMin) / Math.Max(2, plot.Width / 90));
        for (var t = Math.Floor(_timeMin / timeStep) * timeStep; t <= _timeMax; t += timeStep)
        {
            var x = TimeToX(t, plot); context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            DrawText(context, t.ToString("0.##", CultureInfo.InvariantCulture), new Point(x + 3, plot.Bottom + 3), Brushes.Gray, 10);
        }
        if (ViewMode == CurveEditorViewMode.Absolute)
        {
            var step = NiceStep((_valueMax - _valueMin) / Math.Max(2, plot.Height / 55));
            for (var v = Math.Floor(_valueMin / step) * step; v <= _valueMax; v += step)
            {
                var y = ValueToY(null, v, plot, 0, 1); context.DrawLine(Math.Abs(v) < step * .1 ? majorPen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawText(context, v.ToString("0.##", CultureInfo.InvariantCulture), new Point(3, y - 7), Brushes.Gray, 10);
            }
        }
        else if (ViewMode == CurveEditorViewMode.Normalized)
        {
            var min = _normalizedCenter - _normalizedSpan * .5;
            var max = _normalizedCenter + _normalizedSpan * .5;
            var step = NiceStep((max - min) / Math.Max(2, plot.Height / 55));
            for (var value = Math.Floor(min / step) * step; value <= max; value += step)
            {
                var y = NormalizedToY(value, plot);
                context.DrawLine(Math.Abs(value) < step * .1 ? majorPen : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                DrawText(context, value.ToString("0.##", CultureInfo.InvariantCulture), new Point(3, y - 7), Brushes.Gray, 10);
            }
        }
        else
        {
            for (var i = 0; i < visible.Count; i++)
            {
                var top = plot.Top + i * StackedChannelHeight - _stackedScrollOffset;
                var bottom = top + StackedChannelHeight;
                if (bottom < plot.Top || top > plot.Bottom) continue;
                if ((i & 1) != 0) context.FillRectangle(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), new Rect(plot.Left, Math.Max(plot.Top, top), plot.Width, Math.Min(plot.Bottom, bottom) - Math.Max(plot.Top, top)));
                context.DrawLine(majorPen, new Point(plot.Left, top), new Point(plot.Right, top));
                context.DrawLine(gridPen, new Point(plot.Left, top + StackedChannelHeight * .5), new Point(plot.Right, top + StackedChannelHeight * .5));
                DrawText(context, visible[i].Name, new Point(plot.Left + 5, top + 4), new SolidColorBrush(Color.Parse(visible[i].Color)), 10);
            }
        }
        context.DrawRectangle(null, majorPen, plot);
    }

    private void DrawChannel(DrawingContext context, Rect plot, CampathCurveChannel channel, int channelIndex, int channelCount)
    {
        if (channel.Keys.Count == 0) return;
        var color = Color.Parse(channel.Color);
        var pen = new Pen(new SolidColorBrush(color), 1.7);
        Point? previous = null;
        var samples = Math.Clamp((int)plot.Width / 3, 32, 600);
        for (var i = 0; i <= samples; i++)
        {
            var time = _timeMin + (_timeMax - _timeMin) * i / samples;
            var p = new Point(TimeToX(time, plot), ValueToY(channel, channel.Evaluate(time), plot, channelIndex, channelCount));
            if (previous != null) context.DrawLine(pen, previous.Value, p);
            previous = p;
        }
        foreach (var key in channel.Keys)
        {
            var p = KeyPoint(channel, key, plot, channelIndex, channelCount);
            var size = key.Selected ? 9.0 : 7.0;
            var rect = new Rect(p.X - size / 2, p.Y - size / 2, size, size);
            context.FillRectangle(key.Selected ? Brushes.White : new SolidColorBrush(color), rect);
            context.DrawRectangle(null, pen, rect);
            _keyHits.Add((rect.Inflate(4), channel, key));
            if (key.Selected) DrawTangents(context, plot, channel, key, channelIndex, channelCount, p, color);
        }
    }

    private void DrawTangents(DrawingContext context, Rect plot, CampathCurveChannel channel, CampathCurveKey key, int index, int count, Point keyPoint, Color color)
    {
        var fixedTimeLength = (_timeMax - _timeMin) * 50.0 / Math.Max(1, plot.Width);
        var inTime = key.Time - (key.WeightedTangents ? key.InWeight : fixedTimeLength);
        var outTime = key.Time + (key.WeightedTangents ? key.OutWeight : fixedTimeLength);
        var inPoint = new Point(TimeToX(inTime, plot), ValueToY(channel, key.Value - key.InTangent * (key.Time - inTime), plot, index, count));
        var outPoint = new Point(TimeToX(outTime, plot), ValueToY(channel, key.Value + key.OutTangent * (outTime - key.Time), plot, index, count));
        var tangentPen = new Pen(new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B)), 1);
        context.DrawLine(tangentPen, inPoint, keyPoint); context.DrawLine(tangentPen, keyPoint, outPoint);
        AddHandle(context, inPoint, channel, key, TangentSide.In); AddHandle(context, outPoint, channel, key, TangentSide.Out);
    }

    private void AddHandle(DrawingContext context, Point p, CampathCurveChannel channel, CampathCurveKey key, TangentSide side)
    {
        var rect = new Rect(p.X - 4, p.Y - 4, 8, 8); context.DrawEllipse(Brushes.White, null, p, 4, 4); _tangentHits.Add((rect.Inflate(4), channel, key, side));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus(); var point = e.GetPosition(this); var props = e.GetCurrentPoint(this).Properties;
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed) { _panning = true; _lastPointer = point; e.Pointer.Capture(this); e.Handled = true; return; }
        if (!props.IsLeftButtonPressed) return;
        if (_playheadHandleRect.Contains(point))
        {
            _draggingPlayhead = true; e.Pointer.Capture(this); e.Handled = true; return;
        }
        var tangent = _tangentHits.LastOrDefault(h => h.rect.Contains(point));
        if (tangent.key != null) { _dragKey = tangent.key; _dragChannel = tangent.channel; _dragTangent = tangent.side; _dragStartPoint = point; BeginHistoryEdit(); e.Pointer.Capture(this); e.Handled = true; return; }
        var hit = _keyHits.LastOrDefault(h => h.rect.Contains(point));
        if (hit.key != null)
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) ClearSelection();
            hit.key.Selected = true;
            _dragKey = hit.key; _dragChannel = hit.channel; _lastPointer = point; _dragStartPoint = point; _dragAxis = DragAxis.Free;
            _dragOrigins.Clear();
            foreach (var channel in Channels ?? [])
                foreach (var key in channel.Keys.Where(key => key.Selected))
                    _dragOrigins[key] = (channel, key.Time, key.Value);
            BeginHistoryEdit();
            e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true;
            SelectionChanged?.Invoke();
        }
        else
        {
            _boxSelecting = true; _boxAdditive = e.KeyModifiers.HasFlag(KeyModifiers.Shift); _boxStart = point; _selectionBox = new Rect(point, point);
            _selectionBeforeBox.Clear(); foreach (var key in VisibleKeys().Where(key => key.Selected)) _selectionBeforeBox.Add(key);
            if (!_boxAdditive) ClearSelection();
            e.Pointer.Capture(this); InvalidateVisual(); SelectionChanged?.Invoke(); e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var point = e.GetPosition(this); var plot = PlotRect();
        if (_panning)
        {
            var delta = point - _lastPointer; _lastPointer = point;
            var dt = -delta.X / plot.Width * (_timeMax - _timeMin); _timeMin += dt; _timeMax += dt;
            if (ViewMode == CurveEditorViewMode.Absolute) { var dv = delta.Y / plot.Height * (_valueMax - _valueMin); _valueMin += dv; _valueMax += dv; }
            else if (ViewMode == CurveEditorViewMode.Normalized) _normalizedCenter += delta.Y / plot.Height * _normalizedSpan;
            else _stackedScrollOffset = Math.Max(0, _stackedScrollOffset - delta.Y);
            InvalidateVisual(); e.Handled = true; return;
        }
        if (_draggingPlayhead)
        {
            var time = Math.Max(0, XToTime(point.X, plot));
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                var nearest = FindNearestVisibleKeyTime(time);
                if (nearest.HasValue) time = nearest.Value;
            }
            PlayheadTime = time;

            var ctrlDown = e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (ctrlDown && !_freecamPreviewActive) _freecamPreviewActive = true;
            else if (!ctrlDown && _freecamPreviewActive) { _freecamPreviewActive = false; FreecamPreviewEnded?.Invoke(); }
            if (_freecamPreviewActive) FreecamPreviewRequested?.Invoke(time);

            var altDown = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (altDown && !ctrlDown && !_campathPreviewActive) { _campathPreviewActive = true; CampathPreviewRequested?.Invoke(); }
            else if ((!altDown || ctrlDown) && _campathPreviewActive) { _campathPreviewActive = false; CampathPreviewEnded?.Invoke(); }
            e.Handled = true; return;
        }
        if (_boxSelecting)
        {
            _selectionBox = RectFromPoints(_boxStart, point);
            foreach (var key in VisibleKeys()) key.Selected = _boxAdditive && _selectionBeforeBox.Contains(key);
            foreach (var hit in _keyHits)
                if (_selectionBox.Intersects(hit.rect)) hit.key.Selected = true;
            InvalidateVisual(); SelectionChanged?.Invoke(); e.Handled = true; return;
        }
        if (_dragKey == null || _dragChannel == null) return;
        if (_dragTangent != TangentSide.None)
        {
            var dt = XToTime(point.X, plot) - _dragKey.Time;
            if (Math.Abs(dt) > 1e-6)
            {
                var weight = Math.Abs(dt);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    SetTangentWeight(_dragKey, _dragTangent, weight, !e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                }
                else
                {
                    var value = YToValue(_dragChannel, point.Y, plot); var slope = (value - _dragKey.Value) / dt;
                    SetTangentSlope(_dragKey, _dragTangent, slope, !e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                    if (_dragKey.WeightedTangents) SetTangentWeight(_dragKey, _dragTangent, weight, !e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                }
            }
        }
        else
        {
            var delta = point - _dragStartPoint;
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _dragAxis == DragAxis.Free && Math.Abs(delta.X) + Math.Abs(delta.Y) > 4)
                _dragAxis = Math.Abs(delta.X) >= Math.Abs(delta.Y) ? DragAxis.Horizontal : DragAxis.Vertical;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift)) _dragAxis = DragAxis.Free;

            var primaryOrigin = _dragOrigins[_dragKey];
            var deltaTime = delta.X / plot.Width * (_timeMax - _timeMin);
            var candidateTime = primaryOrigin.time + deltaTime;
            if (_dragAxis != DragAxis.Vertical)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                {
                    var snap = FindCrossChannelSnap(_dragChannel, candidateTime, plot);
                    if (snap.HasValue) candidateTime = snap.Value;
                }
                else if (SnapEnabled) candidateTime = Math.Round(candidateTime / SnapInterval) * SnapInterval;
                deltaTime = candidateTime - primaryOrigin.time;
            }

            foreach (var (key, origin) in _dragOrigins)
            {
                if (_dragAxis != DragAxis.Vertical) key.Time = Math.Max(0, origin.time + deltaTime);
                if (_dragAxis != DragAxis.Horizontal)
                {
                    var originalY = ValueToScreenY(origin.channel, origin.value, plot);
                    key.Value = YToValue(origin.channel, originalY + delta.Y, plot);
                }
            }
        }
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragKey != null && _dragTangent == TangentSide.None)
            foreach (var channel in _dragOrigins.Values.Select(value => value.channel).Distinct()) SortKeys(channel);
        EndHistoryEdit();
        if (_draggingPlayhead)
        {
            PlayheadDragEnded?.Invoke();
            if (_freecamPreviewActive) { _freecamPreviewActive = false; FreecamPreviewEnded?.Invoke(); }
            if (_campathPreviewActive) { _campathPreviewActive = false; CampathPreviewEnded?.Invoke(); }
        }
        _panning = false; _draggingPlayhead = false; _boxSelecting = false; _dragKey = null; _dragChannel = null; _dragTangent = TangentSide.None; _dragOrigins.Clear(); e.Pointer.Capture(null); InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); var plot = PlotRect(); var p = e.GetPosition(this); var factor = Math.Pow(1.15, -e.Delta.Y);
        if (ViewMode == CurveEditorViewMode.Stacked && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _stackedScrollOffset -= e.Delta.Y * Math.Max(12, StackedChannelHeight * .35);
            ClampStackedScroll(plot, Channels?.Count(channel => channel.IsVisible) ?? 0); InvalidateVisual(); e.Handled = true; return;
        }
        var horizontal = !e.KeyModifiers.HasFlag(KeyModifiers.Control); var vertical = !e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (horizontal) ZoomRange(ref _timeMin, ref _timeMax, XToTime(p.X, plot), factor);
        if (vertical && ViewMode == CurveEditorViewMode.Absolute) ZoomRange(ref _valueMin, ref _valueMax, YToValue(null, p.Y, plot), factor);
        else if (vertical && ViewMode == CurveEditorViewMode.Normalized)
        {
            var anchor = YToNormalized(p.Y, plot);
            _normalizedCenter = anchor + (_normalizedCenter - anchor) * factor;
            _normalizedSpan = Math.Clamp(_normalizedSpan * factor, .05, 20);
        }
        else if (vertical && ViewMode == CurveEditorViewMode.Stacked)
            StackedChannelHeight = Math.Clamp(StackedChannelHeight / factor, 36, 220);
        InvalidateVisual(); e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F) { if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) FitSelection(); else FitAll(); e.Handled = true; }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { foreach (var k in VisibleKeys()) k.Selected = true; InvalidateVisual(); SelectionChanged?.Invoke(); e.Handled = true; }
        else if (e.Key == Key.Delete)
        {
            var selected = VisibleKeys().Where(key => key.Selected).ToHashSet();
            if (selected.Count == 0) return;
            BeginHistoryEdit();
            foreach (var c in Channels ?? []) foreach (var k in c.Keys.Where(selected.Contains).ToList()) c.Keys.Remove(k);
            EndHistoryEdit();
            InvalidateVisual(); SelectionChanged?.Invoke(); e.Handled = true;
        }
    }

    public CurveWeightSelectionState GetWeightSelectionState()
    {
        var selected = VisibleKeys().Where(key => key.Selected).ToList();
        if (selected.Count == 0) return CurveWeightSelectionState.None;
        var weighted = selected.Count(key => key.WeightedTangents);
        return weighted == 0 ? CurveWeightSelectionState.Unweighted
            : weighted == selected.Count ? CurveWeightSelectionState.Weighted
            : CurveWeightSelectionState.Mixed;
    }

    public void SelectKeys(IEnumerable<CampathCurveKey> keys, bool additive)
    {
        if (!additive) ClearSelection();
        foreach (var key in keys) key.Selected = true;
        SelectionChanged?.Invoke(); InvalidateVisual();
    }

    public void ToggleSelectedWeightedTangents()
    {
        var selected = VisibleKeys().Where(key => key.Selected).ToList();
        if (selected.Count == 0) return;
        BeginHistoryEdit();
        var makeWeighted = selected.Any(key => !key.WeightedTangents);
        foreach (var key in selected) key.WeightedTangents = makeWeighted;
        EndHistoryEdit();
        SelectionChanged?.Invoke(); InvalidateVisual();
    }

    public void FlattenSelectedTangents()
    {
        var selected = VisibleKeys().Where(key => key.Selected).ToList();
        if (selected.Count == 0) return;
        BeginHistoryEdit();
        foreach (var key in selected)
        {
            key.InTangent = 0; key.OutTangent = 0; key.TangentMode = CurveTangentMode.Smooth;
        }
        EndHistoryEdit();
        InvalidateVisual();
    }

    public void StraightenSelectedTangents()
    {
        if (!VisibleKeys().Any(key => key.Selected)) return;
        BeginHistoryEdit();
        foreach (var channel in Channels ?? [])
        {
            var ordered = channel.Keys.OrderBy(key => key.Time).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var key = ordered[i]; if (!key.Selected) continue;
                var previous = ordered[Math.Max(0, i - 1)]; var next = ordered[Math.Min(ordered.Count - 1, i + 1)];
                var dt = next.Time - previous.Time;
                var slope = Math.Abs(dt) < 1e-9 ? 0 : (next.Value - previous.Value) / dt;
                key.InTangent = slope; key.OutTangent = slope; key.TangentMode = CurveTangentMode.Smooth;
            }
        }
        EndHistoryEdit();
        InvalidateVisual();
    }

    private void BeginHistoryEdit()
    {
        if (_historyEditActive) return;
        _historyEditActive = true;
        HistoryEditStarted?.Invoke();
    }

    private void EndHistoryEdit()
    {
        if (!_historyEditActive) return;
        _historyEditActive = false;
        HistoryEditCompleted?.Invoke();
    }

    private double? FindCrossChannelSnap(CampathCurveChannel source, double candidate, Rect plot)
    {
        var threshold = 10.0 / Math.Max(1, plot.Width) * (_timeMax - _timeMin);
        var nearest = (Channels ?? []).Where(channel => !ReferenceEquals(channel, source) && channel.IsVisible)
            .SelectMany(channel => channel.Keys).Select(key => key.Time)
            .OrderBy(time => Math.Abs(time - candidate)).FirstOrDefault(double.NaN);
        return !double.IsNaN(nearest) && Math.Abs(nearest - candidate) <= threshold ? nearest : null;
    }

    private double? FindNearestVisibleKeyTime(double time)
    {
        var times = VisibleKeys().Select(key => key.Time).ToList();
        return times.Count == 0 ? null : times.OrderBy(candidate => Math.Abs(candidate - time)).First();
    }

    private static void SetTangentSlope(CampathCurveKey key, TangentSide side, double slope, bool linked)
    {
        if (side == TangentSide.In) key.InTangent = slope; else key.OutTangent = slope;
        if (linked) { key.InTangent = slope; key.OutTangent = slope; key.TangentMode = CurveTangentMode.Smooth; }
        else key.TangentMode = CurveTangentMode.Broken;
    }

    private static void SetTangentWeight(CampathCurveKey key, TangentSide side, double weight, bool linked)
    {
        if (side == TangentSide.In) key.InWeight = weight; else key.OutWeight = weight;
        if (linked) { key.InWeight = weight; key.OutWeight = weight; }
    }

    private static Rect RectFromPoints(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void SortKeys(CampathCurveChannel channel)
    {
        var ordered = channel.Keys.OrderBy(key => key.Time).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = channel.Keys.IndexOf(ordered[i]);
            if (current != i) channel.Keys.Move(current, i);
        }
    }

    public void FitAll() => FitKeys(VisibleKeys().ToList());
    public void FitSelection() { var keys = VisibleKeys().Where(k => k.Selected).ToList(); if (keys.Count > 0) FitKeys(keys); }
    private void FitKeys(IReadOnlyList<CampathCurveKey> keys)
    {
        if (keys.Count == 0) return; var minT = keys.Min(k => k.Time); var maxT = keys.Max(k => k.Time); var minV = keys.Min(k => k.Value); var maxV = keys.Max(k => k.Value);
        var padT = Math.Max(.25, (maxT - minT) * .08); var padV = Math.Max(1, (maxV - minV) * .08);
        _timeMin = Math.Max(0, minT - padT); _timeMax = maxT + padT; _valueMin = minV - padV; _valueMax = maxV + padV;
        _normalizedCenter = 0; _normalizedSpan = 2.2; _stackedScrollOffset = 0; InvalidateVisual();
    }

    private IEnumerable<CampathCurveKey> VisibleKeys() => Channels?.Where(c => c.IsVisible).SelectMany(c => c.Keys) ?? [];
    private void ClearSelection() { foreach (var k in Channels?.SelectMany(c => c.Keys) ?? []) k.Selected = false; }
    private Rect PlotRect() => new(LeftGutter, 5, Math.Max(1, Bounds.Width - LeftGutter - 5), Math.Max(1, Bounds.Height - BottomGutter - 5));
    private Point KeyPointFor(CampathCurveChannel c, CampathCurveKey k, Rect plot) { var visible = Channels?.Where(x => x.IsVisible).ToList() ?? []; return KeyPoint(c, k, plot, Math.Max(0, visible.IndexOf(c)), Math.Max(1, visible.Count)); }
    private Point KeyPoint(CampathCurveChannel c, CampathCurveKey k, Rect plot, int i, int n) => new(TimeToX(k.Time, plot), ValueToY(c, k.Value, plot, i, n));
    private double TimeToX(double t, Rect p) => p.Left + (t - _timeMin) / Math.Max(1e-9, _timeMax - _timeMin) * p.Width;
    private double XToTime(double x, Rect p) => _timeMin + (x - p.Left) / p.Width * (_timeMax - _timeMin);
    private double ValueToY(CampathCurveChannel? c, double v, Rect p, int index, int count)
    {
        if (ViewMode == CurveEditorViewMode.Absolute || c == null) return p.Bottom - (v - _valueMin) / Math.Max(1e-9, _valueMax - _valueMin) * p.Height;
        var min = c.Keys.Count == 0 ? 0 : c.Keys.Min(k => k.Value); var max = c.Keys.Count == 0 ? 1 : c.Keys.Max(k => k.Value); if (Math.Abs(max - min) < 1e-9) { min -= 1; max += 1; }
        var normalized = (v - min) / (max - min) * 2.0 - 1.0;
        if (ViewMode == CurveEditorViewMode.Normalized) return NormalizedToY(normalized, p);
        return p.Top + index * StackedChannelHeight - _stackedScrollOffset + (1 - (normalized + 1) * .5) * StackedChannelHeight;
    }
    private double YToValue(CampathCurveChannel? c, double y, Rect p)
    {
        if (ViewMode == CurveEditorViewMode.Absolute || c == null) return _valueMin + (p.Bottom - y) / p.Height * (_valueMax - _valueMin);
        var visible = Channels?.Where(x => x.IsVisible).ToList() ?? []; var index = Math.Max(0, visible.IndexOf(c));
        var normalized = ViewMode == CurveEditorViewMode.Normalized
            ? YToNormalized(y, p)
            : (1 - (y - p.Top - index * StackedChannelHeight + _stackedScrollOffset) / StackedChannelHeight) * 2.0 - 1.0;
        var min = c.Keys.Min(k => k.Value); var max = c.Keys.Max(k => k.Value); if (Math.Abs(max - min) < 1e-9) { min -= 1; max += 1; } return min + (normalized + 1.0) * .5 * (max - min);
    }
    private double ValueToScreenY(CampathCurveChannel channel, double value, Rect plot)
    {
        var visible = Channels?.Where(item => item.IsVisible).ToList() ?? [];
        return ValueToY(channel, value, plot, Math.Max(0, visible.IndexOf(channel)), Math.Max(1, visible.Count));
    }
    private double NormalizedToY(double value, Rect plot)
    {
        var min = _normalizedCenter - _normalizedSpan * .5;
        return plot.Bottom - (value - min) / _normalizedSpan * plot.Height;
    }
    private double YToNormalized(double y, Rect plot) => _normalizedCenter - _normalizedSpan * .5 + (plot.Bottom - y) / plot.Height * _normalizedSpan;
    private void ClampStackedScroll(Rect plot, int count)
    {
        var maximum = Math.Max(0, count * StackedChannelHeight - plot.Height);
        _stackedScrollOffset = Math.Clamp(_stackedScrollOffset, 0, maximum);
    }
    private static void ZoomRange(ref double min, ref double max, double anchor, double factor) { min = anchor + (min - anchor) * factor; max = anchor + (max - anchor) * factor; if (max - min < 1e-6) max = min + 1e-6; }
    private static double NiceStep(double raw) { var p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9)))); var f = raw / p; return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p; }
    private static void DrawText(DrawingContext c, string s, Point p, IBrush b, double size) => c.DrawText(new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, b), p);

    private void OnChannelsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is IEnumerable<CampathCurveChannel> old) Hook(old, false);
        if (e.NewValue is IEnumerable<CampathCurveChannel> current) Hook(current, true);
        FitAll();
    }
    private void Hook(IEnumerable<CampathCurveChannel> channels, bool add)
    {
        foreach (var channel in channels)
        {
            if (add) { channel.PropertyChanged += OnItemChanged; channel.Keys.CollectionChanged += OnKeysChanged; foreach (var k in channel.Keys) k.PropertyChanged += OnItemChanged; }
            else { channel.PropertyChanged -= OnItemChanged; channel.Keys.CollectionChanged -= OnKeysChanged; foreach (var k in channel.Keys) k.PropertyChanged -= OnItemChanged; }
        }
    }
    private void OnKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (CampathCurveKey k in e.OldItems) k.PropertyChanged -= OnItemChanged;
        if (e.NewItems != null) foreach (CampathCurveKey k in e.NewItems) k.PropertyChanged += OnItemChanged;
        InvalidateVisual();
    }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();
}
