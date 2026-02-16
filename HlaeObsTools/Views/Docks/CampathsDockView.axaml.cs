using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Layout;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathsDockView : UserControl
{
    public CampathsDockView()
    {
        InitializeComponent();
        InitializePopulateFlyout();
        DataContextChanged += OnDataContextChanged;
    }

    private static readonly DataFormat<string> CampathDragFormat =
        DataFormat.CreateStringApplicationFormat("hlaeobs.campath-id");
    private static readonly DataFormat<string> GroupDragFormat =
        DataFormat.CreateStringApplicationFormat("hlaeobs.group-id");
    private const double CampathDragThreshold = 4.0;
    private CampathItemViewModel? _campathPressedItem;
    private Point? _campathPressPoint;
    private bool _campathDragInitiated;
    private IPointer? _campathPointer;
    private CampathGroupViewModel? _groupPressed;
    private Point? _groupPressPoint;
    private bool _groupDragInitiated;
    private IPointer? _groupPointer;
    private Button? _populateProfileButton;
    private FlyoutBase? _populateSourceFlyout;
    private TaskCompletionSource<CampathPopulateSource?>? _populateSourceTcs;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CampathsDockViewModel vm)
        {
            vm.PromptAsync = PromptAsync;
            vm.ConfirmAsync = ConfirmAsync;
            vm.SelectPopulateSourceAsync = SelectPopulateSourceAsync;
            vm.BrowseFileAsync = BrowseFileAsync;
            vm.BrowseFilesAsync = BrowseFilesAsync;
            vm.BrowseFolderAsync = BrowseFolderAsync;
            vm.ViewGroupRequested += OnViewGroupRequested;
        }
    }

    private async Task<string?> PromptAsync(string title, string message, int width, int height)
    {
        var dialog = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var textBox = new TextBox { Margin = new Thickness(0, 6, 0, 6) };
        var okButton = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message });
        panel.Children.Add(textBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close(true);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        await dialog.ShowDialog<bool?>(host);
        return result;
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var okButton = new Button { Content = "Delete", IsDefault = true, Width = 90 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 90 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        okButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return false;

        var result = await dialog.ShowDialog<bool?>(host);
        return result == true;
    }

    private async Task<string?> BrowseFileAsync(string title)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        var storageProvider = host.StorageProvider;
        if (storageProvider == null)
            return null;

        var result = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

        if (result is { Count: > 0 })
            return result[0].Path.LocalPath;

        return null;
    }

    private async Task<string?> BrowseFolderAsync(string title)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        var storageProvider = host.StorageProvider;
        if (storageProvider == null)
            return null;

        var result = await storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });

        if (result is { Count: > 0 })
            return result[0].Path.LocalPath;

        return null;
    }

    private async Task<IEnumerable<string>?> BrowseFilesAsync(string title)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        var storageProvider = host.StorageProvider;
        if (storageProvider == null)
            return null;

        var result = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true
            });

        return result?.Select(item => item.Path.LocalPath).ToList();
    }

    private async Task<CampathPopulateSource?> SelectPopulateSourceAsync()
    {
        if (_populateProfileButton == null || _populateSourceFlyout == null)
            return null;

        if (_populateSourceTcs != null)
            return await _populateSourceTcs.Task;

        var tcs = new TaskCompletionSource<CampathPopulateSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _populateSourceTcs = tcs;
        FlyoutBase.ShowAttachedFlyout(_populateProfileButton);
        return await tcs.Task;
    }

    private void CompletePopulateSelection(CampathPopulateSource? choice)
    {
        var tcs = _populateSourceTcs;
        if (tcs == null)
            return;

        _populateSourceTcs = null;
        tcs.TrySetResult(choice);
    }

    private void OnPopulateSelectFolderClicked(object? sender, RoutedEventArgs e)
    {
        CompletePopulateSelection(CampathPopulateSource.Folder);
    }

    private void OnPopulateSelectFilesClicked(object? sender, RoutedEventArgs e)
    {
        CompletePopulateSelection(CampathPopulateSource.Files);
    }

    private void OnPopulateSelectCancelClicked(object? sender, RoutedEventArgs e)
    {
        CompletePopulateSelection(null);
    }

    private void OnPopulateFlyoutClosed(object? sender, EventArgs e)
    {
        CompletePopulateSelection(null);
    }

    private void OnViewGroupRequested(object? sender, CampathGroupViewModel? group)
    {
        if (group == null || DataContext is not CampathsDockViewModel vm)
            return;

        var host = TopLevel.GetTopLevel(this) as Window;
        if (host != null)
        {
            var window = new CampathGroupViewWindow(vm, group);
            window.Show(host);
        }
    }

    private void OnCampathPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control &&
            control.DataContext is CampathItemViewModel campathVm)
        {
            _campathPressedItem = campathVm;
            _campathPressPoint = e.GetPosition(this);
            _campathDragInitiated = false;
            _campathPointer = e.Pointer;
            _campathPointer.Capture(control);
            e.Handled = true;
        }
    }

    private async void OnCampathPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_campathPressedItem == null || _campathDragInitiated || !_campathPressPoint.HasValue)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetCampathPointerState(sender as Control);
            return;
        }

        var current = e.GetPosition(this);
        var delta = current - _campathPressPoint.Value;
        var distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        if (distance < CampathDragThreshold)
            return;

        if (sender is Control control && control.DataContext is CampathItemViewModel campathVm && ReferenceEquals(campathVm, _campathPressedItem))
        {
            _campathDragInitiated = true;
            var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(CampathDragFormat, campathVm.Id.ToString("D"));
            data.Add(item);
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            ResetCampathPointerState(control);
        }
    }

    private async void OnCampathPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && _campathPressedItem != null && !_campathDragInitiated)
        {
            if (DataContext is CampathsDockViewModel vm)
            {
                await vm.PlayCampathAsync(_campathPressedItem);
            }
        }

        ResetCampathPointerState(sender as Control);
    }

    private void OnCampathPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetCampathPointerState(sender as Control);
    }

    private void ResetCampathPointerState(Control? control = null)
    {
        _campathPressedItem = null;
        _campathPressPoint = null;
        _campathDragInitiated = false;
        if (_campathPointer != null)
        {
            _campathPointer.Capture(null);
            _campathPointer = null;
        }
    }

    private void OnCampathDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(CampathDragFormat) && sender is Control { DataContext: CampathItemViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnCampathDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathsDockViewModel vm)
            return;

        if (!TryResolveCampath(e.DataTransfer, vm, out var dragged) || dragged == null)
            return;

        var target = (sender as Control)?.DataContext as CampathItemViewModel;
        if (target == null || ReferenceEquals(dragged, target))
            return;

        vm.MoveCampath(dragged, target);
        e.Handled = true;
    }

    private void OnGroupDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(GroupDragFormat) && sender is Control { DataContext: CampathGroupViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.DataTransfer.Contains(CampathDragFormat) && sender is Control { DataContext: CampathGroupViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnGroupDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathsDockViewModel vm)
            return;

        TryResolveGroup(e.DataTransfer, vm, out var draggedGroup);
        TryResolveCampath(e.DataTransfer, vm, out var draggedCampath);
        var group = (sender as Control)?.DataContext as CampathGroupViewModel;
        if (group == null)
            return;

        if (draggedGroup != null)
        {
            if (!ReferenceEquals(draggedGroup, group))
            {
                vm.MoveGroup(draggedGroup, group);
                e.Handled = true;
            }
        }
        else if (draggedCampath != null)
        {
            vm.AddCampathToGroup(draggedCampath, group);
            e.Handled = true;
        }
    }

    private void OnGroupPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control control &&
            control.DataContext is CampathGroupViewModel groupVm)
        {
            _groupPressed = groupVm;
            _groupPressPoint = e.GetPosition(this);
            _groupDragInitiated = false;
            _groupPointer = e.Pointer;
            _groupPointer.Capture(control);
            e.Handled = true;
        }
    }

    private async void OnGroupPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_groupPressed == null || _groupDragInitiated || !_groupPressPoint.HasValue)
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            ResetGroupPointerState(sender as Control);
            return;
        }

        var current = e.GetPosition(this);
        var delta = current - _groupPressPoint.Value;
        var distance = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);

        if (distance < CampathDragThreshold)
            return;

        if (sender is Control control && control.DataContext is CampathGroupViewModel groupVm && ReferenceEquals(groupVm, _groupPressed))
        {
            _groupDragInitiated = true;
            var data = new DataTransfer();
            var item = new DataTransferItem();
            item.Set(GroupDragFormat, groupVm.Id.ToString("D"));
            data.Add(item);
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
            ResetGroupPointerState(control);
        }
    }

    private static bool TryResolveCampath(IDataTransfer dataTransfer, CampathsDockViewModel vm, out CampathItemViewModel? campath)
    {
        campath = null;

        var idText = dataTransfer.TryGetValue(CampathDragFormat);
        if (string.IsNullOrWhiteSpace(idText) || !Guid.TryParse(idText, out var id))
            return false;

        campath = vm.SelectedProfile?.Campaths.FirstOrDefault(c => c.Id == id);
        return campath != null;
    }

    private static bool TryResolveGroup(IDataTransfer dataTransfer, CampathsDockViewModel vm, out CampathGroupViewModel? group)
    {
        group = null;

        var idText = dataTransfer.TryGetValue(GroupDragFormat);
        if (string.IsNullOrWhiteSpace(idText) || !Guid.TryParse(idText, out var id))
            return false;

        group = vm.SelectedProfile?.Groups.FirstOrDefault(g => g.Id == id);
        return group != null;
    }

    private async void OnGroupPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && _groupPressed != null && !_groupDragInitiated)
        {
            if (DataContext is CampathsDockViewModel vm)
            {
                await vm.PlayCampathGroupAsync(_groupPressed);
            }
        }

        ResetGroupPointerState(sender as Control);
    }

    private void OnGroupPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        ResetGroupPointerState(sender as Control);
    }

    private void ResetGroupPointerState(Control? control = null)
    {
        _groupPressed = null;
        _groupPressPoint = null;
        _groupDragInitiated = false;
        if (_groupPointer != null)
        {
            _groupPointer.Capture(null);
            _groupPointer = null;
        }
    }

    private void InitializePopulateFlyout()
    {
        _populateProfileButton = this.FindControl<Button>("PopulateProfileButton");
        if (_populateProfileButton == null)
            return;

        _populateSourceFlyout = FlyoutBase.GetAttachedFlyout(_populateProfileButton);
        if (_populateSourceFlyout != null)
        {
            _populateSourceFlyout.Closed += OnPopulateFlyoutClosed;
        }
    }
}
