using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HlaeObsTools.Views;

public partial class AboutWindow : Window
{
    private const string ProjectUrl = "https://github.com/papesgit/hot";

    public AboutWindow()
        : this("unknown")
    {
    }

    public AboutWindow(string version)
    {
        InitializeComponent();
        VersionText.Text = $"Version {version}";
    }

    private void OpenProject(object? sender, RoutedEventArgs e)
    {
        OpenExternalLink(ProjectUrl);
    }

    private async void OpenExternalLink(string url)
    {
        if (!ExternalLinkLauncher.TryOpen(url))
        {
            await DialogHelpers.MessageAsync(
                this,
                "Unable to open link",
                $"Open this address in your browser:\n{url}");
        }
    }

    private void CloseDialog(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
