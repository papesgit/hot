using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Dock.Model.Mvvm.Controls;

namespace HlaeObsTools.ViewModels.Docks;

public sealed class CampathSequencerDockViewModel : Tool, IDisposable
{
    private double _viewStart;
    private double _secondsPerPixel = 0.02;

    public CampathSequencerDockViewModel(CampathSequenceViewModel sequence)
    {
        Sequence = sequence;
        Id = "CampathSequencer";
        Title = "Sequencer";
        CanFloat = true;
        CanPin = true;
        CanClose = true;
        SelectedCamera = Sequence.Cameras.FirstOrDefault();
        Sequence.PropertyChanged += OnSequenceChanged;
        AddCameraCommand = new DelegateCommand(_ =>
        {
            SelectedCamera = Sequence.AddCamera();
            return Task.CompletedTask;
        });
        TogglePlaybackCommand = new DelegateCommand(_ =>
        {
            Sequence.TogglePlayback();
            return Task.CompletedTask;
        });
        UndoCommand = new DelegateCommand(_ =>
        {
            Sequence.Undo();
            return Task.CompletedTask;
        });
        RedoCommand = new DelegateCommand(_ =>
        {
            Sequence.Redo();
            return Task.CompletedTask;
        });
        PossessCutsCommand = new DelegateCommand(_ =>
        {
            Sequence.PossessCameraCuts();
            return Task.CompletedTask;
        });
    }

    public CampathSequenceViewModel Sequence { get; }
    public ICommand AddCameraCommand { get; }
    public ICommand TogglePlaybackCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand PossessCutsCommand { get; }

    public CampathCameraTrackViewModel? SelectedCamera
    {
        get => Sequence.SelectedCamera;
        set
        {
            if (Sequence.SelectedCamera == value)
                return;
            Sequence.SelectedCamera = value;
            OnPropertyChanged();
        }
    }

    public double ViewStart
    {
        get => _viewStart;
        set => SetProperty(ref _viewStart, Math.Max(0.0, value));
    }

    public double SecondsPerPixel
    {
        get => _secondsPerPixel;
        set => SetProperty(ref _secondsPerPixel, Math.Clamp(value, 0.0001, 10.0));
    }

    public string PlaybackLabel => Sequence.IsPlaying ? "Pause" : "Play";
    public bool ShowPlayIcon => !Sequence.IsPlaying;
    public bool ShowPauseIcon => Sequence.IsPlaying;

    public void PossessCamera(CampathCameraTrackViewModel camera)
    {
        SelectedCamera = camera;
        Sequence.PossessCamera(camera.Id);
        OnPropertyChanged(nameof(Sequence));
    }

    private void OnSequenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CampathSequenceViewModel.IsPlaying))
        {
            OnPropertyChanged(nameof(PlaybackLabel));
            OnPropertyChanged(nameof(ShowPlayIcon));
            OnPropertyChanged(nameof(ShowPauseIcon));
        }
        if (e.PropertyName is nameof(CampathSequenceViewModel.Possession)
            or nameof(CampathSequenceViewModel.PlayheadTime))
            OnPropertyChanged(nameof(Sequence));
        if (e.PropertyName == nameof(CampathSequenceViewModel.SelectedCamera))
            OnPropertyChanged(nameof(SelectedCamera));
    }

    public void Dispose()
    {
        Sequence.PropertyChanged -= OnSequenceChanged;
    }
}
