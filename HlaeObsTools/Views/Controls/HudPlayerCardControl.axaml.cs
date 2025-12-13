using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using HlaeObsTools.ViewModels.Hud;

namespace HlaeObsTools.Views.Controls;

public partial class HudPlayerCardControl : UserControl
{
    private const double RadialOuterRadius = 96;
    private const double RadialInnerRadius = 32;
    private const double RadialMargin = 12;
    private const double LabelWidth = 90;
    private const double LabelHeight = 24;
    private readonly double _radialSize = RadialOuterRadius * 2 + RadialMargin * 2;
    private Point _radialCenterControl;
    private Point _radialCenterRoot;
    private bool _pointerCaptured;
    private HudPlayerCardViewModel? _currentViewModel;
    private Popup? RadialPopupControl => this.FindControl<Popup>("RadialPopup");

    public HudPlayerCardControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        SetupRadialPresenter();
        PointerPressed += OnCardPointerPressed;
        PointerMoved += OnCardPointerMoved;
        PointerReleased += OnCardPointerReleased;
        PointerCaptureLost += OnCardPointerCaptureLost;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentViewModel != null)
        {
            _currentViewModel.RadialActions.CollectionChanged -= OnRadialActionsChanged;
        }

        _currentViewModel = DataContext as HudPlayerCardViewModel;
        if (_currentViewModel != null)
        {
            _currentViewModel.RadialActions.CollectionChanged += OnRadialActionsChanged;
            BuildRadialLayout(_currentViewModel);
        }
    }

    private void OnRadialActionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_currentViewModel != null)
        {
            BuildRadialLayout(_currentViewModel);
        }
    }

    private void SetupRadialPresenter()
    {
        if (RadialItems != null)
        {
            RadialItems.Width = _radialSize;
            RadialItems.Height = _radialSize;
        }

        if (RadialMenuCanvas != null)
        {
            RadialMenuCanvas.Width = _radialSize;
            RadialMenuCanvas.Height = _radialSize;
        }

        if (RadialCenter != null)
        {
            var centerOffset = _radialSize / 2 - RadialCenter.Width / 2;
            Canvas.SetLeft(RadialCenter, centerOffset);
            Canvas.SetTop(RadialCenter, centerOffset);
        }

        if (RadialCenterLabel != null)
        {
            const double labelWidth = 60;
            const double labelHeight = 18;
            Canvas.SetLeft(RadialCenterLabel, _radialSize / 2 - labelWidth / 2);
            Canvas.SetTop(RadialCenterLabel, _radialSize / 2 - labelHeight / 2);
        }
    }

    private void BuildRadialLayout(HudPlayerCardViewModel viewModel)
    {
        if (!viewModel.RadialActions.Any())
            return;

        var sliceCount = viewModel.RadialActions.Count;
        var sweep = 360.0 / sliceCount;
        var center = _radialSize / 2;
        var labelRadius = RadialInnerRadius + (RadialOuterRadius - RadialInnerRadius) * 0.65;

        for (int i = 0; i < viewModel.RadialActions.Count; i++)
        {
            var option = viewModel.RadialActions[i];
            var startAngle = -90 + i * sweep;
            var geometry = CreateSliceGeometry(center, center, RadialInnerRadius, RadialOuterRadius, startAngle, sweep);
            var labelCenter = PointOnCircle(center, center, labelRadius, startAngle + sweep / 2);
            option.SetLayout(
                geometry,
                new Point(labelCenter.X - LabelWidth / 2, labelCenter.Y - LabelHeight / 2));
        }
    }

    private static Geometry CreateSliceGeometry(double centerX, double centerY, double innerRadius, double outerRadius, double startAngle, double sweepAngle)
    {
        var outerStart = PointOnCircle(centerX, centerY, outerRadius, startAngle);
        var outerEnd = PointOnCircle(centerX, centerY, outerRadius, startAngle + sweepAngle);
        var innerStart = PointOnCircle(centerX, centerY, innerRadius, startAngle);
        var innerEnd = PointOnCircle(centerX, centerY, innerRadius, startAngle + sweepAngle);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(outerStart, true);
            ctx.ArcTo(outerEnd, new Size(outerRadius, outerRadius), sweepAngle, sweepAngle > 180, SweepDirection.Clockwise);
            ctx.LineTo(innerEnd);
            ctx.ArcTo(innerStart, new Size(innerRadius, innerRadius), sweepAngle, sweepAngle > 180, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }

        return geometry;
    }

    private static Point PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * Math.PI / 180;
        var x = centerX + radius * Math.Cos(angleRadians);
        var y = centerY + radius * Math.Sin(angleRadians);
        return new Point(x, y);
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentViewModel == null)
            return;

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        PositionRadialMenu(e);
        _currentViewModel.OpenRadialMenu();
        _currentViewModel.HighlightRadialAction(null, true);
        _pointerCaptured = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pointerCaptured || _currentViewModel == null || !_currentViewModel.IsRadialMenuOpen)
            return;

        UpdateHoveredAction(e);
    }

    private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_currentViewModel == null || !_pointerCaptured)
            return;

        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            CloseRadialMenu();
            return;
        }

        UpdateHoveredAction(e);
        _currentViewModel.RequestPlayerAction(_currentViewModel.HoveredRadialAction);
        e.Pointer.Capture(null);
        CloseRadialMenu();
        e.Handled = true;
    }

    private void OnCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        CloseRadialMenu();
    }

    private void CloseRadialMenu()
    {
        _pointerCaptured = false;
        if (_currentViewModel != null)
        {
            _currentViewModel.CloseRadialMenu();
        }

        var popup = RadialPopupControl;
        if (popup != null)
        {
            popup.IsOpen = false;
        }
    }

    private void PositionRadialMenu(PointerPressedEventArgs e)
    {
        var popup = RadialPopupControl;
        var topLevel = TopLevel.GetTopLevel(this);
        if (popup == null || topLevel == null)
            return;

        _radialCenterControl = e.GetPosition(this);
        _radialCenterRoot = this.TranslatePoint(_radialCenterControl, topLevel) ?? _radialCenterControl;

        popup.Placement = PlacementMode.Pointer;
        popup.PlacementTarget = this;
        popup.HorizontalOffset = -_radialSize / 2;
        popup.VerticalOffset = -_radialSize / 2;
        popup.IsOpen = true;
    }

    private void UpdateHoveredAction(PointerEventArgs e)
    {
        if (_currentViewModel == null)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        var positionRoot = e.GetPosition(topLevel);
        var dx = positionRoot.X - _radialCenterRoot.X;
        var dy = positionRoot.Y - _radialCenterRoot.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var highlightCenter = distance <= RadialInnerRadius;

        HudPlayerActionOption? hovered = null;
        if (!highlightCenter && distance <= RadialOuterRadius + RadialMargin && _currentViewModel.RadialActions.Any())
        {
            var angle = Math.Atan2(dy, dx) * 180 / Math.PI + 90;
            if (angle < 0)
            {
                angle += 360;
            }

            var sliceSize = 360.0 / _currentViewModel.RadialActions.Count;
            var index = Math.Clamp((int)Math.Floor(angle / sliceSize), 0, _currentViewModel.RadialActions.Count - 1);
            hovered = _currentViewModel.RadialActions.ElementAtOrDefault(index);
        }

        _currentViewModel.HighlightRadialAction(hovered, highlightCenter);
    }
}
