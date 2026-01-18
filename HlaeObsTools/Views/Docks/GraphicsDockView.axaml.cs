using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Views;

namespace HlaeObsTools.Views.Docks;

public partial class GraphicsDockView : UserControl
{
    public GraphicsDockView()
    {
        InitializeComponent();
    }

    private async void OnAddAtlasClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "New atlas", "Atlas name", "atlas");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.AddAtlas(name);
    }

    private async void OnAddRegionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "New region", "Region id", "region");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.AddRegion(name);
    }

    private async void OnAddInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "New instance", "Instance name", "gfx");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.AddInstance(name);
    }

    private async void OnSaveProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "Save profile", "Profile name", vm.SelectedProfileName);
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.SaveProfileAs(name);
    }

    private async void OnRemoveProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;
        if (string.IsNullOrWhiteSpace(vm.SelectedProfileName))
            return;

        var ok = await DialogHelpers.ConfirmAsync(this, "Remove profile",
            $"Are you sure you want to delete profile '{vm.SelectedProfileName}'?");
        if (!ok)
            return;
        vm.RemoveSelectedProfile();
    }

}
