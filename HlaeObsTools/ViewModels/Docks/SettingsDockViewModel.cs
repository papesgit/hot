using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Settings dock for configuring UI options like radar markers.
/// </summary>
public class SettingsDockViewModel : Tool
{
    private readonly RadarSettings _radarSettings;
    private readonly BrowserSourcesSettings _browserSettings;

    public SettingsDockViewModel(RadarSettings radarSettings, BrowserSourcesSettings browserSettings)
    {
        _radarSettings = radarSettings;
        _browserSettings = browserSettings;
        Title = "Settings";
        CanClose = false;
        CanFloat = true;
        CanPin = true;
    }

    public double MarkerScale
    {
        get => _radarSettings.MarkerScale;
        set
        {
            if (value < 0.3) value = 0.3;
            if (value > 3.0) value = 3.0;
            if (_radarSettings.MarkerScale != value)
            {
                _radarSettings.MarkerScale = value;
                OnPropertyChanged();
            }
        }
    }

    public string HudUrl
    {
        get => _browserSettings.HudUrl;
        set
        {
            if (_browserSettings.HudUrl != value)
            {
                _browserSettings.HudUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public string BrowserSourceUrl
    {
        get => _browserSettings.BrowserSourceUrl;
        set
        {
            if (_browserSettings.BrowserSourceUrl != value)
            {
                _browserSettings.BrowserSourceUrl = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsHudEnabled
    {
        get => _browserSettings.IsHudEnabled;
        set
        {
            if (_browserSettings.IsHudEnabled != value)
            {
                _browserSettings.IsHudEnabled = value;
                OnPropertyChanged();
            }
        }
    }
}
