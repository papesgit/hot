using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using HlaeObsTools.Services.Input;

namespace HlaeObsTools.Views;

public static class DialogHelpers
{
    public static async Task MessageAsync(Control ownerControl, string title, string message)
    {
        if (TopLevel.GetTopLevel(ownerControl) is not Window owner)
            return;

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var closeButton = new Button
        {
            Content = "Close",
            IsDefault = true,
            IsCancel = true,
            Width = 80,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeButton.Click += (_, _) => dialog.Close();

        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        panel.Children.Add(closeButton);
        dialog.Content = panel;

        await dialog.ShowDialog(owner);
    }

    public static Task<string?> PromptAsync(Control ownerControl, string title, string label, string placeholder)
    {
        return PromptAsync(ownerControl, title, label, placeholder, 320, 150);
    }

    public static async Task<string?> PromptAsync(Control ownerControl, string title, string label, string placeholder, int width, int height)
    {
        if (TopLevel.GetTopLevel(ownerControl) is not Window owner)
            return null;

        var dialog = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var textBox = new TextBox { Margin = new Thickness(0, 6, 0, 6), PlaceholderText = placeholder };
        var okButton = new Button { Content = "OK", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "Cancel", IsCancel = true, Width = 80 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(textBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        string? result = null;
        okButton.Click += (_, _) =>
        {
            result = textBox.Text;
            dialog.Close(true);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        // Keep spectator bindings disabled for the lifetime of this text-entry dialog.
        await KeyboardInputGate.RunSuppressedAsync(async () =>
        {
            await dialog.ShowDialog<bool?>(owner);
            return true;
        });
        return result;
    }

    public static async Task<bool> ConfirmAsync(Control ownerControl, string title, string message)
    {
        if (TopLevel.GetTopLevel(ownerControl) is not Window owner)
            return false;

        var dialog = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var okButton = new Button { Content = "Yes", IsDefault = true, Width = 80 };
        var cancelButton = new Button { Content = "No", IsCancel = true, Width = 80 };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        panel.Children.Add(buttons);

        dialog.Content = panel;

        bool result = false;
        okButton.Click += (_, _) =>
        {
            result = true;
            dialog.Close(true);
        };
        cancelButton.Click += (_, _) => dialog.Close(false);

        await dialog.ShowDialog<bool?>(owner);
        return result;
    }
}
