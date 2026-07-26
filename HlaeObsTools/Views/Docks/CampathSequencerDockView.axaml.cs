using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathSequencerDockView : UserControl
{
    public CampathSequencerDockView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel, true);
    }

    private void OnUndoRedoKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CampathSequencerDockViewModel vm
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                vm.Sequence.Redo();
            else
                vm.Sequence.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            vm.Sequence.Redo();
            e.Handled = true;
        }
    }
}
