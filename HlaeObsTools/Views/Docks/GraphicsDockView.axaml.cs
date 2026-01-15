using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class GraphicsDockView : UserControl
{
    public GraphicsDockView()
    {
        InitializeComponent();
    }

    private async void OnAddAtlasClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await PromptAsync("New atlas", "Atlas name", "atlas");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.AddAtlas(name);
    }

    private async void OnAddRegionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await PromptAsync("New region", "Region id", "region");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.AddRegion(name);
    }

    private async void OnAddInstanceClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsDockViewModel vm)
            return;

        var name = await PromptAsync("New instance", "Instance name", "gfx");
        if (string.IsNullOrWhiteSpace(name))
            return;
        vm.AddInstance(name);
    }

    private async Task<string?> PromptAsync(string title, string label, string placeholder)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        var dialog = new Window
        {
            Title = title,
            Width = 320,
            Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var textBox = new TextBox { Margin = new Thickness(0, 6, 0, 6), Watermark = placeholder };
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

        await dialog.ShowDialog<bool?>(owner);
        return result;
    }
}
