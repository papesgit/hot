using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LiveMarkdown.Avalonia;

namespace HlaeObsTools.Views;

public partial class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow()
        : this(new Version(0, 0), null)
    {
    }

    public ReleaseNotesWindow(Version version, string? releaseNotes)
    {
        InitializeComponent();
        Title = $"Release notes - HLAE Observer Tools {version}";
        ReleaseNotesRenderer.MarkdownBuilder = new ObservableStringBuilder(
            string.IsNullOrWhiteSpace(releaseNotes)
                ? "_No release notes were provided for this release._"
                : releaseNotes);
    }

    private void CloseDialog(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
