using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HlaeObsTools.ViewModels.Docks;
using System;
using WebViewControl;
using System.ComponentModel;
using Avalonia.Threading;

namespace HlaeObsTools.Views.Docks;

public partial class VideoDisplayDockView : UserControl
{
    private Point? _lockedCursorCenter;
    private bool _isRightButtonDown;
    private Point? _lastMousePosition;
    private DispatcherTimer? _hudCssTimer;
    private INotifyPropertyChanged? _currentVmNotifier;
    private bool _cursorHidden;

    public VideoDisplayDockView()
    {
        InitializeComponent();

        if (HudWebView != null)
        {
            HudWebView.Loaded += (_, _) => InjectHudOverlayStyles();
            HudWebView.AttachedToVisualTree += (_, _) => StartHudCssTimer();
            HudWebView.DetachedFromVisualTree += (_, _) => StopHudCssTimer();
        }

        if (VideoContainer != null)
        {
            VideoContainer.SizeChanged += (_, _) => UpdateSharedTextureAspectSize();
        }
        this.AttachedToVisualTree += (_, _) => UpdateSharedTextureAspectSize();

        if (SharedTextureHost != null)
        {
            SharedTextureHost.RightButtonDown += SharedTextureHost_RightButtonDown;
            SharedTextureHost.RightButtonUp += SharedTextureHost_RightButtonUp;
        }

        DataContextChanged += OnDataContextChanged;
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

    private void SharedTextureHost_RightButtonDown(object? sender, EventArgs e)
    {
        // Mirror the freecam activation when clicking on the shared texture host.
        if (_isRightButtonDown) return;

        _isRightButtonDown = true;
        BeginFreecam();
    }

    private void SharedTextureHost_RightButtonUp(object? sender, EventArgs e)
    {
        if (!_isRightButtonDown) return;

        _isRightButtonDown = false;
        EndFreecam();
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

        SyncHudState();

        if (DataContext is VideoDisplayDockViewModel vm)
        {
            if (vm.UseD3DHost)
            {
                SharedTextureHost?.StartRenderer();
            }
            else if (vm.UseSharedTextureCpu)
            {
                vm.StartStream();
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoDisplayDockViewModel.IsHudEnabled) ||
            e.PropertyName == nameof(VideoDisplayDockViewModel.HudAddress))
        {
            SyncHudState();
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.UseD3DHost))
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                if (vm.UseD3DHost)
                {
                    SharedTextureHost?.StartRenderer();
                    if (vm.IsStreaming) vm.StopStream();
                }
                else
                {
                    SharedTextureHost?.StopRenderer();
                }
            }
        }
        else if (e.PropertyName == nameof(VideoDisplayDockViewModel.UseSharedTextureCpu))
        {
            if (DataContext is VideoDisplayDockViewModel vm)
            {
                if (vm.UseSharedTextureCpu)
                {
                    vm.StartStream();
                }
                else if (vm.IsStreaming)
                {
                    vm.StopStream();
                }
            }
        }
    }

    private void SyncHudState()
    {
        if (HudWebView == null || DataContext is not VideoDisplayDockViewModel vm)
            return;

        if (vm.IsHudEnabled)
        {
            InjectHudOverlayStyles();
            HudWebView.InvalidateMeasure();
            HudWebView.InvalidateVisual();
            StartHudCssTimer();
        }
        else
        {
            StopHudCssTimer();
        }
    }

    private void StartHudCssTimer()
    {
        if (HudWebView == null || DataContext is not VideoDisplayDockViewModel vm || !vm.IsHudEnabled)
            return;

        _hudCssTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        _hudCssTimer.Tick -= HudCssTimerOnTick;
        _hudCssTimer.Tick += HudCssTimerOnTick;
        if (!_hudCssTimer.IsEnabled)
        {
            _hudCssTimer.Start();
        }
    }

    private void StopHudCssTimer()
    {
        if (_hudCssTimer != null && _hudCssTimer.IsEnabled)
        {
            _hudCssTimer.Stop();
        }
    }

    private void HudCssTimerOnTick(object? sender, EventArgs e)
    {
        InjectHudOverlayStyles();
    }

    private void InjectHudOverlayStyles()
    {
        const string script = """
(() => {
    try {
        const apply = () => {
            const style = document.createElement('style');
            style.innerHTML = `
                html, body { margin:0; padding:0; overflow:hidden; background:transparent !important; }
                ::-webkit-scrollbar { width:0px; height:0px; display:none; }
            `;
            document.head.appendChild(style);
            if (document.documentElement) {
                document.documentElement.style.background = 'transparent';
                document.documentElement.style.overflow = 'hidden';
            }
            if (document.body) {
                document.body.style.background = 'transparent';
                document.body.style.overflow = 'hidden';
            }
        };

        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', apply, { once: true });
        } else {
            apply();
        }
    } catch (err) {
        console.error('HUD style injection failed', err);
    }
})();
""";

        try
        {
            _ = HudWebView?.EvaluateScript<object>(script);
        }
        catch
        {
            // ignore injection errors
        }
    }
}
