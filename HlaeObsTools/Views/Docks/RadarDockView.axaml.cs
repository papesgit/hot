using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using HlaeObsTools.ViewModels.Docks;

namespace HlaeObsTools.Views.Docks;

public partial class RadarDockView : UserControl
{
    public RadarDockView()
    {
        InitializeComponent();
    }

    private void CampathCamera_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is CampathPathViewModel path)
        {
            vm.PlayCampath(path);
            e.Handled = true;
        }
    }

    private void CampathIcon_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is CampathPathViewModel path)
        {
            vm.SetCampathHighlight(path, true);
        }
    }

    private void CampathIcon_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is RadarDockViewModel vm && sender is Control ctrl && ctrl.DataContext is CampathPathViewModel path)
        {
            vm.SetCampathHighlight(path, false);
        }
    }
}
