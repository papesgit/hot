using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared settings for browser-based overlays and HUD.
/// </summary>
public sealed class BrowserSourcesSettings : ViewModelBase
{
    public const string DefaultBrowserSourceUrl = "http://127.0.0.1:36364";

    private string _browserSourceUrl = DefaultBrowserSourceUrl;
    private bool _isHudEnabled;

    /// <summary>
    /// URL used for the Browser Source dock window.
    /// </summary>
    public string BrowserSourceUrl
    {
        get => _browserSourceUrl;
        set => SetProperty(ref _browserSourceUrl, value);
    }

    /// <summary>
    /// Whether the native HUD overlay in the video display is shown.
    /// </summary>
    public bool IsHudEnabled
    {
        get => _isHudEnabled;
        set => SetProperty(ref _isHudEnabled, value);
    }
}
