using System.ComponentModel;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Simple browser dock using Avalonia WebView for rendering local overlays.
/// </summary>
public class BrowserSourceDockViewModel : Tool
{
    private readonly BrowserSourcesSettings _settings;

    public BrowserSourceDockViewModel(BrowserSourcesSettings settings)
    {
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;

        Title = "Browser Source";
        CanClose = false;
        CanFloat = true;
        CanPin = true;
    }

    /// <summary>
    /// URL loaded by the embedded browser.
    /// </summary>
    public string Address => _settings.BrowserSourceUrl;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserSourcesSettings.BrowserSourceUrl))
        {
            OnPropertyChanged(nameof(Address));
        }
    }
}
