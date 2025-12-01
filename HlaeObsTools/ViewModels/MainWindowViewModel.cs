using Dock.Model.Core;

namespace HlaeObsTools.ViewModels;

public class MainWindowViewModel : ViewModelBase
{
    private IDock? _layout;

    public IDock? Layout
    {
        get => _layout;
        set => SetProperty(ref _layout, value);
    }

    public MainWindowViewModel()
    {
        var factory = new MainDockFactory(this);
        Layout = factory.CreateLayout();
        factory.InitLayout(Layout);
    }
}
