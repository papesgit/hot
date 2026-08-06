using Dock.Model.Core;
using Avalonia.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HlaeObsTools.Services.Hotkeys;
using HlaeObsTools.Services.Updates;

namespace HlaeObsTools.ViewModels;

public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private MainDockFactory? _factory;
    private readonly UpdateCheckService _updateCheckService;
    private IDock? _layout;

    public IReadOnlyList<DockMenuItemViewModel> DockMenuItems { get; } =
    [
        new("BottomRight", "Campaths"),
        new("CampathSequencer", "Campath Sequencer"),
        new("TopRight", "Console"),
        new("CurveEditor", "Curve Editor"),
        new("Graphics", "Graphics"),
        new("TopLeft", "Radar"),
        new("Replay", "Replay"),
        new("BottomLeft", "Settings"),
        new("BottomCenter", "3D Viewport"),
        new("TopCenter", "Video Stream")
    ];

    public List<LayoutMenuItemViewModel> LayoutMenuItems { get; } = new();
    public event EventHandler? LayoutMenuChanged;
    public string ActiveLayoutName { get; private set; } = string.Empty;
    public bool CanDeleteActiveLayout { get; private set; }

    public string Version => GetVersion();

    public string Title =>
        $"HLAE Observer Tools v{Version}";

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

    public MainWindowViewModel(UpdateCheckService? updateCheckService = null)
    {
        _updateCheckService = updateCheckService ?? new UpdateCheckService();
    }

    public async Task InitializeAsync(Func<string, string, double, Task>? reportProgressAsync = null)
    {
        if (_factory != null)
            return;

        if (reportProgressAsync != null)
            await reportProgressAsync("Loading settings...", "Reading persisted settings and preparing startup state.", 1);

        if (reportProgressAsync != null)
            await reportProgressAsync("Initializing core services...", "Configuring network, input, graphics, and hotkey services.", 2);

        _factory = new MainDockFactory(this, _updateCheckService);

        if (reportProgressAsync != null)
            Layout = await _factory.CreateLayoutAsync(reportProgressAsync);
        else
            Layout = _factory.CreateLayout();

        if (reportProgressAsync != null)
            await reportProgressAsync("Finalizing workspace...", "Attaching dock hosts and finishing startup wiring.", 12);

        _factory.InitLayout(Layout);
    }

    public HotkeyService? HotkeyService => _factory?.HotkeyService;

    public void ToggleDock(string id)
    {
        _factory?.ToggleDock(id);
    }

    internal void SetDockVisibility(string? id, bool isOpen)
    {
        var menuItem = DockMenuItems.FirstOrDefault(item =>
            string.Equals(item.Id, id, StringComparison.Ordinal));
        if (menuItem != null)
            menuItem.IsOpen = isOpen;
    }

    internal void SetLayouts(IEnumerable<(string Name, bool IsBuiltIn)> layouts, string activeLayout)
    {
        LayoutMenuItems.Clear();
        foreach (var layout in layouts)
        {
            LayoutMenuItems.Add(new LayoutMenuItemViewModel(
                layout.Name,
                layout.IsBuiltIn,
                string.Equals(layout.Name, activeLayout, StringComparison.OrdinalIgnoreCase)));
        }

        UpdateActiveLayoutState(activeLayout);
        LayoutMenuChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetActiveLayout(string name)
    {
        foreach (var item in LayoutMenuItems)
            item.IsSelected = string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase);

        UpdateActiveLayoutState(name);
        LayoutMenuChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateActiveLayoutState(string name)
    {
        ActiveLayoutName = name;
        CanDeleteActiveLayout = LayoutMenuItems.Any(item =>
            item.IsSelected && !item.IsBuiltIn);
    }

    public void SelectLayout(string name)
    {
        _factory?.SelectLayout(name);
    }

    public string? SaveCurrentLayout(string name)
    {
        if (_factory == null)
            return "The workspace is not ready yet.";

        return _factory.SaveCurrentLayout(name);
    }

    public void DeleteLayout(string name)
    {
        _factory?.DeleteLayout(name);
    }

    public bool ShouldWaitForVideoDockStartup =>
        _factory?.IsDockContentActive("TopCenter") == true;

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

public sealed class DockMenuItemViewModel : ViewModelBase
{
    private bool _isOpen = true;

    public DockMenuItemViewModel(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }
    public string Title { get; }

    public bool IsOpen
    {
        get => _isOpen;
        internal set => SetProperty(ref _isOpen, value);
    }
}

public sealed class LayoutMenuItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public LayoutMenuItemViewModel(string name, bool isBuiltIn, bool isSelected)
    {
        Name = name;
        IsBuiltIn = isBuiltIn;
        _isSelected = isSelected;
    }

    public string Name { get; }
    public bool IsBuiltIn { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
