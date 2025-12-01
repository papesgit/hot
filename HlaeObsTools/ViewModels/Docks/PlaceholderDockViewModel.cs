using Dock.Model.Mvvm.Controls;

namespace HlaeObsTools.ViewModels.Docks;

/// <summary>
/// Placeholder dock view model for future features
/// </summary>
public class PlaceholderDockViewModel : Tool
{
    public PlaceholderDockViewModel()
    {
        CanClose = false;
        CanFloat = true;
        CanPin = true;
    }
}
