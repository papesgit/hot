using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

/// <summary>
/// Shared settings for browser-based overlays.
/// </summary>
public sealed class BrowserSourcesSettings : ViewModelBase
{
    public const string DefaultHudUrl = "http://localhost:1349/api/hud";
    public const string DefaultBrowserSourceUrl = "http://127.0.0.1:36364";

    private string _hudUrl = DefaultHudUrl;
    private string _browserSourceUrl = DefaultBrowserSourceUrl;
    private bool _isHudEnabled;
    private bool _useNativeHud;

    /// <summary>
    /// URL used for the HUD overlay in the video display.
    /// </summary>
    public string HudUrl
    {
        get => _hudUrl;
        set => SetProperty(ref _hudUrl, value);
    }

    /// <summary>
    /// URL used for the Browser Source dock window.
    /// </summary>
    public string BrowserSourceUrl
    {
        get => _browserSourceUrl;
        set => SetProperty(ref _browserSourceUrl, value);
    }

    /// <summary>
    /// Whether the HUD overlay in the video display is shown.
    /// </summary>
    public bool IsHudEnabled
    {
        get => _isHudEnabled;
        set => SetProperty(ref _isHudEnabled, value);
    }

    /// <summary>
    /// When true, use the built-in Avalonia HUD instead of the webview HUD.
    /// </summary>
    public bool UseNativeHud
    {
        get => _useNativeHud;
        set => SetProperty(ref _useNativeHud, value);
    }
}
