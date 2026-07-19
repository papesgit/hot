using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared radar settings for marker customization.
/// </summary>
public sealed class RadarSettings : ViewModelBase
{
    public static readonly IReadOnlyList<string> RadarStyleOptions = ["ingame", "simpleradar", "JTs"];

    public IReadOnlyList<string> RadarStyles => RadarStyleOptions;

    private double _radarScale = 1.0;
    private double _markerScale = 1.0;
    private double _heightScaleMultiplier = 1.0;
    private bool _useAltPlayerBinds;
    private bool _displayNumbersTopmost = true;
    private bool _showPlayerNames = true;
    private string _radarStyle = "ingame";

    /// <summary>
    /// The visual style used for the radar map images.
    /// </summary>
    public string RadarStyle
    {
        get => _radarStyle;
        set
        {
            var normalized = RadarStyleOptions.FirstOrDefault(style =>
                string.Equals(style, value, StringComparison.OrdinalIgnoreCase)) ?? "ingame";
            SetProperty(ref _radarStyle, normalized);
        }
    }

    /// <summary>
    /// Scale factor for the radar view.
    /// </summary>
    public double RadarScale
    {
        get => _radarScale;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 2.0);
            SetProperty(ref _radarScale, clamped);
        }
    }

    /// <summary>
    /// Scale factor for player markers on the radar.
    /// </summary>
    public double MarkerScale
    {
        get => _markerScale;
        set
        {
            var clamped = Math.Clamp(value, 0.3, 3.0);
            SetProperty(ref _markerScale, clamped);
        }
    }

    /// <summary>
    /// Multiplier for height-based scaling of player markers.
    /// </summary>
    public double HeightScaleMultiplier
    {
        get => _heightScaleMultiplier;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 2.0);
            SetProperty(ref _heightScaleMultiplier, clamped);
        }
    }

    /// <summary>
    /// Whether to use alternative player bind labels for slots 6-0.
    /// </summary>
    public bool UseAltPlayerBinds
    {
        get => _useAltPlayerBinds;
        set => SetProperty(ref _useAltPlayerBinds, value);
    }

    /// <summary>
    /// Whether player display numbers render above all markers.
    /// </summary>
    public bool DisplayNumbersTopmost
    {
        get => _displayNumbersTopmost;
        set => SetProperty(ref _displayNumbersTopmost, value);
    }

    /// <summary>
    /// Whether player names render under the markers.
    /// </summary>
    public bool ShowPlayerNames
    {
        get => _showPlayerNames;
        set => SetProperty(ref _showPlayerNames, value);
    }
}
