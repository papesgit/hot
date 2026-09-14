using System;
using System.Collections.ObjectModel;

namespace HlaeObsTools.ViewModels;

/// <summary>Shared replay-marking policy, plus the vMix-only event placement settings.</summary>
public sealed class ReplaySettings : ViewModelBase
{
    public static ObservableCollection<string> BackendOptions { get; } = new() { "Disabled", "vMix Instant Replay", "OBS Replays" };
    private bool _autoMarkEnabled;
    private string _backend = "Disabled";
    private double _preSeconds = 2.0, _postSeconds = 2.0, _extendWindowSeconds = 3.0, _framesPerSecond = 60.0;
    private string _channel = "A";
    private int _camera = 1;

    /// <summary>Enables automatic locally caught-kill markers for the selected replay engine.</summary>
    public bool AutoMarkEnabled { get => _autoMarkEnabled; set => SetProperty(ref _autoMarkEnabled, value); }
    public string Backend
    {
        get => _backend;
        set
        {
            if (!SetProperty(ref _backend, BackendOptions.Contains(value) ? value : "Disabled")) return;
            OnPropertyChanged(nameof(IsObsReplaysSelected));
            OnPropertyChanged(nameof(IsVmixSelected));
        }
    }
    public bool IsObsReplaysSelected => string.Equals(Backend, "OBS Replays", StringComparison.Ordinal);
    public bool IsVmixSelected => string.Equals(Backend, "vMix Instant Replay", StringComparison.Ordinal);
    public double PreSeconds { get => _preSeconds; set => SetProperty(ref _preSeconds, Math.Max(0, value)); }
    public double PostSeconds
    {
        get => _postSeconds;
        set
        {
            if (!SetProperty(ref _postSeconds, Math.Max(0, value))) return;
            if (_extendWindowSeconds < _postSeconds)
                SetProperty(ref _extendWindowSeconds, _postSeconds, nameof(ExtendWindowSeconds));
        }
    }

    public double ExtendWindowSeconds
    {
        get => _extendWindowSeconds;
        set => SetProperty(ref _extendWindowSeconds, Math.Max(_postSeconds, Math.Max(0, value)));
    }
    public double FramesPerSecond { get => _framesPerSecond; set => SetProperty(ref _framesPerSecond, Math.Clamp(value, 1.0, 240.0)); }
    public int Camera { get => _camera; set => SetProperty(ref _camera, Math.Clamp(value, 1, 8)); }
    public string Channel { get => _channel; set => SetProperty(ref _channel, (value ?? string.Empty).Trim()); }
}
