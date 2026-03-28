using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class AttachPresetAnimationDockView : UserControl
{
    public AttachPresetAnimationDockView()
    {
        InitializeComponent();
    }

    private void DeleteEvent_Click(object? sender, RoutedEventArgs e)
    {
        ExecuteItemCommand(sender, vm => vm.DeleteEventCommand);
    }

    private void ExecuteItemCommand(object? sender, Func<AttachPresetAnimationDockViewModel, System.Windows.Input.ICommand> commandSelector)
    {
        if (DataContext is not AttachPresetAnimationDockViewModel vm)
            return;

        if (sender is not Button button)
            return;

        if (button.DataContext is not AttachPresetAnimationEventViewModel item)
            return;

        var command = commandSelector(vm);
        if (command.CanExecute(item))
        {
            command.Execute(item);
        }
    }
}
