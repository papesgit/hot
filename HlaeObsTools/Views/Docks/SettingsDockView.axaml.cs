using Avalonia.Controls;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class SettingsDockView : UserControl
{
    public SettingsDockView()
    {
        InitializeComponent();
    }

    private void OpenAnimationEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsDockViewModel vm)
            return;

        if (sender is not Button button)
            return;

        if (button.DataContext is not AttachPresetViewModel preset)
            return;

        if (vm.OpenAttachPresetAnimationCommand.CanExecute(preset))
        {
            vm.OpenAttachPresetAnimationCommand.Execute(preset);
        }
    }
}
