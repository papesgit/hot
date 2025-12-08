using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HlaeObsTools.ViewModels.Docks;
using System;
using System.ComponentModel;
using Avalonia.Threading;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using System.Linq;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using System.Threading;
using System.Threading.Tasks;

namespace HlaeObsTools.Views.Docks;

public partial class VideoDisplayDockView : UserControl
{
    private Point? _lockedCursorCenter;
    private bool _isRightButtonDown;
    private Point? _lastMousePosition;
    private INotifyPropertyChanged? _currentVmNotifier;
    private bool _cursorHidden;
    private bool _isShiftPressed;
    private double _currentArrowY;
    private Polygon? _speedArrow;
    private TextBlock? _speedLabel;
    private bool _isFirstSpeedUpdate = true;
    private CancellationTokenSource? _animationCts;
    private double _lastFreecamSpeed;
    private double _lastCanvasHeight;
    private bool _lastShiftPressed;

    public VideoDisplayDockView()
    {
        InitializeComponent();

        if (VideoContainer != null)
        {
            VideoContainer.SizeChanged += (_, _) =>
            {
                UpdateSharedTextureAspectSize();
                UpdateSpeedScaleRegionSize();
            };
        }
        this.AttachedToVisualTree += (_, _) =>
        {
            UpdateSharedTextureAspectSize();
            UpdateSpeedScaleRegionSize();
        };
        if (SpeedScaleCanvas != null)
        {
            SpeedScaleCanvas.SizeChanged += (_, _) => UpdateSpeedScale();
        }

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        DataContextChanged += OnDataContextChanged;
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            if (vm.UseD3DHost)
            {
                SharedTextureControl?.StartRenderer();
            }
            else
            {
                vm.StartStream();
            }
        }
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.StopStream();
            SharedTextureControl?.StopRenderer();
        }
    }

    private void RefreshBindsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.RefreshSpectatorBindings();
        }
    }

    private void VideoContainer_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(this);

        // Right mouse button pressed - activate freecam
        if (pointer.Properties.IsRightButtonPressed && !_isRightButtonDown)
        {
            _isRightButtonDown = true;
            (sender as IInputElement)?.Focus();

            BeginFreecam();
            e.Handled = true;
        }
    }

    private void VideoContainer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(this);

        // Right mouse button released - deactivate freecam
        if (!pointer.Properties.IsRightButtonPressed && _isRightButtonDown)
        {
            _isRightButtonDown = false;

            EndFreecam();
            e.Handled = true;
        }
    }

    private void VideoContainer_PointerMoved(object? sender, PointerEventArgs e)
    {
        // When freecam is active, lock cursor to center and send mouse deltas
        if (_lockedCursorCenter.HasValue && VideoContainer != null && DataContext is VideoDisplayDockViewModel vm)
        {
            // Get current screen position (before re-centering)
            var currentPos = e.GetPosition(VideoContainer);
            var screenPosPixel = VideoContainer.PointToScreen(currentPos);
            var screenPos = new Point(screenPosPixel.X, screenPosPixel.Y);

            // Calculate delta from last position
            if (_lastMousePosition.HasValue)
            {
                int dx = (int)(screenPos.X - _lastMousePosition.Value.X);
                int dy = (int)(screenPos.Y - _lastMousePosition.Value.Y);
                // Raw mouse deltas are provided by RawInputHandler; this only locks the cursor.
            }

            // Update last position to center (where we're about to move cursor)
            _lastMousePosition = _lockedCursorCenter.Value;

            // Re-center cursor
            SetCursorPosition((int)_lockedCursorCenter.Value.X, (int)_lockedCursorCenter.Value.Y);
        }
    }

    // P/Invoke for setting cursor position
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private void SetCursorPosition(int x, int y)
    {
        SetCursorPos(x, y);
    }

    // Forwarders for the shared texture overlay to reuse the existing freecam handling.
    private void SharedTextureOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        VideoContainer_PointerPressed(VideoContainer, e);
    }

    private void SharedTextureOverlay_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        VideoContainer_PointerReleased(VideoContainer, e);
    }

    private void SharedTextureOverlay_PointerMoved(object? sender, PointerEventArgs e)
    {
        VideoContainer_PointerMoved(VideoContainer, e);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            if (!_isShiftPressed)
            {
                _isShiftPressed = true;
                UpdateSpeedScale();
            }
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            if (_isShiftPressed)
            {
                _isShiftPressed = false;
                UpdateSpeedScale();
            }
        }
    }

    private void UpdateSharedTextureAspectSize()
    {
        if (SharedTextureAspect == null || VideoContainer == null) return;

        var bounds = VideoContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        const double aspect = 16.0 / 9.0;
        double targetW = bounds.Width;
        double targetH = targetW / aspect;
        if (targetH > bounds.Height)
        {
            targetH = bounds.Height;
            targetW = targetH * aspect;
        }

        SharedTextureAspect.Width = targetW;
        SharedTextureAspect.Height = targetH;
    }

    private void UpdateSpeedScaleRegionSize()
    {
        if (SpeedScaleRegion == null || VideoContainer == null) return;

        var bounds = VideoContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Set region to 30% width and 40% height
        SpeedScaleRegion.Width = bounds.Width * 0.3;
        SpeedScaleRegion.Height = bounds.Height * 0.4;
    }

    private void BeginFreecam()
    {
        if (DataContext is not VideoDisplayDockViewModel vm || VideoContainer == null)
            return;

        vm.ActivateFreecam();

        var containerBounds = VideoContainer.Bounds;
        var centerPoint = new Point(containerBounds.Width / 2, containerBounds.Height / 2);
        var screenCenterPixel = VideoContainer.PointToScreen(centerPoint);
        var screenCenter = new Point(screenCenterPixel.X, screenCenterPixel.Y);

        _lockedCursorCenter = screenCenter;
        _lastMousePosition = screenCenter;

        if (_lockedCursorCenter.HasValue)
        {
            SetCursorPosition((int)_lockedCursorCenter.Value.X, (int)_lockedCursorCenter.Value.Y);
            LockCursorToPoint(_lockedCursorCenter.Value);
        }

        Cursor = new Cursor(StandardCursorType.None);
        this.Focus();
    }

    private void EndFreecam()
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.DeactivateFreecam();
        }
        Cursor = Cursor.Default;
        UnlockCursor();
        _lockedCursorCenter = null;
        _lastMousePosition = null;
    }

    private void LockCursorToPoint(Point screenPoint)
    {
        int cx = (int)screenPoint.X;
        int cy = (int)screenPoint.Y;
        var rect = new RECT { left = cx, top = cy, right = cx + 1, bottom = cy + 1 };
        ClipCursor(ref rect);
        if (!_cursorHidden)
        {
            ShowCursor(false);
            _cursorHidden = true;
        }
    }

    private void UnlockCursor()
    {
        ClipCursor(IntPtr.Zero);
        if (_cursorHidden)
        {
            ShowCursor(true);
            _cursorHidden = false;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_currentVmNotifier != null)
        {
            _currentVmNotifier.PropertyChanged -= OnViewModelPropertyChanged;
            _currentVmNotifier = null;
        }

        if (DataContext is INotifyPropertyChanged notifier)
        {
            _currentVmNotifier = notifier;
            notifier.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdateSpeedScale();

        if (DataContext is VideoDisplayDockViewModel vm)
        {
            if (vm.UseD3DHost)
            {
                SharedTextureControl?.StartRenderer();
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoDisplayDockViewModel.UseD3DHost))
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                if (vm.UseD3DHost)
                {
                    SharedTextureControl?.StartRenderer();
                    if (vm.IsStreaming) vm.StopStream();
                }
                else
                {
                    SharedTextureControl?.StopRenderer();
                }
            }
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.FreecamSpeed))
        {
            UpdateSpeedScale();
        }
    }

    private void UpdateSpeedScale()
    {
        if (SpeedScaleCanvas == null || DataContext is not VideoDisplayDockViewModel vm)
            return;

        var height = SpeedScaleCanvas.Bounds.Height;
        if (height <= 0 || vm.SpeedTicks == null || vm.SpeedTicks.Count == 0)
            return;

        const double marginTop = 12;
        const double marginBottom = 12;
        const double lineX = 12;
        const double tickLong = 14;
        const double tickShort = 10;
        const double canvasWidth = 90;
        double usableHeight = Math.Max(0, height - marginTop - marginBottom);

        // Set canvas width
        SpeedScaleCanvas.Width = canvasWidth;

        // Calculate target position
        double speedMultiplier = _isShiftPressed ? vm.SprintMultiplier : 1.0;
        var effectiveSpeed = vm.FreecamSpeed * speedMultiplier;
        var clampedSpeed = Math.Clamp(effectiveSpeed, vm.SpeedMin, vm.SpeedMax);
        double currentNorm = (clampedSpeed - vm.SpeedMin) / (vm.SpeedMax - vm.SpeedMin);
        double targetArrowY = marginTop + usableHeight * (1 - currentNorm);

        // Detect if speed changed vs. just a resize
        bool speedChanged = Math.Abs(vm.FreecamSpeed - _lastFreecamSpeed) > 0.001;
        bool shiftStateChanged = _isShiftPressed != _lastShiftPressed;
        bool heightChanged = Math.Abs(height - _lastCanvasHeight) > 0.5;

        // Initialize on first update
        if (_isFirstSpeedUpdate)
        {
            _currentArrowY = targetArrowY;
            _lastFreecamSpeed = vm.FreecamSpeed;
            _lastCanvasHeight = height;
            _lastShiftPressed = _isShiftPressed;
            _isFirstSpeedUpdate = false;
        }

        // Update tracked values
        _lastFreecamSpeed = vm.FreecamSpeed;
        _lastCanvasHeight = height;
        _lastShiftPressed = _isShiftPressed;

        // Clear and rebuild static elements
        SpeedScaleCanvas.Children.Clear();

        // Main ruler line
        var spine = new Line
        {
            StartPoint = new Point(lineX, marginTop),
            EndPoint = new Point(lineX, height - marginBottom),
            Stroke = Brushes.White,
            StrokeThickness = 3,
            StrokeLineCap = PenLineCap.Round
        };
        SpeedScaleCanvas.Children.Add(spine);

        // Tick marks
        var ticks = vm.SpeedTicks.ToList();
        for (int i = 0; i < ticks.Count; i++)
        {
            var value = ticks[i];
            double norm = (value - vm.SpeedMin) / (vm.SpeedMax - vm.SpeedMin);
            double y = marginTop + usableHeight * (1 - norm);
            double len = i % 2 == 0 ? tickLong : tickShort;

            var tick = new Line
            {
                StartPoint = new Point(lineX, y),
                EndPoint = new Point(lineX + len, y),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                StrokeLineCap = PenLineCap.Square
            };
            SpeedScaleCanvas.Children.Add(tick);
        }

        // Top/bottom labels
        var topLabel = CreateLabel(vm.SpeedMax.ToString("0"), lineX + tickLong + 6, marginTop - 2, 12, FontWeight.SemiBold);
        var bottomLabel = CreateLabel(vm.SpeedMin.ToString("0"), lineX + tickLong + 6, height - marginBottom - 14, 12, FontWeight.SemiBold);
        SpeedScaleCanvas.Children.Add(topLabel);
        SpeedScaleCanvas.Children.Add(bottomLabel);

        // Create or update arrow and label
        if (_speedArrow == null)
        {
            _speedArrow = new Polygon();
            _speedLabel = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold };
        }

        // Update arrow appearance
        _speedArrow.Fill = _isShiftPressed ? Brushes.Yellow : Brushes.White;
        _speedArrow.Points = new Points(new[]
        {
            new Point(lineX + tickLong + 8, 0),
            new Point(lineX + tickLong + 36, -7),
            new Point(lineX + tickLong + 36, 7)
        });

        // Update label text and appearance
        var speedText = _isShiftPressed ? $"{effectiveSpeed:F1}" : vm.FreecamSpeedText;
        _speedLabel!.Text = speedText;
        _speedLabel.Foreground = _isShiftPressed ? Brushes.Yellow : Brushes.White;

        SpeedScaleCanvas.Children.Add(_speedArrow);
        SpeedScaleCanvas.Children.Add(_speedLabel);

        // Decide whether to animate or snap
        if (heightChanged && !speedChanged && !shiftStateChanged)
        {
            // Window resize: recalculate position proportionally without animation
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (shiftStateChanged && _isShiftPressed)
        {
            // Shift pressed: snap immediately to doubled position
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (shiftStateChanged && !_isShiftPressed)
        {
            // Shift released: snap back to original position
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (Math.Abs(targetArrowY - _currentArrowY) < 0.5)
        {
            // Very small changes: instant update
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (speedChanged)
        {
            // Speed changed: animate to new position
            AnimateArrowPosition(targetArrowY, lineX, tickLong);
        }
        else
        {
            // No change: just update position
            UpdateArrowPosition(lineX, tickLong);
        }
    }

    private void UpdateArrowPosition(double lineX, double tickLong)
    {
        if (_speedArrow == null || _speedLabel == null) return;

        _speedArrow.RenderTransform = new TranslateTransform(0, _currentArrowY);

        _speedLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var labelSize = _speedLabel.DesiredSize;
        Canvas.SetLeft(_speedLabel, lineX + tickLong + 8 - labelSize.Width - 8);
        Canvas.SetTop(_speedLabel, _currentArrowY - labelSize.Height / 2);
    }

    private async void AnimateArrowPosition(double targetY, double lineX, double tickLong)
    {
        if (_speedArrow == null || _speedLabel == null) return;

        // Cancel any ongoing animation
        _animationCts?.Cancel();
        _animationCts = new CancellationTokenSource();
        var token = _animationCts.Token;

        var startY = _currentArrowY;
        var duration = TimeSpan.FromMilliseconds(300);
        var easing = new CubicEaseOut();
        var startTime = DateTime.Now;

        try
        {
            while (true)
            {
                if (token.IsCancellationRequested)
                    return;

                var elapsed = DateTime.Now - startTime;
                if (elapsed >= duration)
                {
                    _currentArrowY = targetY;
                    UpdateArrowPosition(lineX, tickLong);
                    break;
                }

                var progress = elapsed.TotalMilliseconds / duration.TotalMilliseconds;
                var easedProgress = easing.Ease(progress);
                _currentArrowY = startY + (targetY - startY) * easedProgress;
                UpdateArrowPosition(lineX, tickLong);

                await Task.Delay(16, token); // ~60fps
            }
        }
        catch (TaskCanceledException)
        {
            // Animation was cancelled, which is expected
        }
    }

    private TextBlock CreateLabel(string text, double left, double top, double fontSize, FontWeight weight)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = fontSize,
            FontWeight = weight
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, top);
        return label;
    }
}
