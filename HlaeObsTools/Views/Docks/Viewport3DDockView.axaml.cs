using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class Viewport3DDockView : UserControl
{
    public Viewport3DDockView()
    {
        InitializeComponent();

        AddHandler(PointerPressedEvent, OnViewportPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnViewportPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerMovedEvent, OnViewportPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerWheelChangedEvent, OnViewportPointerWheelChanged, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);

        KeyDown += OnViewportKeyDown;
        this.DataContextChanged += (_, _) => WirePins();
        WirePins();
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Viewport != null)
            Viewport.ForwardPointerPressed(e);
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Viewport != null)
            Viewport.ForwardPointerReleased(e);
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (Viewport != null)
            Viewport.ForwardPointerMoved(e);
    }

    private void OnViewportPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Viewport != null)
            Viewport.ForwardPointerWheel(e);
    }

    private void OnViewportKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.B)
            return;

        if (Viewport == null || !Viewport.IsKeyboardFocusWithin)
            return;

        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        if (!Viewport.TryGetFreecamState(out var state))
            return;

        vm.HandoffFreecam(state);
        Viewport.DisableFreecamInput();
        e.Handled = true;
    }

    private void WirePins()
    {
        if (DataContext is Viewport3DDockViewModel vm)
        {
            vm.PinsUpdated -= OnPinsUpdated;
            vm.PinsUpdated += OnPinsUpdated;
        }
    }

    private void OnPinsUpdated(IReadOnlyList<ViewportPin> pins)
    {
        if (Viewport != null)
        {
            Viewport.SetPins(pins);
        }
    }
}
