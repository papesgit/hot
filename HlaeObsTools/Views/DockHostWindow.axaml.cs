using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using System;
using HlaeObsTools.Services.Hotkeys;

namespace HlaeObsTools.Views;

public partial class DockHostWindow : Window, IHostWindow
{
    private Action<bool>? _keyboardSuppressionHandler;
    private Func<KeyEventArgs, bool>? _hotkeyKeyDownHandler;
    private Action<PointerEventArgs>? _hotkeyPointerMovedHandler;
    private HotkeyService? _hotkeyService;
    private Control? _hotkeyHoveredControl;
    private bool _isHotkeyBindingMode;
    private bool _suppressHotkeys;

    public DockHostWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.GotFocusEvent, OnInputElementGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        Deactivated += OnWindowDeactivated;
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
    }

    public IDockWindow? Window { get; set; }

    public IDockManager? DockManager { get; set; }

    public IHostWindowState? HostWindowState { get; set; }

    public bool IsTracked { get; set; }

    public IDockable? DockableViewModel
    {
        get => DockControl?.DataContext as IDockable;
        set
        {
            if (DockControl != null)
            {
                DockControl.DataContext = value;
            }
        }
    }

    public bool OnClose()
    {
        // Allow the window to close
        return true;
    }

    public void OnClosed()
    {
        if (_hotkeyService != null)
        {
            _hotkeyService.BindingModeChanged -= OnHotkeyBindingModeChanged;
            _hotkeyService.StatusChanged -= OnHotkeyStatusChanged;
            _hotkeyService.HoverTargetChanged -= OnHotkeyHoverTargetChanged;
        }
    }

    public void Present(bool isDialog)
    {
        if (!isDialog)
        {
            Show();
        }
        else
        {
            if (Owner is Window ownerWindow)
            {
                ShowDialog(ownerWindow);
            }
            else
            {
                ShowDialog(null!);
            }
        }
    }

    public void Exit()
    {
        Close();
    }

    public void SetPosition(double x, double y)
    {
        Position = new PixelPoint((int)x, (int)y);
    }

    public void GetPosition(out double x, out double y)
    {
        x = Position.X;
        y = Position.Y;
    }

    public void SetSize(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public void GetSize(out double width, out double height)
    {
        width = Width;
        height = Height;
    }

    public void SetTitle(string? title)
    {
        if (!string.IsNullOrEmpty(title))
        {
            Title = title;
        }
    }

    public void SetLayout(IDock? dock)
    {
        if (DockControl != null)
        {
            DockControl.Layout = dock;
        }
    }

    public void SetKeyboardSuppressionHandler(Action<bool> handler)
    {
        _keyboardSuppressionHandler = handler;
    }

    public void SetHotkeyHandlers(Func<KeyEventArgs, bool> keyDownHandler, Action<PointerEventArgs> pointerMovedHandler)
    {
        _hotkeyKeyDownHandler = keyDownHandler;
        _hotkeyPointerMovedHandler = pointerMovedHandler;
    }

    public void SetHotkeyOverlaySource(HotkeyService hotkeyService)
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

    private void OnInputElementGotFocus(object? sender, GotFocusEventArgs e)
    {
        _suppressHotkeys = IsTextInputElement(e.Source);
        _keyboardSuppressionHandler?.Invoke(_suppressHotkeys);
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _suppressHotkeys = false;
        _keyboardSuppressionHandler?.Invoke(false);
    }

    private static bool IsTextInputElement(object? source)
    {
        return source is TextBox || source is TextPresenter;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_suppressHotkeys)
            return;

        if (_hotkeyKeyDownHandler != null && _hotkeyKeyDownHandler(e))
        {
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        _hotkeyPointerMovedHandler?.Invoke(e);
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

    public void SetActive()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }
}
