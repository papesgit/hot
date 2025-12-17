using System.ComponentModel;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared radar settings for marker customization.
/// </summary>
public sealed class RadarSettings : ViewModelBase
{
    private double _markerScale = 1.0;
    private bool _useAltPlayerBinds;

    /// <summary>
    /// Scale factor for player markers on the radar.
    /// </summary>
    public double MarkerScale
    {
        get => _markerScale;
        set => SetProperty(ref _markerScale, value);
    }

    /// <summary>
    /// Whether to use alternative player bind labels for slots 6-0.
    /// </summary>
    public bool UseAltPlayerBinds
    {
        get => _useAltPlayerBinds;
        set => SetProperty(ref _useAltPlayerBinds, value);
    }
}
