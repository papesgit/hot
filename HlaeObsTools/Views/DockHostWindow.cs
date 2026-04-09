using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using HlaeObsTools.Services.Hotkeys;

namespace HlaeObsTools.Views;

public class DockHostWindow : HostWindow
{
    private Action<bool>? _keyboardSuppressionHandler;
    private Func<KeyEventArgs, bool>? _hotkeyKeyDownHandler;
    private Action<PointerEventArgs>? _hotkeyPointerMovedHandler;
    private HotkeyService? _hotkeyService;
    private Control? _hotkeyHoveredControl;
    private bool _isHotkeyBindingMode;
    private bool _suppressHotkeys;

    private Canvas? _hotkeyOverlayCanvas;
    private Border? _hotkeyHoverOutline;
    private Border? _hotkeyStatusPanel;
    private TextBlock? _hotkeyStatusText;
    private bool _isResolvingOverlayParts;

    public DockHostWindow()
    {
        AddHandler(InputElement.GotFocusEvent, OnInputElementGotFocus, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        Deactivated += OnWindowDeactivated;
        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
    }

    protected override Type StyleKeyOverride => typeof(DockHostWindow);

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

        if (_hotkeyStatusText != null)
        {
            _hotkeyStatusText.Text = hotkeyService.StatusMessage;
        }

        hotkeyService.BindingModeChanged += OnHotkeyBindingModeChanged;
        hotkeyService.StatusChanged += OnHotkeyStatusChanged;
        hotkeyService.HoverTargetChanged += OnHotkeyHoverTargetChanged;

        RefreshHotkeyOverlay();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        Dispatcher.UIThread.Post(ResolveOverlayParts, DispatcherPriority.Loaded);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(ResolveOverlayParts, DispatcherPriority.Loaded);
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
    }

    private void ResolveOverlayParts()
    {
        if (_isResolvingOverlayParts)
            return;

        _isResolvingOverlayParts = true;
        try
        {
            _hotkeyOverlayCanvas = this.GetVisualDescendants().OfType<Canvas>().FirstOrDefault(x => x.Name == "PART_HotkeyOverlayCanvas");
            _hotkeyHoverOutline = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "PART_HotkeyHoverOutline");
            _hotkeyStatusPanel = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(x => x.Name == "PART_HotkeyStatusPanel");
            _hotkeyStatusText = this.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.Name == "PART_HotkeyStatusText");

            if (_hotkeyService != null && _hotkeyStatusText != null)
            {
                _hotkeyStatusText.Text = _hotkeyService.StatusMessage;
            }
        }
        finally
        {
            _isResolvingOverlayParts = false;
        }
    }

    private void OnInputElementGotFocus(object? sender, FocusChangedEventArgs e)
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
        if (_hotkeyStatusText != null)
        {
            _hotkeyStatusText.Text = status ?? string.Empty;
        }

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

        if (_hotkeyStatusPanel == null || _hotkeyHoverOutline == null || _hotkeyOverlayCanvas == null)
        {
            ResolveOverlayParts();
        }

        if (_hotkeyStatusPanel == null || _hotkeyHoverOutline == null)
            return;

        _hotkeyStatusPanel.IsVisible = _isHotkeyBindingMode;
        if (!_isHotkeyBindingMode)
        {
            _hotkeyHoverOutline.IsVisible = false;
            return;
        }

        if (!TryGetOverlayBounds(_hotkeyHoveredControl, out var x, out var y, out var width, out var height))
        {
            _hotkeyHoverOutline.IsVisible = false;
            return;
        }

        Canvas.SetLeft(_hotkeyHoverOutline, x);
        Canvas.SetTop(_hotkeyHoverOutline, y);
        _hotkeyHoverOutline.Width = width;
        _hotkeyHoverOutline.Height = height;
        _hotkeyHoverOutline.IsVisible = true;
    }

    private bool TryGetOverlayBounds(Control? control, out double x, out double y, out double width, out double height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        if (control == null || !control.IsVisible || _hotkeyOverlayCanvas == null)
            return false;

        if (!ReferenceEquals(TopLevel.GetTopLevel(control), this))
            return false;

        var topLeft = Avalonia.VisualExtensions.TranslatePoint(control, default, _hotkeyOverlayCanvas);
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
}
