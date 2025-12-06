using Dock.Model.Mvvm.Controls;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Simple browser dock using Avalonia WebView for rendering local overlays.
/// </summary>
public class BrowserSourceDockViewModel : Tool
{
    public BrowserSourceDockViewModel()
    {
        Title = "Browser Source";
        CanClose = false;
        CanFloat = true;
        CanPin = true;
    }

    /// <summary>
    /// URL loaded by the embedded browser.
    /// </summary>
    public string Address { get; } = "http://127.0.0.1:36364";
}
