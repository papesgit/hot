using Dock.Model.Core;
using System;

namespace HlaeObsTools.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MainDockFactory _factory;
    private IDock? _layout;

    public IDock? Layout
    {
        get => _layout;
        set => SetProperty(ref _layout, value);
    }

    public MainWindowViewModel()
    {
        _factory = new MainDockFactory(this);
        Layout = _factory.CreateLayout();
        _factory.InitLayout(Layout);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
