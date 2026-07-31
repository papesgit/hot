using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathGroupViewWindow : Window
{
    private static readonly DataFormat<string> CampathDragFormat =
        DataFormat.CreateStringApplicationFormat("hlaeobs.group-window-campath-id");
    private const double DragThreshold = 4.0;
    private ScrollViewer? _cardScrollViewer;
    private CampathItemViewModel? _pressedItem;
    private Point? _pressPoint;
    private PointerPressedEventArgs? _pressEvent;
    private IPointer? _pointer;
    private bool _dragInitiated;

    public CampathGroupViewWindow()
    {
        InitializeComponent();
        InitializeInteraction();
    }

    public CampathGroupViewWindow(CampathsDockViewModel ownerVm, CampathGroupViewModel group)
    {
        InitializeComponent();
        InitializeInteraction();
        DataContext = new CampathGroupViewWindowVm(ownerVm, group);
    }

    private void InitializeInteraction()
    {
        _cardScrollViewer = this.FindControl<ScrollViewer>("CardScrollViewer");
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Tunnel, true);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not CampathGroupViewWindowVm vm || e.Delta.Y == 0 || !IsPointerWithin(e, _cardScrollViewer))
            return;

        vm.AdjustCardScale(Math.Sign(e.Delta.Y) * 0.1);
        e.Handled = true;
    }

    private static bool IsPointerWithin(PointerEventArgs e, Control? control)
    {
        if (control == null)
            return false;

        var point = e.GetPosition(control);
        return point.X >= 0 && point.Y >= 0 && point.X <= control.Bounds.Width && point.Y <= control.Bounds.Height;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || sender is not Control control || control.DataContext is not CampathItemViewModel item)
            return;

        _pressedItem = item;
        _pressPoint = e.GetPosition(this);
        _pressEvent = e;
        _pointer = e.Pointer;
        _pointer.Capture(control);
        _dragInitiated = false;
        e.Handled = true;
    }

    private async void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedItem == null || _dragInitiated || !_pressPoint.HasValue || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(this) - _pressPoint.Value;
        if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) < DragThreshold || sender is not Control control || !ReferenceEquals(control.DataContext, _pressedItem))
            return;

        _dragInitiated = true;
        var transfer = new DataTransfer();
        var transferItem = new DataTransferItem();
        transferItem.Set(CampathDragFormat, _pressedItem.Id.ToString("D"));
        transfer.Add(transferItem);
        if (_pressEvent != null)
            await DragDrop.DoDragDropAsync(_pressEvent, transfer, DragDropEffects.Move);
        ResetPointerState();
    }

    private void OnCardPointerReleased(object? sender, PointerReleasedEventArgs e) => ResetPointerState();
    private void OnCardPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ResetPointerState();

    private void OnCardDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(CampathDragFormat) && sender is Control { DataContext: CampathItemViewModel })
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void OnCardDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not CampathGroupViewWindowVm vm || sender is not Control { DataContext: CampathItemViewModel target })
            return;

        var id = e.DataTransfer.TryGetValue(CampathDragFormat);
        if (Guid.TryParse(id, out var draggedId))
            vm.MoveCampath(draggedId, target.Id);
        e.Handled = true;
    }

    private void ResetPointerState()
    {
        _pressedItem = null;
        _pressPoint = null;
        _pressEvent = null;
        _dragInitiated = false;
        _pointer?.Capture(null);
        _pointer = null;
    }
}

public class CampathGroupViewWindowVm : ViewModelBase
{
    private readonly CampathsDockViewModel _ownerVm;
    private readonly CampathGroupViewModel _groupVm;
    private double _cardScale;

    public ObservableCollection<CampathItemViewModel> CampathItems { get; }
    public string GroupName => _groupVm.Name;
    public string ModeText => _groupVm.Mode.ToString();
    public ICommand RemoveCampathFromGroupCommand { get; }

    public double CardScale
    {
        get => _cardScale;
        private set => SetProperty(ref _cardScale, Math.Clamp(value, 0.4, 2.0));
    }

    public bool HideInRadar
    {
        get => _groupVm.HideInRadar;
        set
        {
            if (_groupVm.HideInRadar == value)
                return;

            _groupVm.HideInRadar = value;
            _ownerVm.Save();
            OnPropertyChanged();
        }
    }

    public CampathGroupViewWindowVm(CampathsDockViewModel ownerVm, CampathGroupViewModel groupVm)
    {
        _ownerVm = ownerVm;
        _groupVm = groupVm;
        _cardScale = ownerVm.CampathScale;
        CampathItems = new ObservableCollection<CampathItemViewModel>(
            _groupVm.CampathIds
                .Select(id => _ownerVm.SelectedProfile?.Campaths.FirstOrDefault(item => item.Id == id))
                .OfType<CampathItemViewModel>());
        RemoveCampathFromGroupCommand = new DelegateCommand(param => { Remove(param as CampathItemViewModel); return Task.CompletedTask; });
    }

    public void AdjustCardScale(double delta) => CardScale += delta;

    public void MoveCampath(Guid draggedId, Guid targetId)
    {
        var dragged = CampathItems.FirstOrDefault(item => item.Id == draggedId);
        var target = CampathItems.FirstOrDefault(item => item.Id == targetId);
        if (dragged == null || target == null || ReferenceEquals(dragged, target))
            return;

        var oldIndex = CampathItems.IndexOf(dragged);
        var newIndex = CampathItems.IndexOf(target);
        CampathItems.Move(oldIndex, newIndex);
        _groupVm.MoveCampath(oldIndex, newIndex);
        _ownerVm.Save();
    }

    private void Remove(CampathItemViewModel? item)
    {
        if (item == null)
            return;

        CampathItems.Remove(item);
        _groupVm.RemoveCampath(item.Id);
        _ownerVm.Save();
    }
}
