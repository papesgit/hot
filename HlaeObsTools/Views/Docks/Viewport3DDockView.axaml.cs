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
