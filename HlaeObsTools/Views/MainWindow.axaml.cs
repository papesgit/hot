using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels;
using System;

namespace HlaeObsTools.Views;

public partial class MainWindow : Window
{
    private bool _suppressHotkeys;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();

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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);

        // Ensure background services are torn down when the main window closes
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
