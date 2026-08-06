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
        string productName,
        Version currentVersion,
        Version latestVersion,
        string releasePageUrl,
        string? releaseNotes,
        string downloadButtonText = "View release",
        string? availabilityMessage = null,
        bool showReleaseNotes = true)
    {
        var dialog = new Window
        {
            Title = "Update available",
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 560,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var result = UpdateAvailableDialogResult.RemindLater;
        var downloadButton = new Button { Content = downloadButtonText, IsDefault = true };
        var showNotesButton = new Button { Content = "Show release notes" };
        var remindLaterButton = new Button { Content = "Remind me later" };
        var skipButton = new Button { Content = "Skip this version", IsCancel = true };

        downloadButton.Click += (_, _) =>
        {
            ExternalLinkLauncher.TryOpen(releasePageUrl);
        };
        showNotesButton.Click += (_, _) => new ReleaseNotesWindow(latestVersion, releaseNotes).Show(dialog);
        remindLaterButton.Click += (_, _) => dialog.Close();
        skipButton.Click += (_, _) =>
        {
            result = UpdateAvailableDialogResult.SkipVersion;
            dialog.Close();
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = availabilityMessage
                ?? $"{productName} {latestVersion} is available.\nYou are currently using {currentVersion}.",
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        buttons.Children.Add(downloadButton);
        if (showReleaseNotes)
            buttons.Children.Add(showNotesButton);
        buttons.Children.Add(remindLaterButton);
        buttons.Children.Add(skipButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
        return result;
    }
}
