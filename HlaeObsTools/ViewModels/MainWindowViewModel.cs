using Dock.Model.Core;
using System;
using System.Reflection;

namespace HlaeObsTools.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly MainDockFactory _factory;
    private IDock? _layout;
    public string Title =>
        $"HLAE Observer Tools v{GetVersion()}";

    private static string GetVersion()
    {
        var info =
            Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";

        // Strip SemVer build metadata (e.g. "+git.abcdef")
        return info.Split('+')[0];
    }

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

    public void SetKeyboardSuppression(bool suppress)
    {
        _factory.SetKeyboardSuppression(suppress);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }
}
