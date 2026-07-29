using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using HlaeObsTools.Controls;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CurveEditorDockView : UserControl
{
    private TopLevel? _playbackKeyHost;

    public CurveEditorDockView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel, true);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        CurveCanvas.SelectionChanged += UpdateWeightedButton;
        CurveCanvas.HistoryEditStarted += OnHistoryEditStarted;
        CurveCanvas.HistoryEditCompleted += OnHistoryEditCompleted;
        CurveCanvas.PlayheadDragCompleted += OnPlayheadDragCompleted;
        UpdateWeightedButton();
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachPlaybackKeyHost();
        _playbackKeyHost = TopLevel.GetTopLevel(this);
        _playbackKeyHost?.AddHandler(
            KeyUpEvent, OnPlaybackKeyUp, RoutingStrategies.Tunnel, true);
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e) =>
        DetachPlaybackKeyHost();

    private void DetachPlaybackKeyHost()
    {
        _playbackKeyHost?.RemoveHandler(KeyUpEvent, OnPlaybackKeyUp);
        _playbackKeyHost = null;
    }

    private void OnPlaybackKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !IsPointerOver
            || e.Source is TextBox or TextPresenter
            || DataContext is not CurveEditorDockViewModel vm)
            return;

        vm.TogglePlayback();
        e.Handled = true;
    }

    private void OnUndoRedoKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not CurveEditorDockViewModel vm)
            return;
        if (e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.Redo();
            else vm.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            vm.Redo();
            e.Handled = true;
        }
    }

    private void OnFitAll(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CurveEditorDockViewModel vm) vm.RequestFitAll();
    }

    private void OnFitSelection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is CurveEditorDockViewModel vm) vm.RequestFitSelection();
    }

    private void OnFlattenTangents(object? sender, RoutedEventArgs e) => CurveCanvas.FlattenSelectedTangents();

    private void OnStraightenTangents(object? sender, RoutedEventArgs e) => CurveCanvas.StraightenSelectedTangents();

    private void OnToggleWeightedTangents(object? sender, RoutedEventArgs e)
    {
        CurveCanvas.ToggleSelectedWeightedTangents();
        UpdateWeightedButton();
    }

    private void UpdateWeightedButton()
    {
        WeightedButton.Background = CurveCanvas.GetWeightSelectionState() switch
        {
            CurveWeightSelectionState.Weighted => new SolidColorBrush(Color.Parse("#7C5CB8")),
            CurveWeightSelectionState.Mixed => new SolidColorBrush(Color.Parse("#75435F")),
            _ => new SolidColorBrush(Color.Parse("#33363D"))
        };
    }

    private void OnChannelPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CampathCurveChannel channel }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CurveCanvas.SelectKeys(channel.Keys, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void OnChannelVisibilityPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CampathCurveChannel channel }
            && DataContext is CurveEditorDockViewModel vm
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) vm.SoloChannel(channel);
            else channel.IsVisible = !channel.IsVisible;
            e.Handled = true;
        }
    }

    private void OnChannelGroupVisibilityPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CurveChannelGroupViewModel group }
            && DataContext is CurveEditorDockViewModel vm
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) vm.SoloChannelGroup(group);
            else group.IsVisible = !group.IsVisible;
            e.Handled = true;
        }
    }

    private void OnChannelGroupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CurveChannelGroupViewModel group }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CurveCanvas.SelectKeys(group.Channels.SelectMany(channel => channel.Keys), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        }
    }

    private void OnAddChannelKeyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CampathCurveChannel channel }
            && DataContext is CurveEditorDockViewModel vm)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            {
                vm.AddKey(channel, useEvaluatedValue: point.Properties.IsRightButtonPressed);
                e.Handled = true;
            }
        }
    }

    private void OnAddGroupKeyPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: CurveChannelGroupViewModel group }
            && DataContext is CurveEditorDockViewModel vm)
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            {
                vm.AddKeys(group.Channels, useEvaluatedValue: point.Properties.IsRightButtonPressed);
                e.Handled = true;
            }
        }
    }

    private void OnHistoryEditStarted()
    {
        if (DataContext is CurveEditorDockViewModel vm) vm.CampathEditor.BeginHistoryTransaction();
    }

    private void OnHistoryEditCompleted()
    {
        if (DataContext is CurveEditorDockViewModel vm) vm.CampathEditor.CommitHistoryTransaction();
    }

    private void OnPlayheadDragCompleted()
    {
        if (DataContext is CurveEditorDockViewModel vm)
            vm.CommitPlayheadScrub();
    }

}
