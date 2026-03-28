using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HlaeObsTools.Services.Hotkeys;
using HlaeObsTools.ViewModels;
using System;

namespace HlaeObsTools.Views;

public partial class MainWindow : Window
{
    private bool _suppressHotkeys;
    private HotkeyService? _hotkeyService;
    private Control? _hotkeyHoveredControl;
    private bool _isHotkeyBindingMode;

    public MainWindow()
        : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        if (DataContext is MainWindowViewModel vm && vm.HotkeyService != null)
        {
            SetHotkeyOverlaySource(vm.HotkeyService);
        }

        AddHandler(InputElement.GotFocusEvent, OnInputElementGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        Deactivated += OnWindowDeactivated;
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnMenuDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Stop the event from bubbling up to the title bar
        e.Handled = true;
    }

    private void MinimizeWindow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseWindow(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnInputElementGotFocus(object? sender, GotFocusEventArgs e)
    {
        _suppressHotkeys = IsTextInputElement(e.Source);
        UpdateKeyboardSuppression(_suppressHotkeys);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _suppressHotkeys = false;
        UpdateKeyboardSuppression(false);
    }

    private void UpdateKeyboardSuppression(bool suppress)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SetKeyboardSuppression(suppress);
        }
    }

    private static bool IsTextInputElement(object? source)
    {
        return source is TextBox || source is TextPresenter;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_suppressHotkeys)
            return;

        if (DataContext is MainWindowViewModel vm && vm.HandleHotkeyKeyDown(e))
        {
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.HandleHotkeyPointerMoved(e);
        }
    }

    private void SetHotkeyOverlaySource(HotkeyService hotkeyService)
    {
        if (_hotkeyService != null)
        {
            _hotkeyService.BindingModeChanged -= OnHotkeyBindingModeChanged;
            _hotkeyService.StatusChanged -= OnHotkeyStatusChanged;
            _hotkeyService.HoverTargetChanged -= OnHotkeyHoverTargetChanged;
        }

        _hotkeyService = hotkeyService;
        _hotkeyHoveredControl = hotkeyService.HoveredControl;
        _isHotkeyBindingMode = hotkeyService.IsBindingMode;
        HotkeyStatusText.Text = hotkeyService.StatusMessage;

        hotkeyService.BindingModeChanged += OnHotkeyBindingModeChanged;
        hotkeyService.StatusChanged += OnHotkeyStatusChanged;
        hotkeyService.HoverTargetChanged += OnHotkeyHoverTargetChanged;

        RefreshHotkeyOverlay();
    }

    private void OnHotkeyBindingModeChanged(object? sender, bool isEnabled)
    {
        _isHotkeyBindingMode = isEnabled;
        if (!isEnabled)
            _hotkeyHoveredControl = null;

        RefreshHotkeyOverlay();
    }

    private void OnHotkeyStatusChanged(object? sender, string status)
    {
        HotkeyStatusText.Text = status ?? string.Empty;
        RefreshHotkeyOverlay();
    }

    private void OnHotkeyHoverTargetChanged(object? sender, HotkeyHoverChangedEventArgs e)
    {
        _hotkeyHoveredControl = e.Control;
        RefreshHotkeyOverlay();
    }

    private void RefreshHotkeyOverlay()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshHotkeyOverlay);
            return;
        }

        HotkeyStatusPanel.IsVisible = _isHotkeyBindingMode;
        if (!_isHotkeyBindingMode)
        {
            HotkeyHoverOutline.IsVisible = false;
            return;
        }

        if (!TryGetOverlayBounds(_hotkeyHoveredControl, out var x, out var y, out var width, out var height))
        {
            HotkeyHoverOutline.IsVisible = false;
            return;
        }

        Canvas.SetLeft(HotkeyHoverOutline, x);
        Canvas.SetTop(HotkeyHoverOutline, y);
        HotkeyHoverOutline.Width = width;
        HotkeyHoverOutline.Height = height;
        HotkeyHoverOutline.IsVisible = true;
    }

    private bool TryGetOverlayBounds(Control? control, out double x, out double y, out double width, out double height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        if (control == null || !control.IsVisible)
            return false;

        if (!ReferenceEquals(TopLevel.GetTopLevel(control), this))
            return false;

        var topLeft = Avalonia.VisualExtensions.TranslatePoint(control, default, HotkeyOverlayCanvas);
        if (topLeft == null)
            return false;

        var bounds = control.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        const double padding = 3;
        x = Math.Max(0, topLeft.Value.X - padding);
        y = Math.Max(0, topLeft.Value.Y - padding);
        width = bounds.Width + (padding * 2);
        height = bounds.Height + (padding * 2);
        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyService != null)
        {
            _hotkeyService.BindingModeChanged -= OnHotkeyBindingModeChanged;
            _hotkeyService.StatusChanged -= OnHotkeyStatusChanged;
            _hotkeyService.HoverTargetChanged -= OnHotkeyHoverTargetChanged;
        }

        base.OnClosed(e);

        // Ensure background services are torn down when the main window closes
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
