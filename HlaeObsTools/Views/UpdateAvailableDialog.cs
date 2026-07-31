using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HlaeObsTools.Views;

public enum UpdateAvailableDialogResult
{
    RemindLater,
    SkipVersion
}

public static class UpdateAvailableDialog
{
    public static async Task<UpdateAvailableDialogResult> ShowAsync(
        Window owner,
        Version currentVersion,
        Version latestVersion,
        string releasePageUrl)
    {
        var dialog = new Window
        {
            Title = "Update available",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var result = UpdateAvailableDialogResult.RemindLater;
        var downloadButton = new Button { Content = "View release", IsDefault = true };
        var remindLaterButton = new Button { Content = "Remind me later" };
        var skipButton = new Button { Content = "Skip this version", IsCancel = true };

        downloadButton.Click += (_, _) =>
        {
            ExternalLinkLauncher.TryOpen(releasePageUrl);
        };
        remindLaterButton.Click += (_, _) => dialog.Close();
        skipButton.Click += (_, _) =>
        {
            result = UpdateAvailableDialogResult.SkipVersion;
            dialog.Close();
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = $"HLAE Observer Tools {latestVersion} is available. You are currently using {currentVersion}.",
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(downloadButton);
        buttons.Children.Add(remindLaterButton);
        buttons.Children.Add(skipButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
        return result;
    }
}
