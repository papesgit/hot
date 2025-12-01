using Avalonia.Controls;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}
