using System.Threading.Tasks;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Views;

namespace HlaeObsTools.Views.Docks;

public partial class GraphicsDockView : UserControl
{
    public GraphicsDockView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, true);
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

        var name = await DialogHelpers.PromptAsync(this, "Save profile as", "Profile name", "profile");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.SaveEmptyProfileAs(name);
    }

    private async void OnNewProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "New profile", "Profile name", "profile");
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (vm.HasUnsavedChanges && !await ConfirmDiscardChangesAsync())
            return;
        vm.CreateProfile(name);
    }

    private async void OnDuplicateProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "Duplicate profile", "New profile name", $"{vm.SelectedProfileName} copy");
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (vm.HasUnsavedChanges && !await DialogHelpers.ConfirmAsync(this, "Unsaved changes",
                "You have unsaved changes. They will be saved before duplicating. Are you sure you wanna proceed?"))
        {
            return;
        }
        vm.DuplicateCurrentProfile(name);
    }

    private async void OnRenameProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await DialogHelpers.PromptAsync(this, "Rename profile", "New profile name", vm.SelectedProfileName);
        if (!string.IsNullOrWhiteSpace(name))
            vm.RenameCurrentProfile(name);
    }

    private void OnSaveCurrentProfileClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GraphicsDockViewModel vm)
            vm.SaveCurrentProfile();
    }

    private async void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm
            || sender is not ComboBox { SelectedItem: GraphicsDockViewModel.GraphicsProfileListItem profile })
        {
            return;
        }

        if (string.Equals(profile.Name, vm.SelectedProfileName, System.StringComparison.OrdinalIgnoreCase))
            return;

        if (vm.HasUnsavedChanges && !await ConfirmDiscardChangesAsync())
        {
            vm.RestoreSelectedProfile();
            return;
        }

        vm.LoadProfile(profile.Name);
    }

    private Task<bool> ConfirmDiscardChangesAsync()
    {
        return DialogHelpers.ConfirmAsync(this, "Unsaved changes",
            "You have unsaved changes. Are you sure you wanna proceed?");
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

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        await CopySelectionAsync();
    }

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        await PasteSelectionAsync();
    }

    private void OnAtlasItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && DataContext is GraphicsDockViewModel vm
            && sender is Control { DataContext: GraphicsDockViewModel.GraphicsAtlasViewModel atlas })
        {
            vm.ActivateAtlas(atlas);
        }
    }

    private void OnRegionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && DataContext is GraphicsDockViewModel vm
            && sender is Control { DataContext: GraphicsDockViewModel.GraphicsRegionViewModel region })
        {
            vm.ActivateRegion(region);
        }
    }

    private void OnInstanceItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && DataContext is GraphicsDockViewModel vm
            && sender is Control { DataContext: GraphicsDockViewModel.GraphicsInstanceViewModel instance })
        {
            vm.ActivateInstance(instance);
        }
    }

    private void OnRegionSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is GraphicsDockViewModel vm && sender is ListBox list)
            vm.SetSelectedRegions(list.SelectedItems?.OfType<GraphicsDockViewModel.GraphicsRegionViewModel>() ?? Enumerable.Empty<GraphicsDockViewModel.GraphicsRegionViewModel>());
    }

    private void OnInstanceSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is GraphicsDockViewModel vm && sender is ListBox list)
            vm.SetSelectedInstances(list.SelectedItems?.OfType<GraphicsDockViewModel.GraphicsInstanceViewModel>() ?? Enumerable.Empty<GraphicsDockViewModel.GraphicsInstanceViewModel>());
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm || e.Source is TextBox)
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.D:
                    vm.DuplicateSelectedCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.C:
                    await CopySelectionAsync();
                    e.Handled = true;
                    return;
                case Key.V:
                    await PasteSelectionAsync();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Delete)
        {
            vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async Task CopySelectionAsync()
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;
        var content = vm.SerializeSelectedItem();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (content == null || clipboard == null)
            return;
        await clipboard.SetTextAsync(content);
    }

    private async Task PasteSelectionAsync()
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;
        vm.PasteSerializedItem(await clipboard.TryGetTextAsync());
    }

}
