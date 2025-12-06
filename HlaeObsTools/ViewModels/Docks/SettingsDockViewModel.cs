using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Settings dock for configuring UI options like radar markers.
/// </summary>
public class SettingsDockViewModel : Tool
{
    private readonly RadarSettings _settings;

    public SettingsDockViewModel(RadarSettings settings)
    {
        _settings = settings;
        Title = "Settings";
        CanClose = false;
        CanFloat = true;
        CanPin = true;
    }

    public double MarkerScale
    {
        get => _settings.MarkerScale;
        set
        {
            if (value < 0.3) value = 0.3;
            if (value > 3.0) value = 3.0;
            if (_settings.MarkerScale != value)
            {
                _settings.MarkerScale = value;
                OnPropertyChanged();
            }
        }
    }
}
