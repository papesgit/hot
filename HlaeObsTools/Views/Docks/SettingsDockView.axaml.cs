using Avalonia.Controls;
using Avalonia.Interactivity;
using HlaeObsTools.Services.Campaths;
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

    private async void OnCampathEditorModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not SettingsDockViewModel vm
            || sender is not ComboBox comboBox
            || comboBox.SelectedItem is not CampathEditorModeOption option)
            return;

        var editor = vm.CampathEditor;
        if (option.Mode == editor.EditorMode)
            return;

        var changesModel = (option.Mode == CampathEditorMode.Curves) != editor.IsCurveMode;
        if (changesModel && editor.HasAuthoredKeys)
        {
            var message = option.Mode == CampathEditorMode.Curves
                ? "Convert the classic compound keyframes into independently editable curve channels? This can be undone."
                : $"Convert the editable curves to {option.DisplayName}? Independent channel timing and curve handles will be flattened into compound keyframes. This can be undone.";
            if (!await DialogHelpers.ConfirmAsync(this, "Convert camera path", message))
            {
                comboBox.SelectedItem = editor.SelectedEditorModeOption;
                return;
            }
        }

        editor.SetEditorMode(option.Mode);
    }
}
