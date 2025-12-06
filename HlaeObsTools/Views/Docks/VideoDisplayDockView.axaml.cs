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

    public VideoDisplayDockView()
    {
        InitializeComponent();

        if (HudWebView != null)
        {
            HudWebView.Loaded += (_, _) => InjectHudOverlayStyles();
            HudWebView.AttachedToVisualTree += (_, _) => StartHudCssTimer();
            HudWebView.DetachedFromVisualTree += (_, _) => StopHudCssTimer();
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.StartRtpStream();
        }
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is VideoDisplayDockViewModel vm)
        {
            vm.StopStream();
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

            if (DataContext is VideoDisplayDockViewModel vm && VideoContainer != null)
            {
                // Activate freecam
                vm.ActivateFreecam();

                // Calculate center of video container in screen coordinates
                var containerBounds = VideoContainer.Bounds;
                var centerPoint = new Point(containerBounds.Width / 2, containerBounds.Height / 2);
                var screenCenterPixel = VideoContainer.PointToScreen(centerPoint);
                var screenCenter = new Point(screenCenterPixel.X, screenCenterPixel.Y);

                _lockedCursorCenter = screenCenter;
                _lastMousePosition = screenCenter; // Initialize last position

                // Set cursor to center
                if (_lockedCursorCenter.HasValue)
                {
                    SetCursorPosition((int)_lockedCursorCenter.Value.X, (int)_lockedCursorCenter.Value.Y);
                }

                // Hide cursor
                Cursor = new Cursor(StandardCursorType.None);

                // Focus for keyboard input
                this.Focus();

                e.Handled = true;
            }
        }
    }

    private void VideoContainer_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(this);

        // Right mouse button released - deactivate freecam
        if (!pointer.Properties.IsRightButtonPressed && _isRightButtonDown)
        {
            _isRightButtonDown = false;

            if (DataContext is VideoDisplayDockViewModel vm)
            {
                // Deactivate freecam
                vm.DeactivateFreecam();

                // Show cursor
                Cursor = Cursor.Default;

                _lockedCursorCenter = null;
                _lastMousePosition = null;

                e.Handled = true;
            }
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

    private void SetCursorPosition(int x, int y)
    {
        SetCursorPos(x, y);
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
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoDisplayDockViewModel.IsHudEnabled) ||
            e.PropertyName == nameof(VideoDisplayDockViewModel.HudAddress))
        {
            SyncHudState();
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
