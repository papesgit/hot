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
using HlaeObsTools.Services.Video.RTP;
using HlaeObsTools.ViewModels.Hud;

namespace HlaeObsTools.Views.Docks;

public partial class VideoDisplayDockView : UserControl
{
    private static TaskCompletionSource s_startupReadyTcs = CreateStartupReadyTaskCompletionSource();
    private Point? _lockedCursorCenter;
    private bool _isRightButtonDown;
    private INotifyPropertyChanged? _currentVmNotifier;
    private VideoDisplayDockViewModel? _currentViewModel;
    private INotifyPropertyChanged? _currentHudOverlayNotifier;
    private HudOverlayViewModel? _currentHudOverlay;
    private bool _cursorHidden;
    private bool _isShiftPressed;
    private bool _isDemoPaused;
    private double _currentArrowY;
    private Polygon? _speedArrow;
    private TextBlock? _speedLabel;
    private bool _isFirstSpeedUpdate = true;
    private CancellationTokenSource? _animationCts;
    private double _lastEffectiveSpeed;
    private double _lastCanvasHeight;
    private bool _lastSprintActive;
    private Window? _parentWindow;
    private DispatcherTimer? _analogSprintTimer;

    public VideoDisplayDockView()
    {
        InitializeComponent();

        if (PlayPauseButton != null)
        {
            // Dock content ready signal
            PlayPauseButton.AttachedToVisualTree += OnPlayPauseButtonAttachedToVisualTree;
        }

        if (VideoContainer != null)
        {
            VideoContainer.SizeChanged += (_, _) =>
            {
                UpdateSharedTextureAspectSize();
                UpdateRtpSwapchainAspectSize();
                UpdateSpeedScaleRegionSize();
                UpdateHudOverlayPosition();
                UpdateRtpSwapchainBounds();
            };
        }
        this.AttachedToVisualTree += (_, _) =>
        {
            UpdateSharedTextureAspectSize();
            UpdateRtpSwapchainAspectSize();
            UpdateSpeedScaleRegionSize();
            UpdateHudOverlayPosition();
            UpdateRtpSwapchainBounds();
            SubscribeToWindowEvents();
        };
        this.DetachedFromVisualTree += (_, _) =>
        {
            UnsubscribeFromWindowEvents();
        };
        var canvas = HudContent?.GetSpeedScaleCanvas();
        if (canvas != null)
        {
            canvas.SizeChanged += (_, _) => UpdateSpeedScale();
        }
        if (SharedTextureAspect != null)
        {
            SharedTextureAspect.SizeChanged += (_, _) => UpdateHudOverlayPosition();
        }
        if (SharedTextureHost != null)
        {
            SharedTextureHost.SharedHandleInvalidated += OnSharedHandleInvalidated;
            SharedTextureHost.RightButtonDown += OnSharedTextureRightButtonDown;
            SharedTextureHost.RightButtonUp += OnSharedTextureRightButtonUp;
            SharedTextureHost.FramePresented += OnSharedTextureFramePresented;
        }
        if (RtpSwapchainAspect != null)
        {
            RtpSwapchainAspect.SizeChanged += (_, _) => UpdateRtpSwapchainBounds();
        }
        if (RtpSwapchainHost != null)
        {
            RtpSwapchainHost.RightButtonDown += OnRtpRightButtonDown;
            RtpSwapchainHost.RightButtonUp += OnRtpRightButtonUp;
            RtpSwapchainHost.FramePresented += OnRtpFramePresented;
            RtpSwapchainHost.AttachedToVisualTree += (_, _) => UpdateRtpSwapchainBounds();
            RtpSwapchainHost.DetachedFromVisualTree += (_, _) => UpdateRtpSwapchainBounds();
        }

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        DataContextChanged += OnDataContextChanged;

        PointerPressed += OnDockPointerPressed;
        PointerReleased += OnDockPointerReleased;

        _analogSprintTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _analogSprintTimer.Tick += (_, _) =>
        {
            if (DataContext is VideoDisplayDockViewModel vm && vm.AnalogKeyboardEnabled)
            {
                UpdateSpeedScale();
            }
        };
        _analogSprintTimer.Start();
    }

    public static void ResetStartupReadySignal()
    {
        s_startupReadyTcs = CreateStartupReadyTaskCompletionSource();
    }

    public static Task WaitForStartupReadyAsync()
    {
        return s_startupReadyTcs.Task;
    }

    private static TaskCompletionSource CreateStartupReadyTaskCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void OnPlayPauseButtonAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
        {
            s_startupReadyTcs.TrySetResult();
            return;
        }

        topLevel.RequestAnimationFrame(_ =>
        {
            s_startupReadyTcs.TrySetResult();
        });
    }

    private void UpdateHudOverlayPosition()
    {
        if (DataContext is not VideoDisplayDockViewModel vm || SharedTextureAspect == null)
            return;

        // Only update if using D3DHost mode
        if (!vm.UseD3DHost)
            return;

        if (!this.IsAttachedToVisualTree() || !SharedTextureAspect.IsAttachedToVisualTree())
            return;

        try
        {
            var bounds = SharedTextureAspect.Bounds;
            PixelPoint topLeft;
            PixelSize size;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                if (VideoContainer == null || !VideoContainer.IsAttachedToVisualTree() || !TryGetLetterboxedRect(VideoContainer, 16.0 / 9.0, out var rect))
                    return;

                var fallbackTopLeft = VideoContainer.PointToScreen(new Point(rect.X, rect.Y));
                topLeft = new PixelPoint((int)fallbackTopLeft.X, (int)fallbackTopLeft.Y);
                size = new PixelSize((int)rect.Width, (int)rect.Height);
            }
            else
            {
                // Get screen position of the SharedTextureAspect control
                var controlTopLeft = SharedTextureAspect.PointToScreen(new Point(0, 0));
                topLeft = new PixelPoint((int)controlTopLeft.X, (int)controlTopLeft.Y);
                size = new PixelSize((int)bounds.Width, (int)bounds.Height);
            }

            vm.UpdateHudOverlayBounds(topLeft, size);
        }
        catch
        {
            // Ignore positioning errors during initialization
        }
    }

    private void SubscribeToWindowEvents()
    {
        if (_parentWindow != null) return;

        _parentWindow = TopLevel.GetTopLevel(this) as Window;
        if (_parentWindow != null)
        {
            _parentWindow.PositionChanged += OnParentWindowPositionChanged;
            _parentWindow.PropertyChanged += OnParentWindowPropertyChanged;
        }
    }

    private void UnsubscribeFromWindowEvents()
    {
        if (_parentWindow != null)
        {
            _parentWindow.PositionChanged -= OnParentWindowPositionChanged;
            _parentWindow.PropertyChanged -= OnParentWindowPropertyChanged;
            _parentWindow = null;
        }
    }

    private void SubscribeToOverlayEvents(VideoDisplayDockViewModel vm)
    {
        vm.OverlayRightButtonDown += OnOverlayRightButtonDown;
        vm.OverlayRightButtonUp += OnOverlayRightButtonUp;
        vm.OverlayShiftKeyChanged += OnOverlayShiftKeyChanged;
        vm.FreecamSprintStateChanged += OnFreecamSprintStateChanged;
        vm.FreecamInputLockRequested += OnFreecamInputLockRequested;
        vm.FreecamInputReleaseRequested += OnFreecamInputReleaseRequested;
    }

    private void UnsubscribeFromOverlayEvents(VideoDisplayDockViewModel vm)
    {
        vm.OverlayRightButtonDown -= OnOverlayRightButtonDown;
        vm.OverlayRightButtonUp -= OnOverlayRightButtonUp;
        vm.OverlayShiftKeyChanged -= OnOverlayShiftKeyChanged;
        vm.FreecamSprintStateChanged -= OnFreecamSprintStateChanged;
        vm.FreecamInputLockRequested -= OnFreecamInputLockRequested;
        vm.FreecamInputReleaseRequested -= OnFreecamInputReleaseRequested;
    }

    private void SubscribeToHudOverlay(HudOverlayViewModel? overlay)
    {
        if (_currentHudOverlayNotifier != null)
        {
            _currentHudOverlayNotifier.PropertyChanged -= OnHudOverlayPropertyChanged;
            _currentHudOverlayNotifier = null;
            _currentHudOverlay = null;
        }

        if (overlay is INotifyPropertyChanged notifier)
        {
            _currentHudOverlayNotifier = notifier;
            _currentHudOverlay = overlay;
            notifier.PropertyChanged += OnHudOverlayPropertyChanged;
        }

        UpdateHudOverlayVisibility();
    }

    private void OnHudOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HudOverlayViewModel.ShowNativeHud))
        {
            UpdateHudOverlayVisibility();
        }
    }

    private bool IsHudOverlayVisible(VideoDisplayDockViewModel vm)
    {
        return vm.HudOverlay?.ShowNativeHud ?? false;
    }

    private void UpdateHudOverlayVisibility()
    {
        if (DataContext is not VideoDisplayDockViewModel vm)
            return;

        if (IsHudOverlayVisible(vm) && (vm.UseD3DHost || (!vm.UseD3DHost && vm.IsStreaming)))
        {
            vm.ShowHudOverlay();
            if (vm.UseD3DHost)
                UpdateHudOverlayPosition();
            else
                UpdateRtpSwapchainBounds();
        }
        else
        {
            vm.HideHudOverlay();
        }
    }

    private void OnOverlayRightButtonDown(object? sender, EventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm && (vm.UseD3DHost || (!vm.UseD3DHost && vm.IsStreaming)))
        {
            StatusBar.Focus();
            _isRightButtonDown = true;
            BeginFreecam();
        }
    }

    private void OnOverlayRightButtonUp(object? sender, EventArgs e)
    {
        if (_isRightButtonDown)
        {
            _isRightButtonDown = false;
            EndFreecam();
        }
    }

    private void OnOverlayShiftKeyChanged(object? sender, bool isPressed)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.SetSprintModifierState(isPressed);
        }
        else
        {
            _isShiftPressed = isPressed;
            UpdateSpeedScale();
        }
    }

    private void OnFreecamSprintStateChanged(object? sender, bool isPressed)
    {
        _isShiftPressed = isPressed;
        UpdateSpeedScale();
    }

    private void OnFreecamInputLockRequested(object? sender, EventArgs e)
    {
        if (_lockedCursorCenter.HasValue)
            return;

        _isRightButtonDown = true;
        BeginFreecam();
    }

    private void OnFreecamInputReleaseRequested(object? sender, EventArgs e)
    {
        if (!_lockedCursorCenter.HasValue)
            return;

        _isRightButtonDown = false;
        EndFreecam();
    }

    private void OnParentWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm && !vm.UseD3DHost && vm.IsStreaming)
        {
            UpdateRtpSwapchainBounds();
        }
        else
        {
            UpdateHudOverlayPosition();
        }
    }

    private void OnParentWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(Window.WindowState))
        {
            // Delay update slightly to allow window to finish state transition
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is VideoDisplayDockViewModel vm && !vm.UseD3DHost && vm.IsStreaming)
                {
                    UpdateRtpSwapchainBounds();
                }
                else
                {
                    UpdateHudOverlayPosition();
                }
            }, DispatcherPriority.Background);
        }
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            if (vm.UseD3DHost)
            {
                SharedTextureHost?.StartRenderer();
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
            SharedTextureHost?.StopRenderer();
            RtpSwapchainHost?.StopRenderer();
        }
    }

    private void RefreshBindsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.RefreshSpectatorBindings();
        }
    }

    private async void PlayPauseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm || IconPath == null)
            return;

        if (_isDemoPaused)
        {
            await vm.ResumeDemoAsync();
            IconPath.Data = Geometry.Parse("M6 5 H10 V19 H6 Z M14 5 H18 V19 H14 Z");
            _isDemoPaused = false;
        }
        else
        {
            await vm.PauseDemoAsync();
            IconPath.Data = Geometry.Parse("M8 5 L8 19 L19 12 Z");
            _isDemoPaused = true;
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    private static bool IsCapsLockHeld()
    {
        const int VK_CAPITAL = 0x14;
        return (GetAsyncKeyState(VK_CAPITAL) & 0x8000) != 0;
    }

    private static FreecamInitMode ResolveInitMode(VideoDisplayDockViewModel vm)
    {
        var capsLockHeld = IsCapsLockHeld();
        var useStatic = vm.FreecamSettings?.SwapRightClickInitMode == true ? !capsLockHeld : capsLockHeld;
        return useStatic ? FreecamInitMode.Static : FreecamInitMode.InheritMotion;
    }

    private void SetCursorPosition(int x, int y)
    {
        SetCursorPos(x, y);
    }

    private void OnSharedTextureRightButtonDown(object? sender, EventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm || !vm.UseD3DHost)
            return;

        if (IsHudOverlayVisible(vm))
            return;

        vm.NotifyOverlayRightButtonDown();
    }

    private void OnSharedTextureRightButtonUp(object? sender, EventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm || !vm.UseD3DHost)
            return;

        if (IsHudOverlayVisible(vm))
            return;

        vm.NotifyOverlayRightButtonUp();
    }

    private void OnRtpRightButtonDown(object? sender, EventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm || vm.UseD3DHost || !vm.IsStreaming)
            return;

        if (IsHudOverlayVisible(vm))
            return;

        vm.NotifyOverlayRightButtonDown();
    }

    private void OnRtpRightButtonUp(object? sender, EventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm || vm.UseD3DHost || !vm.IsStreaming)
            return;

        if (IsHudOverlayVisible(vm))
            return;

        vm.NotifyOverlayRightButtonUp();
    }

    private void OnSharedTextureFramePresented(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not VideoDisplayDockViewModel vm || !vm.UseD3DHost)
                return;

            vm.RecordSharedTextureFramePresented();
        }, DispatcherPriority.Background);
    }

    private void OnRtpFramePresented(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is not VideoDisplayDockViewModel vm || vm.UseD3DHost || !vm.IsStreaming)
                return;

            vm.RecordRtpFramePresented();
        }, DispatcherPriority.Background);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                vm.SetSprintModifierState(true);
            }
            else if (!_isShiftPressed)
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
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                vm.SetSprintModifierState(false);
            }
            else if (_isShiftPressed)
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
        var speedScaleRegion = HudContent?.GetSpeedScaleRegion();
        if (speedScaleRegion == null || VideoContainer == null) return;

        var bounds = VideoContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        // Set region to 30% width and 40% height
        speedScaleRegion.Width = bounds.Width * 0.3;
        speedScaleRegion.Height = bounds.Height * 0.4;
    }

    private void UpdateRtpSwapchainAspectSize()
    {
        if (RtpSwapchainAspect == null || VideoContainer == null || DataContext is not VideoDisplayDockViewModel vm)
            return;
        if (vm.UseD3DHost || !vm.IsStreaming)
            return;

        var bounds = VideoContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        double aspect = vm.RtpFrameAspect > 0 ? vm.RtpFrameAspect : 16.0 / 9.0;
        double targetW = bounds.Width;
        double targetH = targetW / aspect;
        if (targetH > bounds.Height)
        {
            targetH = bounds.Height;
            targetW = targetH * aspect;
        }

        RtpSwapchainAspect.Width = targetW;
        RtpSwapchainAspect.Height = targetH;
    }

    private void UpdateRtpSwapchainBounds()
    {
        if (RtpSwapchainHost == null || DataContext is not VideoDisplayDockViewModel vm)
            return;
        if (vm.UseD3DHost || !vm.IsStreaming)
            return;

        if (!this.IsAttachedToVisualTree())
            return;

        var targetControl = (Control?)RtpSwapchainAspect ?? RtpSwapchainHost;
        var b = targetControl.Bounds;
        var hasValidBounds = b.Width > 0 && b.Height > 0;
        Rect rect;
        PixelPoint screenTopLeft;
        if (!hasValidBounds)
        {
            if (VideoContainer == null || !VideoContainer.IsAttachedToVisualTree() || !TryGetLetterboxedRect(VideoContainer, vm.RtpFrameAspect > 0 ? vm.RtpFrameAspect : 16.0 / 9.0, out rect))
                return;

            var topLeft = VideoContainer.PointToScreen(new Point(rect.X, rect.Y));
            screenTopLeft = new PixelPoint((int)topLeft.X, (int)topLeft.Y);
            b = rect;
        }
        else
        {
            if (!targetControl.IsAttachedToVisualTree())
                return;

            rect = b;
            var topLeft = targetControl.PointToScreen(new Point(0, 0));
            screenTopLeft = new PixelPoint((int)topLeft.X, (int)topLeft.Y);
        }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        int w = (int)Math.Round(rect.Width * scale);
        int h = (int)Math.Round(rect.Height * scale);

        RtpSwapchainHost.SetContainerLayout(0, 0, w, h);
        RtpSwapchainHost.SetChildLayout(0, 0, w, h);
        RtpSwapchainHost.UpdateChildBounds();

        if (IsHudOverlayVisible(vm) && !vm.UseD3DHost && vm.IsStreaming)
        {
            try
            {
                var size = new PixelSize((int)Math.Round(rect.Width), (int)Math.Round(rect.Height));
                vm.UpdateHudOverlayBounds(screenTopLeft, size);
            }
            catch
            {
                // ignore overlay positioning failures during layout
            }
        }
    }

    private static bool TryGetLetterboxedRect(Control container, double aspect, out Rect rect)
    {
        rect = default;
        var bounds = container.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || aspect <= 0)
            return false;

        double targetW = bounds.Width;
        double targetH = targetW / aspect;
        if (targetH > bounds.Height)
        {
            targetH = bounds.Height;
            targetW = targetH * aspect;
        }

        var left = (bounds.Width - targetW) * 0.5;
        var top = (bounds.Height - targetH) * 0.5;
        rect = new Rect(left, top, targetW, targetH);
        return true;
    }

    private void BeginFreecam()
    {
        if (DataContext is not VideoDisplayDockViewModel vm)
            return;

        vm.ActivateFreecam(ResolveInitMode(vm));

        // Determine which control to use for cursor center calculation
        Control? targetControl = null;
        if (vm.UseD3DHost && SharedTextureAspect != null)
        {
            targetControl = SharedTextureAspect;
        }
        else if (!vm.UseD3DHost && vm.IsStreaming && RtpSwapchainHost != null)
        {
            targetControl = RtpSwapchainHost;
        }
        else if (NoSignalOverlay != null && NoSignalOverlay.IsVisible
            && NoSignalOverlay.Bounds.Width > 0 && NoSignalOverlay.Bounds.Height > 0)
        {
            targetControl = NoSignalOverlay;
        }
        else if (VideoContainer != null && VideoContainer.Bounds.Width > 0 && VideoContainer.Bounds.Height > 0)
        {
            targetControl = VideoContainer;
        }
        else
        {
            targetControl = this;
        }

        if (targetControl == null)
            return;

        var containerBounds = targetControl.Bounds;
        var centerPoint = new Point(containerBounds.Width / 2, containerBounds.Height / 2);
        var screenCenterPixel = targetControl.PointToScreen(centerPoint);
        var screenCenter = new Point(screenCenterPixel.X, screenCenterPixel.Y);

        _lockedCursorCenter = screenCenter;
        if (_lockedCursorCenter.HasValue)
        {
            SetCursorPosition((int)_lockedCursorCenter.Value.X, (int)_lockedCursorCenter.Value.Y);
            LockCursorToPoint(_lockedCursorCenter.Value);
        }

        Cursor = new Cursor(StandardCursorType.None);
        this.Focus();
    }

    private void OnDockPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm)
            return;

        if (vm.UseD3DHost || vm.IsStreaming)
            return;

        var pointer = e.GetCurrentPoint(this);
        if (pointer.Properties.IsRightButtonPressed && !_isRightButtonDown)
        {
            _isRightButtonDown = true;
            BeginFreecam();
            e.Handled = true;
        }
    }

    private void OnDockPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is not VideoDisplayDockViewModel vm)
            return;

        if (vm.UseD3DHost || vm.IsStreaming)
            return;

        var pointer = e.GetCurrentPoint(this);
        if (!pointer.Properties.IsRightButtonPressed && _isRightButtonDown)
        {
            _isRightButtonDown = false;
            EndFreecam();
            e.Handled = true;
        }
    }

    private void NoSignalOverlay_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        OnDockPointerPressed(sender, e);
    }

    private void NoSignalOverlay_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        OnDockPointerReleased(sender, e);
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
        // Unsubscribe from previous ViewModel
        if (_currentVmNotifier != null)
        {
            _currentVmNotifier.PropertyChanged -= OnViewModelPropertyChanged;
            _currentVmNotifier = null;
        }

        if (_currentHudOverlayNotifier != null)
        {
            _currentHudOverlayNotifier.PropertyChanged -= OnHudOverlayPropertyChanged;
            _currentHudOverlayNotifier = null;
            _currentHudOverlay = null;
        }

        if (_currentViewModel != null)
        {
            RtpSwapchainHost?.StopGStreamer();
            UnsubscribeFromOverlayEvents(_currentViewModel);
            _currentViewModel.RtpStreamRequested -= OnRtpStreamRequested;
            _currentViewModel.RtpStreamStopRequested -= OnRtpStreamStopRequested;
            _currentViewModel = null;
        }

        // Subscribe to new ViewModel
        if (DataContext is INotifyPropertyChanged notifier)
        {
            _currentVmNotifier = notifier;
            notifier.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (DataContext is VideoDisplayDockViewModel vm)
        {
            _currentViewModel = vm;
            SubscribeToOverlayEvents(vm);
            vm.RtpStreamRequested += OnRtpStreamRequested;
            vm.RtpStreamStopRequested += OnRtpStreamStopRequested;
            SubscribeToHudOverlay(vm.HudOverlay);
            UpdateRtpSwapchainBounds();

            if (vm.UseD3DHost)
            {
                SharedTextureHost?.StartRenderer();
                SharedTextureHost?.SetSharedTextureHandle(vm.SharedTextureHandle);
                UpdateHudOverlayVisibility();
            }
            else if (!vm.UseD3DHost)
            {
                if (vm.IsStreaming)
                    RtpSwapchainHost?.StartGStreamer(vm.RtpConfig);
                UpdateHudOverlayVisibility();
            }
        }

        UpdateSpeedScale();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoDisplayDockViewModel.UseD3DHost))
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                if (vm.UseD3DHost)
                {
                    RtpSwapchainHost?.StopGStreamer();
                    RtpSwapchainHost?.StopRenderer();
                    SharedTextureHost?.StartRenderer();
                    SharedTextureHost?.SetSharedTextureHandle(vm.SharedTextureHandle);
                    if (vm.IsStreaming) vm.StopStream();
                    UpdateHudOverlayVisibility();
                }
                else
                {
                    SharedTextureHost?.StopRenderer();
                    if (vm.IsStreaming)
                    {
                        RtpSwapchainHost?.StartGStreamer(vm.RtpConfig);
                    }
                    else
                    {
                        RtpSwapchainHost?.StartRenderer();
                    }
                    UpdateHudOverlayVisibility();
                }
            }
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.HudOverlay))
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                SubscribeToHudOverlay(vm.HudOverlay);
            }
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.SharedTextureHandle))
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                SharedTextureHost?.SetSharedTextureHandle(vm.SharedTextureHandle);
            }
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.FreecamSpeed))
        {
            UpdateSpeedScale();
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.RtpFrameAspect))
        {
            UpdateRtpSwapchainAspectSize();
            UpdateRtpSwapchainBounds();
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.IsStreaming))
        {
            UpdateRtpSwapchainAspectSize();
            UpdateRtpSwapchainBounds();
            UpdateHudOverlayVisibility();
        }
    }

    private void OnRtpStreamRequested(object? sender, RtpReceiverConfig config)
    {
        if (DataContext is not VideoDisplayDockViewModel vm || vm.UseD3DHost)
            return;

        RtpSwapchainHost?.StartGStreamer(config);
    }

    private void OnRtpStreamStopRequested(object? sender, EventArgs e)
    {
        RtpSwapchainHost?.StopGStreamer();
    }

    private Canvas? GetActiveSpeedScaleCanvas()
    {
        if (DataContext is VideoDisplayDockViewModel vm && (vm.UseD3DHost || (!vm.UseD3DHost && vm.IsStreaming)))
        {
            // Get canvas from overlay window when using D3DHost
            return vm.GetOverlaySpeedScaleCanvas();
        }
        // Use local canvas (from HudContent) when not using D3DHost
        return HudContent?.GetSpeedScaleCanvas();
    }

    private void UpdateSpeedScale()
    {
        var canvas = GetActiveSpeedScaleCanvas();
        if (canvas == null || DataContext is not VideoDisplayDockViewModel vm)
            return;

        var height = canvas.Bounds.Height;
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
        canvas.Width = canvasWidth;

        // Calculate target position
        double sprintInput = 0.0;
        bool analogSprintActive = false;
        double analogSprint = 0.0;
        bool analogAvailable = vm.AnalogKeyboardEnabled && vm.TryGetAnalogSprint(out analogSprint);
        if (analogAvailable)
        {
            sprintInput = analogSprint;
            if (sprintInput <= 0.0 && _isShiftPressed)
            {
                sprintInput = 1.0;
            }
            analogSprintActive = sprintInput > 0.0;
        }

        double speedMultiplier = analogAvailable
            ? 1.0 + sprintInput * (vm.SprintMultiplier - 1.0)
            : (_isShiftPressed ? vm.SprintMultiplier : 1.0);
        bool sprintActive = analogAvailable ? analogSprintActive : _isShiftPressed;
        var effectiveSpeed = vm.FreecamSpeed * speedMultiplier;
        var clampedSpeed = Math.Clamp(effectiveSpeed, vm.SpeedMin, vm.SpeedMax);
        double currentNorm = (clampedSpeed - vm.SpeedMin) / (vm.SpeedMax - vm.SpeedMin);
        double targetArrowY = marginTop + usableHeight * (1 - currentNorm);

        // Detect if speed changed vs. just a resize
        bool speedChanged = Math.Abs(effectiveSpeed - _lastEffectiveSpeed) > 0.001;
        bool shiftStateChanged = sprintActive != _lastSprintActive;
        bool heightChanged = Math.Abs(height - _lastCanvasHeight) > 0.5;

        // Initialize on first update
        if (_isFirstSpeedUpdate)
        {
            _currentArrowY = targetArrowY;
            _lastEffectiveSpeed = effectiveSpeed;
            _lastCanvasHeight = height;
            _lastSprintActive = sprintActive;
            _isFirstSpeedUpdate = false;
        }

        // Update tracked values
        _lastEffectiveSpeed = effectiveSpeed;
        _lastCanvasHeight = height;
        _lastSprintActive = sprintActive;

        // Clear and rebuild static elements
        canvas.Children.Clear();

        // Main ruler line
        var spine = new Line
        {
            StartPoint = new Point(lineX, marginTop),
            EndPoint = new Point(lineX, height - marginBottom),
            Stroke = Brushes.White,
            StrokeThickness = 3,
            StrokeLineCap = PenLineCap.Round
        };
        canvas.Children.Add(spine);

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
            canvas.Children.Add(tick);
        }

        // Top/bottom labels
        var topLabel = CreateLabel(vm.SpeedMax.ToString("0"), lineX + tickLong + 6, marginTop - 2, 12, FontWeight.SemiBold);
        var bottomLabel = CreateLabel(vm.SpeedMin.ToString("0"), lineX + tickLong + 6, height - marginBottom - 14, 12, FontWeight.SemiBold);
        canvas.Children.Add(topLabel);
        canvas.Children.Add(bottomLabel);

        // Create or update arrow and label
        if (_speedArrow == null)
        {
            _speedArrow = new Polygon();
            _speedLabel = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold };
        }

        // Update arrow appearance
        _speedArrow.Fill = sprintActive ? Brushes.Yellow : Brushes.White;
        _speedArrow.Points = new Points(new[]
        {
            new Point(lineX + tickLong + 8, 0),
            new Point(lineX + tickLong + 36, -7),
            new Point(lineX + tickLong + 36, 7)
        });

        // Update label text and appearance
        var speedText = sprintActive ? $"{effectiveSpeed:F1}" : vm.FreecamSpeedText;
        _speedLabel!.Text = speedText;
        _speedLabel.Foreground = sprintActive ? Brushes.Yellow : Brushes.White;

        // Remove from old parent if necessary before adding to new canvas
        if (_speedArrow.Parent is Panel oldArrowParent)
        {
            oldArrowParent.Children.Remove(_speedArrow);
        }
        if (_speedLabel.Parent is Panel oldLabelParent)
        {
            oldLabelParent.Children.Remove(_speedLabel);
        }

        canvas.Children.Add(_speedArrow);
        canvas.Children.Add(_speedLabel);

        // Decide whether to animate or snap
        if (heightChanged && !speedChanged && !shiftStateChanged)
        {
            // Window resize: recalculate position proportionally without animation
            CancelSpeedAnimation();
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (shiftStateChanged && _isShiftPressed)
        {
            // Shift pressed: snap immediately to doubled position
            CancelSpeedAnimation();
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (shiftStateChanged && !_isShiftPressed)
        {
            // Shift released: snap back to original position
            CancelSpeedAnimation();
            _currentArrowY = targetArrowY;
            UpdateArrowPosition(lineX, tickLong);
        }
        else if (Math.Abs(targetArrowY - _currentArrowY) < 0.5)
        {
            // Very small changes: instant update
            CancelSpeedAnimation();
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

    private void CancelSpeedAnimation()
    {
        _animationCts?.Cancel();
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

    private void OnSharedHandleInvalidated(object? sender, EventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.RequestSharedTextureHandle();
        }
    }
}
