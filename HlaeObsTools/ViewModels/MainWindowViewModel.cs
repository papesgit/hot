using Dock.Model.Core;
using Avalonia.Input;
using System;
using System.Reflection;
using System.Threading.Tasks;
using HlaeObsTools.Services.Hotkeys;

namespace HlaeObsTools.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private MainDockFactory? _factory;
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
    }

    public async Task InitializeAsync(Func<string, string, double, Task>? reportProgressAsync = null)
    {
        if (_factory != null)
            return;

        if (reportProgressAsync != null)
            await reportProgressAsync("Loading settings...", "Reading persisted settings and preparing startup state.", 1);

        if (reportProgressAsync != null)
            await reportProgressAsync("Initializing core services...", "Configuring network, input, graphics, and hotkey services.", 2);

        _factory = new MainDockFactory(this);

        if (reportProgressAsync != null)
            Layout = await _factory.CreateLayoutAsync(reportProgressAsync);
        else
            Layout = _factory.CreateLayout();

        if (reportProgressAsync != null)
            await reportProgressAsync("Finalizing workspace...", "Attaching dock hosts and finishing startup wiring.", 12);

        _factory.InitLayout(Layout);
    }

    public HotkeyService? HotkeyService => _factory?.HotkeyService;

    public void SetKeyboardSuppression(bool suppress)
    {
        _factory?.SetKeyboardSuppression(suppress);
    }

    public bool HandleHotkeyKeyDown(KeyEventArgs e)
    {
        return _factory?.HandleHotkeyKeyDown(e) ?? false;
    }

    public void HandleHotkeyPointerMoved(PointerEventArgs e)
    {
        _factory?.HandleHotkeyPointerMoved(e);
    }

    public void Dispose()
    {
        _factory?.Dispose();
    }
}
