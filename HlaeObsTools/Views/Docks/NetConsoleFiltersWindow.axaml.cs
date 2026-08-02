using Avalonia.Controls;
using Avalonia.Interactivity;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class NetConsoleFiltersWindow : Window
{
    public NetConsoleFiltersWindow()
    {
        InitializeComponent();
    }

    public NetConsoleFiltersWindow(NetConsoleDockViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void CloseDialog(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
