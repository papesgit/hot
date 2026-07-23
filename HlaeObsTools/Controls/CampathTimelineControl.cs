using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Controls;

public sealed class CampathTimelineControl : Control
{
    public static readonly StyledProperty<IReadOnlyList<CampathKeyframeViewModel>?> ItemsProperty =
        AvaloniaProperty.Register<CampathTimelineControl, IReadOnlyList<CampathKeyframeViewModel>?>(nameof(Items));

    public static readonly StyledProperty<CampathKeyframeViewModel?> SelectedItemProperty =
        AvaloniaProperty.Register<CampathTimelineControl, CampathKeyframeViewModel?>(nameof(SelectedItem));

    public static readonly StyledProperty<double> PlayheadTimeProperty =
        AvaloniaProperty.Register<CampathTimelineControl, double>(nameof(PlayheadTime), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> DurationProperty =
        AvaloniaProperty.Register<CampathTimelineControl, double>(nameof(Duration), 5.0);

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<CampathTimelineControl, bool>(nameof(IsPlaying));

    public static readonly StyledProperty<CampathCurveDocument?> CurveDocumentProperty =
        AvaloniaProperty.Register<CampathTimelineControl, CampathCurveDocument?>(nameof(CurveDocument));

    public static readonly StyledProperty<bool> CurvesUnlockedProperty =
        AvaloniaProperty.Register<CampathTimelineControl, bool>(nameof(CurvesUnlocked));

    private readonly List<(Rect rect, CampathKeyframeViewModel keyframe)> _keyframeRects = new();
    private readonly List<(Rect rect, CurveBundle bundle)> _bundleRects = new();
    private readonly List<(Rect rect, CampathCurveChannel channel, CampathCurveKey key)> _curveKeyRects = new();
    private bool _draggingPlayhead;
    private CampathKeyframeViewModel? _draggingKeyframe;
    private double _curveDragAnchorTime;
    private Dictionary<CampathCurveKey, double>? _curveOriginalTimes;
    private Dictionary<CampathKeyframeViewModel, double>? _legacyOriginalTimes;
    private List<(CampathCurveChannel channel, CampathCurveKey key)>? _draggingCurveKeys;
    private bool _boxSelecting;
    private bool _boxSelectionActive;
    private Point _boxStart;
    private Point _boxCurrent;
    private HashSet<CampathCurveKey>? _boxBaseSelection;
    private bool _keyframeDragActive;
    private bool _itemsHooked;
    private bool _freecamPreviewActive;
    private bool _campathPreviewActive;
    private bool _keyframesHooked;
    private Point _pressPoint;
    private const double DragThreshold = 3.0;

    public CampathTimelineControl()
    {
        Focusable = true;
    }

    static CampathTimelineControl()
    {
        AffectsRender<CampathTimelineControl>(ItemsProperty, SelectedItemProperty, PlayheadTimeProperty, DurationProperty, IsPlayingProperty, CurveDocumentProperty, CurvesUnlockedProperty);
        ItemsProperty.Changed.AddClassHandler<CampathTimelineControl>((ctrl, args) => ctrl.OnItemsChanged(args));
        CurveDocumentProperty.Changed.AddClassHandler<CampathTimelineControl>((ctrl, args) => ctrl.OnCurveDocumentChanged(args));
    }

    public IReadOnlyList<CampathKeyframeViewModel>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public CampathKeyframeViewModel? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public double PlayheadTime
    {
        get => GetValue(PlayheadTimeProperty);
        set => SetValue(PlayheadTimeProperty, value);
    }

    public double Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public bool IsPlaying
    {
        get => GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public CampathCurveDocument? CurveDocument
    {
        get => GetValue(CurveDocumentProperty);
        set => SetValue(CurveDocumentProperty, value);
    }

    public bool CurvesUnlocked
    {
        get => GetValue(CurvesUnlockedProperty);
        set => SetValue(CurvesUnlockedProperty, value);
    }

    public event Action<double>? FreecamPreviewRequested;
    public event Action? FreecamPreviewEnded;
    public event Action? CampathPreviewRequested;
    public event Action? CampathPreviewEnded;
    public event Action? KeyframeDragStarted;
    public event Action? KeyframeDragEnded;
    public event Action? CurveDocumentEdited;
    public event Action? PlayheadDragEnded;
    public event Action? HistoryEditStarted;
    public event Action? HistoryEditCompleted;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            Focus();
            var pt = e.GetPosition(this);

            if (HitTestPlayhead(pt))
            {
                _draggingPlayhead = true;
                _freecamPreviewActive = IsCtrlDown(e.KeyModifiers);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            var bundle = HitTestBundle(pt);
            if (bundle != null)
            {
                SelectCurveKeys(bundle.Members.Select(member => member.key), e.KeyModifiers);
                SelectedItem = bundle.IsComplete ? Items?.FirstOrDefault(key => Math.Abs(key.Time - bundle.Time) <= TimeEpsilon) : null;
                BeginCurveDrag(pt, bundle.Time, e.Pointer);
                e.Handled = true;
                return;
            }

            if (CurvesUnlocked && HitTestCurveKey(pt) is { } curveKey)
            {
                SelectCurveKeys([curveKey.key], e.KeyModifiers);
                SelectedItem = null;
                BeginCurveDrag(pt, curveKey.key.Time, e.Pointer);
                e.Handled = true;
                return;
            }

            var keyframe = HitTestKeyframe(pt);
            if (keyframe != null)
            {
                _pressPoint = pt;
                SelectedItem = keyframe;
                _draggingKeyframe = keyframe;
                _keyframeDragActive = false;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (CurveDocument != null)
            {
                _boxSelecting = true;
                _boxSelectionActive = false;
                _boxStart = _boxCurrent = pt;
                _boxBaseSelection = IsShiftDown(e.KeyModifiers)
                    ? GetAllCurveKeys().Where(item => item.key.Selected).Select(item => item.key).ToHashSet()
                    : new HashSet<CampathCurveKey>();
                if (!IsShiftDown(e.KeyModifiers)) ClearCurveSelection();
                SelectedItem = null;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (SelectedItem != null || GetAllCurveKeys().Any(item => item.key.Selected))
            {
                SelectedItem = null;
                ClearCurveSelection();
                e.Handled = true;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_draggingPlayhead)
        {
            var pt = e.GetPosition(this);
            var time = XToTime(pt.X);
            if (IsShiftDown(e.KeyModifiers))
            {
                var snap = FindNearestKeyframeTime(time);
                if (snap.HasValue)
                    time = snap.Value;
            }
            PlayheadTime = time;
            var ctrlDown = IsCtrlDown(e.KeyModifiers);
            if (ctrlDown && !_freecamPreviewActive)
            {
                _freecamPreviewActive = true;
            }
            else if (!ctrlDown && _freecamPreviewActive)
            {
                _freecamPreviewActive = false;
                FreecamPreviewEnded?.Invoke();
            }

            if (_freecamPreviewActive)
                FreecamPreviewRequested?.Invoke(time);

            var altDown = IsAltDown(e.KeyModifiers);
            if (altDown && !ctrlDown && !_campathPreviewActive)
            {
                _campathPreviewActive = true;
                CampathPreviewRequested?.Invoke();
            }
            else if ((!altDown || ctrlDown) && _campathPreviewActive)
            {
                _campathPreviewActive = false;
                CampathPreviewEnded?.Invoke();
            }
            e.Handled = true;
            return;
        }

        if (_boxSelecting)
        {
            _boxCurrent = e.GetPosition(this);
            var delta = _boxCurrent - _boxStart;
            if (!_boxSelectionActive && Math.Abs(delta.X) + Math.Abs(delta.Y) > DragThreshold)
                _boxSelectionActive = true;
            if (_boxSelectionActive) UpdateBoxSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (_draggingCurveKeys != null && _curveOriginalTimes != null)
        {
            var pt = e.GetPosition(this);
            var deltaPixels = pt - _pressPoint;
            if (!_keyframeDragActive && (Math.Abs(deltaPixels.X) + Math.Abs(deltaPixels.Y) > DragThreshold))
            {
                _keyframeDragActive = true;
                KeyframeDragStarted?.Invoke();
            }

            if (_keyframeDragActive)
            {
                var requestedDelta = XToTime(pt.X) - _curveDragAnchorTime;
                var delta = ClampCurveDelta(_draggingCurveKeys, requestedDelta);
                foreach (var member in _draggingCurveKeys)
                    member.key.Time = _curveOriginalTimes[member.key] + delta;
                if (_legacyOriginalTimes != null)
                    foreach (var legacy in _legacyOriginalTimes)
                        legacy.Key.Time = legacy.Value + delta;
            }
            e.Handled = true;
            return;
        }

        if (_draggingKeyframe != null)
        {
            var pt = e.GetPosition(this);
            var delta = pt - _pressPoint;
            if (!_keyframeDragActive && (Math.Abs(delta.X) + Math.Abs(delta.Y) > DragThreshold))
            {
                _keyframeDragActive = true;
                KeyframeDragStarted?.Invoke();
            }

            if (_keyframeDragActive)
                _draggingKeyframe.Time = XToTime(pt.X);
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_draggingPlayhead || _draggingKeyframe != null || _draggingCurveKeys != null || _boxSelecting)
        {
            var playheadReleased = _draggingPlayhead;
            var curveInteractionReleased = _draggingCurveKeys != null || _boxSelecting;
            _draggingPlayhead = false;
            _draggingKeyframe = null;
            _draggingCurveKeys = null;
            _curveOriginalTimes = null;
            _legacyOriginalTimes = null;
            _boxSelecting = false;
            _boxSelectionActive = false;
            _boxBaseSelection = null;
            InvalidateVisual();
            if (_keyframeDragActive)
            {
                _keyframeDragActive = false;
                KeyframeDragEnded?.Invoke();
            }
            if (curveInteractionReleased)
                CurveDocumentEdited?.Invoke();
            if (playheadReleased)
            {
                PlayheadDragEnded?.Invoke();
            }
            if (_freecamPreviewActive)
            {
                _freecamPreviewActive = false;
                FreecamPreviewEnded?.Invoke();
            }
            if (_campathPreviewActive)
            {
                _campathPreviewActive = false;
                CampathPreviewEnded?.Invoke();
            }
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var selectedCurveKeys = GetAllCurveKeys().Where(item => item.key.Selected).Select(item => item.key).ToHashSet();
        if (e.Key == Key.Delete && selectedCurveKeys.Count > 0 && CurveDocument != null)
        {
            HistoryEditStarted?.Invoke();
            var deletedBundleTimes = BuildCurveMarkers(CurveDocument).bundles
                .Where(bundle => bundle.IsComplete && bundle.Members.All(member => selectedCurveKeys.Contains(member.key)))
                .Select(bundle => bundle.Time).ToList();
            foreach (var channel in CurveDocument.Channels)
                for (var i = channel.Keys.Count - 1; i >= 0; i--)
                    if (selectedCurveKeys.Contains(channel.Keys[i])) channel.Keys.RemoveAt(i);
            if (Items is IList<CampathKeyframeViewModel> legacyItems)
                for (var i = legacyItems.Count - 1; i >= 0; i--)
                    if (deletedBundleTimes.Any(time => Math.Abs(legacyItems[i].Time - time) <= TimeEpsilon))
                        legacyItems.RemoveAt(i);
            SelectedItem = null;
            CurveDocumentEdited?.Invoke();
            HistoryEditCompleted?.Invoke();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && SelectedItem != null)
        {
            if (Items is IList<CampathKeyframeViewModel> list)
            {
                HistoryEditStarted?.Invoke();
                list.Remove(SelectedItem);
                SelectedItem = null;
                HistoryEditCompleted?.Invoke();
                e.Handled = true;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width <= 1 || bounds.Height <= 1)
            return;

        _keyframeRects.Clear();
        _bundleRects.Clear();
        _curveKeyRects.Clear();

        var background = new SolidColorBrush(Color.Parse("#0E0E0E"));
        context.FillRectangle(background, bounds);

        var paddingLeft = 6.0;
        var paddingRight = 6.0;
        var paddingTop = 6.0;
        var paddingBottom = 6.0;
        var rulerHeight = 18.0;
        var stripTop = paddingTop + rulerHeight;
        var stripBottom = bounds.Height - paddingBottom;
        var stripMid = (stripTop + stripBottom) * 0.5;
        var stripLeft = paddingLeft;
        var stripRight = bounds.Width - paddingRight;

        var linePen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
        context.DrawLine(linePen, new Point(stripLeft, stripMid), new Point(stripRight, stripMid));

        DrawTimeRuler(context, stripLeft, stripRight, paddingTop + 2.0);
        DrawKeyframes(context, stripLeft, stripRight, stripMid);
        if (_boxSelecting && _boxSelectionActive)
        {
            var selectionRect = RectFromPoints(_boxStart, _boxCurrent);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(35, 110, 165, 255)), selectionRect);
            context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#6EA5FF")), 1), selectionRect);
        }
        DrawPlayhead(context, stripLeft, stripRight, paddingTop + 2.0, stripBottom);
        DrawCurrentTimeText(context, stripLeft, stripRight, paddingTop);
    }

    private void DrawTimeRuler(DrawingContext context, double left, double right, double y)
    {
        var duration = Math.Max(0.01, Duration);
        var width = right - left;
        if (width <= 1)
            return;

        var secondsPerPixel = duration / width;
        var minLabelSpacing = 50.0;
        var minStep = secondsPerPixel * minLabelSpacing;
        var step = ChooseStep(minStep);

        var pen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
        for (var t = 0.0; t <= duration + 0.0001; t += step)
        {
            var x = TimeToX(t, left, right);
            context.DrawLine(pen, new Point(x, y + 2), new Point(x, y + 8));

            var text = new FormattedText(
                FormatTime(t),
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                10,
                Brushes.Gray);
            context.DrawText(text, new Point(x + 2, y - 2));
        }
    }

    private void DrawKeyframes(DrawingContext context, double left, double right, double midY)
    {
        if (CurveDocument is { Channels.Count: > 0 } document && document.Channels.Any(channel => channel.Keys.Count > 0))
        {
            DrawCurveKeyframes(context, document, left, right, midY);
            return;
        }

        if (Items == null || Items.Count == 0)
            return;

        var size = 16.0;
        foreach (var key in Items)
        {
            var x = TimeToX(key.Time, left, right);
            var rect = new Rect(x - size * 0.5, midY - size * 0.5, size, size);
            _keyframeRects.Add((rect.Inflate(3), key));

            var isSelected = key == SelectedItem;
            var color = isSelected ? Color.Parse("#E6E6E6") : Color.Parse("#AAAAAA");
            var brush = new SolidColorBrush(color);

            var diamond = new StreamGeometry();
            using (var gc = diamond.Open())
            {
                gc.BeginFigure(new Point(x, rect.Top), true);
                gc.LineTo(new Point(rect.Right, midY));
                gc.LineTo(new Point(x, rect.Bottom));
                gc.LineTo(new Point(rect.Left, midY));
                gc.EndFigure(true);
            }
            context.DrawGeometry(brush, null, diamond);
        }
    }

    private void DrawCurveKeyframes(DrawingContext context, CampathCurveDocument document, double left, double right, double midY)
    {
        var (bundles, splitKeys) = BuildCurveMarkers(document);
        foreach (var bundle in bundles)
        {
            var bundleSize = bundle.IsComplete ? 16.0 : 13.0;
            var x = TimeToX(bundle.Time, left, right);
            var rect = new Rect(x - bundleSize * .5, midY - bundleSize * .5, bundleSize, bundleSize);
            _bundleRects.Add((rect.Inflate(3), bundle));
            var selected = bundle.Members.All(member => member.key.Selected);
            DrawDiamond(context, x, midY, bundleSize,
                bundle.IsComplete ? Color.Parse("#AAAAAA") : Color.Parse("#777A82"), selected);
        }

        foreach (var cluster in ClusterSplitKeys(splitKeys))
        {
            var x = TimeToX(cluster.Average(item => item.key.Time), left, right);
            const double markerSize = 7.0;
            var spread = Math.Min(14.0, Math.Max(0.0, (cluster.Count - 1) * 1.5));
            for (var i = 0; i < cluster.Count; i++)
            {
                var offset = cluster.Count == 1 ? 0 : -spread * .5 + spread * i / (cluster.Count - 1);
                var color = TryParseColor(cluster[i].channel.Color, Color.Parse("#AAAAAA"));
                var rect = new Rect(x + offset - markerSize * .5, midY - markerSize * .5, markerSize, markerSize);
                _curveKeyRects.Add((rect.Inflate(2), cluster[i].channel, cluster[i].key));
                DrawDiamond(context, x + offset, midY, markerSize, color, cluster[i].key.Selected);
            }
        }
    }

    private static void DrawDiamond(DrawingContext context, double x, double y, double size, Color color, bool selected = false)
    {
        var half = size * .5;
        var diamond = new StreamGeometry();
        using (var gc = diamond.Open())
        {
            gc.BeginFigure(new Point(x, y - half), true);
            gc.LineTo(new Point(x + half, y));
            gc.LineTo(new Point(x, y + half));
            gc.LineTo(new Point(x - half, y));
            gc.EndFigure(true);
        }
        var outline = selected ? new Pen(Brushes.White, 1.5) : null;
        context.DrawGeometry(new SolidColorBrush(color), outline, diamond);
    }

    private static Color TryParseColor(string value, Color fallback)
    {
        try { return Color.Parse(value); }
        catch { return fallback; }
    }

    private void DrawPlayhead(DrawingContext context, double left, double right, double top, double bottom)
    {
        var x = TimeToX(PlayheadTime, left, right);
        var pen = new Pen(new SolidColorBrush(Color.Parse("#FFB84D")), 1.5);
        context.DrawLine(pen, new Point(x, top), new Point(x, bottom));

        var headSize = 14.0;
        var head = new StreamGeometry();
        using (var gc = head.Open())
        {
            gc.BeginFigure(new Point(x, top + headSize), true);
            gc.LineTo(new Point(x - headSize * 0.5, top));
            gc.LineTo(new Point(x + headSize * 0.5, top));
            gc.EndFigure(true);
        }
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#FFB84D")), null, head);
    }

    private void DrawCurrentTimeText(DrawingContext context, double left, double right, double y)
    {
        var text = new FormattedText(
            $"t {PlayheadTime:0.00}s",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            11,
            Brushes.LightGray);
        var textY = Bounds.Height - 16;
        context.DrawText(text, new Point(left, textY));
    }

    private CampathKeyframeViewModel? HitTestKeyframe(Point pt)
    {
        foreach (var (rect, key) in _keyframeRects)
        {
            if (rect.Contains(pt))
                return key;
        }
        return null;
    }

    private CurveBundle? HitTestBundle(Point pt)
    {
        for (var i = _bundleRects.Count - 1; i >= 0; i--)
            if (_bundleRects[i].rect.Contains(pt)) return _bundleRects[i].bundle;
        return null;
    }

    private (CampathCurveChannel channel, CampathCurveKey key)? HitTestCurveKey(Point pt)
    {
        for (var i = _curveKeyRects.Count - 1; i >= 0; i--)
            if (_curveKeyRects[i].rect.Contains(pt)) return (_curveKeyRects[i].channel, _curveKeyRects[i].key);
        return null;
    }

    private static (List<CurveBundle> bundles, List<(CampathCurveChannel channel, CampathCurveKey key)> splitKeys)
        BuildCurveMarkers(CampathCurveDocument document)
    {
        var channels = document.Channels.Where(channel => channel.Keys.Count > 0).ToList();
        var allKeys = channels.SelectMany(channel => channel.Keys.Select(key => (channel, key)))
            .OrderBy(item => item.key.Time).ToList();
        var bundled = new HashSet<CampathCurveKey>();
        var bundles = new List<CurveBundle>();
        var requiredChannels = Math.Max(2, (channels.Count + 1) / 2);
        foreach (var cluster in ClusterSplitKeys(allKeys))
        {
            var center = cluster.Average(item => item.key.Time);
            var members = cluster.GroupBy(item => item.channel)
                .Select(group => group.MinBy(item => Math.Abs(item.key.Time - center)))
                .Where(item => item != default).ToList();
            if (members.Count < requiredChannels) continue;
            foreach (var member in members) bundled.Add(member.key);
            bundles.Add(new CurveBundle(members.Average(member => member.key.Time), members, members.Count == channels.Count));
        }

        var split = allKeys.Where(item => !bundled.Contains(item.key)).ToList();
        return (bundles, split);
    }

    private static List<List<(CampathCurveChannel channel, CampathCurveKey key)>> ClusterSplitKeys(
        List<(CampathCurveChannel channel, CampathCurveKey key)> keys)
    {
        var result = new List<List<(CampathCurveChannel channel, CampathCurveKey key)>>();
        foreach (var item in keys)
        {
            if (result.Count == 0 || Math.Abs(item.key.Time - result[^1][0].key.Time) > TimeEpsilon)
                result.Add(new List<(CampathCurveChannel channel, CampathCurveKey key)>());
            result[^1].Add(item);
        }
        return result;
    }

    private void BeginCurveDrag(Point point, double anchorTime, IPointer pointer)
    {
        _pressPoint = point;
        _curveDragAnchorTime = anchorTime;
        _draggingCurveKeys = GetAllCurveKeys().Where(item => item.key.Selected).ToList();
        _curveOriginalTimes = _draggingCurveKeys.ToDictionary(member => member.key, member => member.key.Time);
        _legacyOriginalTimes = new Dictionary<CampathKeyframeViewModel, double>();
        if (CurveDocument != null && Items != null)
        {
            var selected = _draggingCurveKeys.Select(member => member.key).ToHashSet();
            foreach (var bundle in BuildCurveMarkers(CurveDocument).bundles.Where(bundle => bundle.IsComplete))
            {
                if (!bundle.Members.All(member => selected.Contains(member.key))) continue;
                var legacy = Items.FirstOrDefault(key => Math.Abs(key.Time - bundle.Time) <= TimeEpsilon);
                if (legacy != null) _legacyOriginalTimes[legacy] = legacy.Time;
            }
        }
        _keyframeDragActive = false;
        pointer.Capture(this);
        InvalidateVisual();
    }

    private void SelectCurveKeys(IEnumerable<CampathCurveKey> keys, KeyModifiers modifiers)
    {
        var targets = keys.Distinct().ToList();
        if (IsShiftDown(modifiers))
        {
            foreach (var key in targets) key.Selected = true;
        }
        else if (IsCtrlDown(modifiers))
        {
            foreach (var key in targets) key.Selected = !key.Selected;
        }
        else if (!targets.All(key => key.Selected))
        {
            ClearCurveSelection();
            foreach (var key in targets) key.Selected = true;
        }
        InvalidateVisual();
    }

    private void ClearCurveSelection()
    {
        foreach (var item in GetAllCurveKeys()) item.key.Selected = false;
    }

    private List<(CampathCurveChannel channel, CampathCurveKey key)> GetAllCurveKeys() =>
        CurveDocument?.Channels.SelectMany(channel => channel.Keys.Select(key => (channel, key))).ToList()
        ?? new List<(CampathCurveChannel channel, CampathCurveKey key)>();

    private void UpdateBoxSelection()
    {
        var selected = _boxBaseSelection != null
            ? new HashSet<CampathCurveKey>(_boxBaseSelection)
            : new HashSet<CampathCurveKey>();
        var box = RectFromPoints(_boxStart, _boxCurrent);
        foreach (var marker in _bundleRects)
            if (marker.rect.Intersects(box))
                foreach (var member in marker.bundle.Members) selected.Add(member.key);
        if (CurvesUnlocked)
            foreach (var marker in _curveKeyRects)
                if (marker.rect.Intersects(box)) selected.Add(marker.key);
        foreach (var item in GetAllCurveKeys()) item.key.Selected = selected.Contains(item.key);
    }

    private static Rect RectFromPoints(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(1, Math.Abs(a.X - b.X)), Math.Max(1, Math.Abs(a.Y - b.Y)));

    private double ClampCurveDelta(List<(CampathCurveChannel channel, CampathCurveKey key)> keys, double requestedDelta)
    {
        if (_curveOriginalTimes == null || CurveDocument == null) return 0;
        var lower = double.NegativeInfinity;
        var upper = double.PositiveInfinity;
        var moving = keys.Select(member => member.key).ToHashSet();
        foreach (var member in keys)
        {
            var original = _curveOriginalTimes[member.key];
            lower = Math.Max(lower, -original);
            upper = Math.Min(upper, Math.Max(0.01, Duration) - original);
            foreach (var other in member.channel.Keys)
            {
                if (moving.Contains(other)) continue;
                if (other.Time < original) lower = Math.Max(lower, other.Time + TimeEpsilon - original);
                if (other.Time > original) upper = Math.Min(upper, other.Time - TimeEpsilon - original);
            }
        }
        return lower <= upper ? Math.Clamp(requestedDelta, lower, upper) : 0;
    }

    private bool HitTestPlayhead(Point pt)
    {
        var left = 6.0;
        var right = Bounds.Width - 6.0;
        var x = TimeToX(PlayheadTime, left, right);
        var headSize = 14.0;
        var top = 6.0 + 2.0;
        var hitRect = new Rect(x - headSize * 0.6, top, headSize * 1.2, headSize + 4.0);
        return hitRect.Contains(pt);
    }

    private double TimeToX(double time, double left, double right)
    {
        var duration = Math.Max(0.01, Duration);
        var t = Math.Clamp(time, 0.0, duration);
        return left + (t / duration) * (right - left);
    }

    private double XToTime(double x)
    {
        var left = 6.0;
        var right = Bounds.Width - 6.0;
        if (right <= left)
            return 0.0;
        var t = (x - left) / (right - left);
        t = Math.Clamp(t, 0.0, 1.0);
        return t * Math.Max(0.01, Duration);
    }

    private static bool IsShiftDown(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift);

    private static bool IsCtrlDown(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Control);

    private static bool IsAltDown(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Alt);

    private double? FindNearestKeyframeTime(double time)
    {
        if (CurveDocument is { } document)
        {
            var curveKey = document.Channels.SelectMany(channel => channel.Keys)
                .MinBy(key => Math.Abs(key.Time - time));
            if (curveKey != null) return curveKey.Time;
        }
        if (Items == null || Items.Count == 0)
            return null;
        var nearest = Items.OrderBy(k => Math.Abs(k.Time - time)).FirstOrDefault();
        return nearest?.Time;
    }

    private void OnItemsChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_itemsHooked && e.OldValue is INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= OnItemsCollectionChanged;

        UnhookKeyframeItems();

        _itemsHooked = false;
        if (e.NewValue is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += OnItemsCollectionChanged;
            _itemsHooked = true;
        }

        HookKeyframeItems();
    }

    private void OnCurveDocumentChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (e.OldValue is CampathCurveDocument oldDocument) HookCurveDocument(oldDocument, false);
        if (e.NewValue is CampathCurveDocument newDocument) HookCurveDocument(newDocument, true);
        InvalidateVisual();
    }

    private void HookCurveDocument(CampathCurveDocument document, bool hook)
    {
        if (hook) document.Channels.CollectionChanged += OnCurveChannelsChanged;
        else document.Channels.CollectionChanged -= OnCurveChannelsChanged;
        foreach (var channel in document.Channels) HookCurveChannel(channel, hook);
    }

    private void HookCurveChannel(CampathCurveChannel channel, bool hook)
    {
        if (hook) channel.Keys.CollectionChanged += OnCurveKeysChanged;
        else channel.Keys.CollectionChanged -= OnCurveKeysChanged;
        foreach (var key in channel.Keys)
            if (hook) key.PropertyChanged += OnCurveKeyPropertyChanged;
            else key.PropertyChanged -= OnCurveKeyPropertyChanged;
    }

    private void OnCurveChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (CampathCurveChannel channel in e.OldItems) HookCurveChannel(channel, false);
        if (e.NewItems != null) foreach (CampathCurveChannel channel in e.NewItems) HookCurveChannel(channel, true);
        InvalidateVisual();
    }

    private void OnCurveKeysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (CampathCurveKey key in e.OldItems) key.PropertyChanged -= OnCurveKeyPropertyChanged;
        if (e.NewItems != null) foreach (CampathCurveKey key in e.NewItems) key.PropertyChanged += OnCurveKeyPropertyChanged;
        InvalidateVisual();
    }

    private void OnCurveKeyPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var item in e.OldItems.OfType<CampathKeyframeViewModel>())
                item.PropertyChanged -= OnKeyframePropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (var item in e.NewItems.OfType<CampathKeyframeViewModel>())
                item.PropertyChanged += OnKeyframePropertyChanged;
        }

        InvalidateVisual();
    }

    private void HookKeyframeItems()
    {
        if (_keyframesHooked || Items == null)
            return;

        foreach (var item in Items.OfType<CampathKeyframeViewModel>())
            item.PropertyChanged += OnKeyframePropertyChanged;
        _keyframesHooked = true;
    }

    private void UnhookKeyframeItems()
    {
        if (!_keyframesHooked || Items == null)
            return;

        foreach (var item in Items.OfType<CampathKeyframeViewModel>())
            item.PropertyChanged -= OnKeyframePropertyChanged;
        _keyframesHooked = false;
    }

    private void OnKeyframePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private static double ChooseStep(double minStep)
    {
        var steps = new[] { 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 15.0, 30.0, 60.0 };
        foreach (var step in steps)
        {
            if (step >= minStep)
                return step;
        }
        return 120.0;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 60.0)
            return $"{seconds:0.##}s";
        var mins = Math.Floor(seconds / 60.0);
        var sec = seconds - mins * 60.0;
        return $"{mins:0}m{sec:00}s";
    }

    private const double TimeEpsilon = 0.001;
    private sealed record CurveBundle(double Time, List<(CampathCurveChannel channel, CampathCurveKey key)> Members, bool IsComplete);
}
