using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
#if DEBUG
using Dock.Avalonia.Diagnostics;
using Dock.Avalonia.Diagnostics.Controls;
#endif
using HlaeObsTools.Services.Hotkeys;
using HlaeObsTools.ViewModels;
using System;
using System.Collections.Generic;

namespace HlaeObsTools.Views;

public partial class MainWindow : Window
{
    private const string DocumentationUrl = "https://github.com/papesgit/hot/wiki";
    private bool _suppressHotkeys;
    private HotkeyService? _hotkeyService;
    private Control? _hotkeyHoveredControl;
    private bool _isHotkeyBindingMode;
#if DEBUG
    private IDisposable? _dockDebugOverlaySubscription;
    private IDisposable? _dockDebugWindowSubscription;
#endif

    public MainWindow()
        : this(new MainWindowViewModel())
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        InitializeDockMenu(viewModel);
        InitializeLayoutMenu(viewModel);
        viewModel.LayoutMenuChanged += OnLayoutMenuChanged;
        if (DataContext is MainWindowViewModel vm && vm.HotkeyService != null)
        {
            SetHotkeyOverlaySource(vm.HotkeyService);
        }

        AddHandler(InputElement.GotFocusEvent, OnInputElementGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        Deactivated += OnWindowDeactivated;
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);

#if DEBUG
        _dockDebugOverlaySubscription = this.AttachDockDebugOverlay();
        _dockDebugWindowSubscription = this.AttachDockDebug(
            () => (DataContext as MainWindowViewModel)?.Layout);
#endif
    }

    private void InitializeDockMenu(MainWindowViewModel viewModel)
    {
        var menuItems = new MenuItem[viewModel.DockMenuItems.Count];
        for (var index = 0; index < viewModel.DockMenuItems.Count; index++)
        {
            var dockItem = viewModel.DockMenuItems[index];
            var menuItem = new MenuItem
            {
                Header = dockItem.Title,
                ToggleType = MenuItemToggleType.CheckBox,
                DataContext = dockItem
            };
            menuItem.Bind(
                MenuItem.IsCheckedProperty,
                new Binding(nameof(DockMenuItemViewModel.IsOpen))
                {
                    Mode = BindingMode.OneWay
                });
            menuItem.Click += (_, _) => viewModel.ToggleDock(dockItem.Id);
            menuItems[index] = menuItem;
        }

        ViewMenuItem.ItemsSource = menuItems;
    }

    private void InitializeLayoutMenu(MainWindowViewModel viewModel)
    {
        var items = new List<object>();
        foreach (var layoutItem in viewModel.LayoutMenuItems)
        {
            var menuItem = new MenuItem
            {
                Header = layoutItem.Name,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "DockLayouts",
                DataContext = layoutItem
            };
            menuItem.Bind(
                MenuItem.IsCheckedProperty,
                new Binding(nameof(LayoutMenuItemViewModel.IsSelected))
                {
                    Mode = BindingMode.OneWay
                });
            menuItem.Click += (_, _) => viewModel.SelectLayout(layoutItem.Name);
            items.Add(menuItem);
        }

        items.Add(new Separator());
        var newLayoutItem = new MenuItem { Header = "New Layout\u2026" };
        newLayoutItem.Click += NewLayout;
        items.Add(newLayoutItem);

        var deleteLayoutItem = new MenuItem
        {
            Header = "Delete Layout\u2026",
            IsEnabled = viewModel.CanDeleteActiveLayout
        };
        deleteLayoutItem.Click += DeleteLayout;
        items.Add(deleteLayoutItem);

        LayoutMenuItem.ItemsSource = items;
    }

    private void OnLayoutMenuChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            InitializeLayoutMenu(viewModel);
    }

    private async void NewLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        var name = await DialogHelpers.PromptAsync(
            this,
            "New Layout",
            "Save the current dock arrangement as:",
            "Layout name");
        if (name == null)
            return;

        var error = viewModel.SaveCurrentLayout(name);
        if (error != null)
            await DialogHelpers.MessageAsync(this, "Unable to save layout", error);
    }

    private async void DeleteLayout(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (!viewModel.CanDeleteActiveLayout
            || string.IsNullOrWhiteSpace(viewModel.ActiveLayoutName))
        {
            return;
        }

        var confirmed = await DialogHelpers.ConfirmAsync(
            this,
            "Delete Layout",
            $"Delete the layout \"{viewModel.ActiveLayoutName}\"? The current dock arrangement will remain open, but the saved layout cannot be recovered.");
        if (confirmed)
            viewModel.DeleteLayout(viewModel.ActiveLayoutName);
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (IsInteractiveTitleBarElement(e.Source))
            return;

        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private static bool IsInteractiveTitleBarElement(object? source)
    {
        if (source is Menu or MenuItem or Button)
            return true;

        if (source is not Visual visual)
            return false;

        foreach (var ancestor in visual.GetVisualAncestors())
        {
            if (ancestor is Menu or MenuItem or Button)
                return true;
        }

        return false;
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

    private void ExitApplication(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OpenDocumentation(object? sender, RoutedEventArgs e)
    {
        if (!ExternalLinkLauncher.TryOpen(DocumentationUrl))
        {
            await DialogHelpers.MessageAsync(
                this,
                "Unable to open documentation",
                $"Open this address in your browser:\n{DocumentationUrl}");
        }
    }

    private async void ShowAbout(object? sender, RoutedEventArgs e)
    {
        var version = (DataContext as MainWindowViewModel)?.Version ?? "unknown";
        var dialog = new AboutWindow(version);
        await dialog.ShowDialog(this);
    }

    private void OnInputElementGotFocus(object? sender, FocusChangedEventArgs e)
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
#if DEBUG
        _dockDebugOverlaySubscription?.Dispose();
        _dockDebugOverlaySubscription = null;
        _dockDebugWindowSubscription?.Dispose();
        _dockDebugWindowSubscription = null;
#endif

        if (_hotkeyService != null)
        {
            _hotkeyService.BindingModeChanged -= OnHotkeyBindingModeChanged;
            _hotkeyService.StatusChanged -= OnHotkeyStatusChanged;
            _hotkeyService.HoverTargetChanged -= OnHotkeyHoverTargetChanged;
        }

        if (DataContext is MainWindowViewModel viewModel)
            viewModel.LayoutMenuChanged -= OnLayoutMenuChanged;

        base.OnClosed(e);

        // Ensure background services are torn down when the main window closes
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
