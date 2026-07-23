using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using HlaeObsTools.Controls;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Viewport;

public partial class CampathSequencerView : UserControl
{
    public CampathSequencerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel, true);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnUndoRedoKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || DataContext is not Viewport3DDockViewModel vm)
            return;
        if (e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) vm.CampathEditor.Redo();
            else vm.CampathEditor.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            vm.CampathEditor.Redo();
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        var timeline = this.FindControl<CampathTimelineControl>("Timeline");
        if (timeline == null)
            return;

        timeline.FreecamPreviewRequested -= OnFreecamPreviewRequested;
        timeline.FreecamPreviewEnded -= OnFreecamPreviewEnded;
        timeline.CampathPreviewRequested -= OnCampathPreviewRequested;
        timeline.CampathPreviewEnded -= OnCampathPreviewEnded;
        timeline.KeyframeDragStarted -= OnKeyframeDragStarted;
        timeline.KeyframeDragEnded -= OnKeyframeDragEnded;
        timeline.CurveDocumentEdited -= OnCurveDocumentEdited;
        timeline.HistoryEditStarted -= OnHistoryEditStarted;
        timeline.HistoryEditCompleted -= OnHistoryEditCompleted;
        timeline.PlayheadDragEnded -= OnPlayheadDragEnded;
        timeline.FreecamPreviewRequested += OnFreecamPreviewRequested;
        timeline.FreecamPreviewEnded += OnFreecamPreviewEnded;
        timeline.CampathPreviewRequested += OnCampathPreviewRequested;
        timeline.CampathPreviewEnded += OnCampathPreviewEnded;
        timeline.KeyframeDragStarted += OnKeyframeDragStarted;
        timeline.KeyframeDragEnded += OnKeyframeDragEnded;
        timeline.CurveDocumentEdited += OnCurveDocumentEdited;
        timeline.HistoryEditStarted += OnHistoryEditStarted;
        timeline.HistoryEditCompleted += OnHistoryEditCompleted;
        timeline.PlayheadDragEnded += OnPlayheadDragEnded;
    }

    private void OnFreecamPreviewRequested(double time)
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.ApplyFreecamPreviewAtTime(time);
    }

    private void OnFreecamPreviewEnded()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.EndFreecamPreview();
    }

    private void OnCampathPreviewRequested()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.BeginCampathPreviewOverride();
    }

    private void OnCampathPreviewEnded()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.EndCampathPreviewOverride();
    }

    private void OnKeyframeDragStarted()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.CampathEditor.BeginHistoryTransaction();
        vm.CampathEditor.BeginTimeDrag();
    }

    private void OnKeyframeDragEnded()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.CampathEditor.EndTimeDrag();
        vm.CampathEditor.CommitHistoryTransaction();
    }

    private void OnCurveDocumentEdited()
    {
        if (DataContext is Viewport3DDockViewModel vm)
            vm.CampathEditor.NotifyCurveDocumentChanged();
    }

    private void OnHistoryEditStarted()
    {
        if (DataContext is Viewport3DDockViewModel vm)
            vm.CampathEditor.BeginHistoryTransaction();
    }

    private void OnHistoryEditCompleted()
    {
        if (DataContext is Viewport3DDockViewModel vm)
            vm.CampathEditor.CommitHistoryTransaction();
    }

    private void OnPlayheadDragEnded()
    {
        if (DataContext is not Viewport3DDockViewModel vm)
            return;

        vm.NotifyPlayheadDragEnded();
    }

    private void OnDofToggleClicked(object? sender, RoutedEventArgs e)
    {
        DofPopup.IsOpen = !DofPopup.IsOpen;
        if (DataContext is Viewport3DDockViewModel vm)
            vm.CampathEditor.IsDofEditorOpen = DofPopup.IsOpen;
    }
}
