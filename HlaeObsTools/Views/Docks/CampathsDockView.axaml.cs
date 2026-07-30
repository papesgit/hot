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
using Avalonia.VisualTree;
using HlaeObsTools.Services.Input;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Views;

namespace HlaeObsTools.Views.Docks;

public partial class CampathsDockView : UserControl
{
    private CampathsDockViewModel? _viewModel;
    private bool _viewModelAttached;

    public CampathsDockView()
    {
        InitializeComponent();
        _groupScrollViewer = this.FindControl<ScrollViewer>("GroupScrollViewer");
        _campathScrollViewer = this.FindControl<ScrollViewer>("CampathScrollViewer");
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel, true);
        InitializePopulateFlyout();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += (_, _) => AttachViewModel();
        DetachedFromVisualTree += (_, _) => DetachViewModel();
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
    private PointerPressedEventArgs? _campathPressEvent;
    private CampathGroupViewModel? _groupPressed;
    private Point? _groupPressPoint;
    private bool _groupDragInitiated;
    private IPointer? _groupPointer;
    private PointerPressedEventArgs? _groupPressEvent;
    private Button? _populateProfileButton;
    private FlyoutBase? _populateSourceFlyout;
    private TaskCompletionSource<CampathPopulateSource?>? _populateSourceTcs;
    private readonly ScrollViewer? _groupScrollViewer;
    private readonly ScrollViewer? _campathScrollViewer;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as CampathsDockViewModel;
        if (this.IsAttachedToVisualTree())
            AttachViewModel();
    }

    private void AttachViewModel()
    {
        if (_viewModel == null || _viewModelAttached)
            return;

        _viewModel.PromptAsync = PromptAsync;
        _viewModel.ConfirmAsync = ConfirmAsync;
        _viewModel.SelectPopulateSourceAsync = SelectPopulateSourceAsync;
        _viewModel.BrowseFileAsync = BrowseFileAsync;
        _viewModel.BrowseFilesAsync = BrowseFilesAsync;
        _viewModel.BrowseFolderAsync = BrowseFolderAsync;
        _viewModel.ViewGroupRequested += OnViewGroupRequested;
        _viewModelAttached = true;
    }

    private void DetachViewModel()
    {
        if (_viewModel == null || !_viewModelAttached)
            return;

        _viewModel.ViewGroupRequested -= OnViewGroupRequested;
        if (_viewModel.PromptAsync == PromptAsync)
            _viewModel.PromptAsync = NoPromptAsync;
        if (_viewModel.ConfirmAsync == ConfirmAsync)
            _viewModel.ConfirmAsync = NoConfirmAsync;
        if (_viewModel.SelectPopulateSourceAsync == SelectPopulateSourceAsync)
            _viewModel.SelectPopulateSourceAsync = NoPopulateSourceAsync;
        if (_viewModel.BrowseFileAsync == BrowseFileAsync)
            _viewModel.BrowseFileAsync = NoBrowseFileAsync;
        if (_viewModel.BrowseFilesAsync == BrowseFilesAsync)
            _viewModel.BrowseFilesAsync = NoBrowseFilesAsync;
        if (_viewModel.BrowseFolderAsync == BrowseFolderAsync)
            _viewModel.BrowseFolderAsync = NoBrowseFileAsync;
        _viewModelAttached = false;
    }

    private static Task<string?> NoPromptAsync(string _, string __, int ___, int ____) =>
        Task.FromResult<string?>(null);
    private static Task<bool> NoConfirmAsync(string _, string __) => Task.FromResult(false);
    private static Task<CampathPopulateSource?> NoPopulateSourceAsync() =>
        Task.FromResult<CampathPopulateSource?>(null);
    private static Task<string?> NoBrowseFileAsync(string _) => Task.FromResult<string?>(null);
    private static Task<IEnumerable<string>?> NoBrowseFilesAsync(string _) =>
        Task.FromResult<IEnumerable<string>?>(null);

    private Task<string?> PromptAsync(string title, string message, int width, int height)
    {
        return DialogHelpers.PromptAsync(this, title, message, string.Empty, width, height);
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        return await DialogHelpers.ConfirmAsync(this, title, message);
    }

    private async Task<string?> BrowseFileAsync(string title)
    {
        var host = TopLevel.GetTopLevel(this) as Window;
        if (host == null)
            return null;

        var storageProvider = host.StorageProvider;
        if (storageProvider == null)
            return null;

        var result = await KeyboardInputGate.RunSuppressedAsync(() =>
            storageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                }));

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

        var result = await KeyboardInputGate.RunSuppressedAsync(() =>
            storageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = false
                }));

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

        var result = await KeyboardInputGate.RunSuppressedAsync(() =>
            storageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = title,
                    AllowMultiple = true
                }));

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
            _campathPressEvent = e;
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
            if (_campathPressEvent != null)
            {
                await DragDrop.DoDragDropAsync(_campathPressEvent, data, DragDropEffects.Move);
            }
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

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not CampathsDockViewModel vm || e.Delta.Y == 0)
            return;

        if (IsPointerWithin(e, _groupScrollViewer))
            vm.AdjustGroupScale(Math.Sign(e.Delta.Y) * 0.1);
        else if (IsPointerWithin(e, _campathScrollViewer))
            vm.AdjustCampathScale(Math.Sign(e.Delta.Y) * 0.1);
        else
            return;

        e.Handled = true;
    }

    private static bool IsPointerWithin(PointerEventArgs e, Control? control)
    {
        if (control == null)
            return false;

        var point = e.GetPosition(control);
        return point.X >= 0 && point.Y >= 0 && point.X <= control.Bounds.Width && point.Y <= control.Bounds.Height;
    }

    private void ResetCampathPointerState(Control? control = null)
    {
        _campathPressedItem = null;
        _campathPressPoint = null;
        _campathDragInitiated = false;
        _campathPressEvent = null;
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
            _groupPressEvent = e;
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
            if (_groupPressEvent != null)
            {
                await DragDrop.DoDragDropAsync(_groupPressEvent, data, DragDropEffects.Move);
            }
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
        _groupPressEvent = null;
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
