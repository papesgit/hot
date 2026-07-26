using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HlaeObsTools.Services.Campaths;
using HlaeObsTools.Services.Input;
using HlaeObsTools.ViewModels;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class CampathSequencerDockView : UserControl
{
    private TopLevel? _playbackKeyHost;

    public CampathSequencerDockView()
    {
        InitializeComponent();
        SequenceTimeline.SaveCameraRequested += OnSaveCameraRequested;
        AddHandler(KeyDownEvent, OnUndoRedoKeyDown, RoutingStrategies.Tunnel, true);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private async void OnSaveCameraRequested(CampathCameraTrackViewModel camera)
    {
        var host = TopLevel.GetTopLevel(this);
        if (host?.StorageProvider == null)
            return;

        var result = await KeyboardInputGate.RunSuppressedAsync(() =>
            host.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Save {camera.Name} Campath"
            }));
        if (result != null)
            CampathFileIo.Save(result.Path.LocalPath, camera.Editor);
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
            || DataContext is not CampathSequencerDockViewModel vm)
            return;

        vm.Sequence.TogglePlayback();
        e.Handled = true;
    }

    private void OnUndoRedoKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CampathSequencerDockViewModel vm
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            return;
        if (e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                vm.Sequence.Redo();
            else
                vm.Sequence.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            vm.Sequence.Redo();
            e.Handled = true;
        }
    }
}
