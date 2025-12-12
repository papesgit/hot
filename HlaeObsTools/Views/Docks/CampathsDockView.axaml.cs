using System;
using System.Linq;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Layout;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathsDockView : UserControl
{
    public CampathsDockView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private const string CampathDragFormat = "campath-item";
    private const string GroupDragFormat = "group-item";
    private const double CampathDragThreshold = 4.0;
    private CampathItemViewModel? _campathPressedItem;
    private Point? _campathPressPoint;
    private bool _campathDragInitiated;
    private IPointer? _campathPointer;
    private CampathGroupViewModel? _groupPressed;
    private Point? _groupPressPoint;
    private bool _groupDragInitiated;
    private IPointer? _groupPointer;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CampathsDockViewModel vm)
        {
            vm.PromptAsync = PromptAsync;
            vm.SelectPopulateSourceAsync = SelectPopulateSourceAsync;
            vm.BrowseFileAsync = BrowseFileAsync;
            vm.BrowseFilesAsync = BrowseFilesAsync;
            vm.BrowseFolderAsync = BrowseFolderAsync;
            vm.ViewGroupRequested += OnViewGroupRequested;
        }
    }

    private async Task<string?> PromptAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 180,
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

    private async Task<string?> BrowseFileAsync(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            AllowMultiple = false
        };
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;
        var result = await dlg.ShowAsync(host);
        return result?.FirstOrDefault();
    }

    private async Task<string?> BrowseFolderAsync(string title)
    {
        var dlg = new OpenFolderDialog
        {
            Title = title
        };
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;
        return await dlg.ShowAsync(host);
    }

    private async Task<IEnumerable<string>?> BrowseFilesAsync(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            AllowMultiple = true
        };
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;
        return await dlg.ShowAsync(host);
    }

    private async Task<CampathPopulateSource?> SelectPopulateSourceAsync()
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        var dialog = new Window
        {
            Title = "Populate campaths",
            Width = 360,
            Height = 100,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        CampathPopulateSource? choice = null;

        var infoText = new TextBlock { Text = "How do you want to add campaths?" };
        var folderButton = new Button { Content = "Select folder", Width = 120 };
        ToolTip.SetTip(folderButton, "Selects file types: .xml, .cam, .path, .campath, notype");
        ToolTip.SetShowDelay(folderButton, 100);
        var filesButton = new Button { Content = "Select files", Width = 120 };
        var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        folderButton.Click += (_, _) =>
        {
            choice = CampathPopulateSource.Folder;
            dialog.Close(true);
        };
        filesButton.Click += (_, _) =>
        {
            choice = CampathPopulateSource.Files;
            dialog.Close(true);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        buttons.Children.Add(folderButton);
        buttons.Children.Add(filesButton);
        buttons.Children.Add(cancelButton);

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(infoText);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        await dialog.ShowDialog<bool?>(host);
        return choice;
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
            var data = new DataObject();
            data.Set(CampathDragFormat, campathVm);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
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
        if (e.Data.Contains(CampathDragFormat) && sender is Control { DataContext: CampathItemViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnCampathDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathsDockViewModel vm)
            return;

        var dragged = e.Data.Get(CampathDragFormat) as CampathItemViewModel;
        if (dragged == null)
            return;

        var target = (sender as Control)?.DataContext as CampathItemViewModel;
        if (target == null || ReferenceEquals(dragged, target))
            return;

        vm.MoveCampath(dragged, target);
        e.Handled = true;
    }

    private void OnGroupDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(GroupDragFormat) && sender is Control { DataContext: CampathGroupViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.Contains(CampathDragFormat) && sender is Control { DataContext: CampathGroupViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnGroupDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathsDockViewModel vm)
            return;

        var draggedGroup = e.Data.Get(GroupDragFormat) as CampathGroupViewModel;
        var draggedCampath = e.Data.Get(CampathDragFormat) as CampathItemViewModel;
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
            var data = new DataObject();
            data.Set(GroupDragFormat, groupVm);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
            ResetGroupPointerState(control);
        }
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
}
