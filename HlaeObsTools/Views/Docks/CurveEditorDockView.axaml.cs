using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using HlaeObsTools.Controls;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CurveEditorDockView : UserControl
{
    public CurveEditorDockView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel, true);
        CurveCanvas.SelectionChanged += UpdateWeightedButton;
        CurveCanvas.HistoryEditStarted += OnHistoryEditStarted;
        CurveCanvas.HistoryEditCompleted += OnHistoryEditCompleted;
        CurveCanvas.PlayheadDragCompleted += OnPlayheadDragCompleted;
        UpdateWeightedButton();
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
