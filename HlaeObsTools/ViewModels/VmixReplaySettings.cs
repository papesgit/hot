using System;

namespace HlaeObsTools.ViewModels;

public sealed class VmixReplaySettings : ViewModelBase
{
    private bool _enabled;
    private double _preSeconds = 2.0;
    private double _postSeconds = 2.0;
    private double _extendWindowSeconds = 3.0;
    private string _channel = "A";
    private int _camera = 1;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    /// <summary>
    /// Seconds before the first kill to include.
    /// </summary>
    public double PreSeconds
    {
        get => _preSeconds;
        set => SetProperty(ref _preSeconds, Math.Max(0, value));
    }

    /// <summary>
    /// Seconds after the last kill to include.
    /// </summary>
    public double PostSeconds
    {
        get => _postSeconds;
        set => SetProperty(ref _postSeconds, Math.Max(0, value));
    }

    /// <summary>
    /// If another kill happens within this window (seconds) we extend the same replay.
    /// </summary>
    public double ExtendWindowSeconds
    {
        get => _extendWindowSeconds;
        set => SetProperty(ref _extendWindowSeconds, Math.Max(0, value));
    }

    /// <summary>
    /// vMix replay camera to assign to automatically created main replay events.
    /// </summary>
    public int Camera
    {
        get => _camera;
        set => SetProperty(ref _camera, Math.Clamp(value, 1, 8));
    }

    /// <summary>
    /// vMix replay channel to use for automatically created main replay events (A, B, or AB).
    /// </summary>
    public string Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, (value ?? string.Empty).Trim());
    }
}
