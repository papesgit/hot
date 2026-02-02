using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using System;

namespace HlaeObsTools.Views;

public partial class DockHostWindow : Window, IHostWindow
{
    private Action<bool>? _keyboardSuppressionHandler;
    private Func<KeyEventArgs, bool>? _hotkeyKeyDownHandler;
    private Action<PointerEventArgs>? _hotkeyPointerMovedHandler;
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
        // Cleanup if needed
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

    public void SetActive()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
    }
}
