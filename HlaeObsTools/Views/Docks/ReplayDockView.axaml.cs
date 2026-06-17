using Avalonia.Controls;
using HlaeObsTools.Services.Vmix;
using HlaeObsTools.ViewModels.Docks;
using System.Linq;

namespace HlaeObsTools.Views.Docks;

public partial class ReplayDockView : UserControl
{
    public ReplayDockView()
    {
        InitializeComponent();
    }

    private void OnReplaySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not ReplayDockViewModel vm || sender is not ListBox listBox)
            return;

        vm.SetSelectedEvents(listBox.SelectedItems?.OfType<ReplayEventRecord>() ?? Enumerable.Empty<ReplayEventRecord>());
    }
}
