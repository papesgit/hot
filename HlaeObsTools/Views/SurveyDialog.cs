using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace HlaeObsTools.Views;

public enum SurveyDialogResult
{
    RemindLater,
    DontRemind
}

public static class SurveyDialog
{
    public static async Task<SurveyDialogResult> ShowAsync(Window owner, string surveyUrl)
    {
        var dialog = new Window
        {
            Title = "HOT User Survey",
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 560,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var result = SurveyDialogResult.RemindLater;
        var visitSurveyButton = new Button { Content = "Visit Survey", IsDefault = true };
        var remindLaterButton = new Button { Content = "Remind me later" };
        var dontRemindButton = new Button { Content = "Don't remind me", IsCancel = true };

        visitSurveyButton.Click += (_, _) => ExternalLinkLauncher.TryOpen(surveyUrl);
        remindLaterButton.Click += (_, _) => dialog.Close();
        dontRemindButton.Click += (_, _) =>
        {
            result = SurveyDialogResult.DontRemind;
            dialog.Close();
        };

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = "Help shaping HOT's future by taking a short survey.",
            TextWrapping = TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        buttons.Children.Add(visitSurveyButton);
        buttons.Children.Add(remindLaterButton);
        buttons.Children.Add(dontRemindButton);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
        return result;
    }
}
