using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Timer = System.Threading.Timer;
using FormsKeys = System.Windows.Forms.Keys;
using Avalonia.Input;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;
using HlaeObsTools.ViewModels.Hud;
using HlaeObsTools.Services.Vmix;
using HlaeObsTools.Services.Hotkeys;

namespace HlaeObsTools.ViewModels;

public class MainDockFactory : Factory, IDisposable
{
    private readonly object _context;
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly HlaeInputSender _inputSender;
    private readonly RawInputHandler _rawInputHandler;
    private XInputHandler? _xinputHandler;
    private FreecamSettings? _freecamSettings;
    private readonly Timer _inputFlushTimer;
    private readonly GsiServer _gsiServer;
    private readonly RadarConfigProvider _radarConfigProvider;
    private readonly SettingsStorage _settingsStorage;
    private readonly AppSettingsData _storedSettings;
    private readonly HotkeyService _hotkeyService;
    private readonly VmixReplayService _vmixReplayService;
    private readonly VmixReplaySettings _vmixReplaySettings;
    private VideoDisplayDockViewModel? _videoDisplayVm;
    private bool _disposed;

    public MainDockFactory(object context)
    {
        _context = context;

        _settingsStorage = new SettingsStorage();
        _storedSettings = _settingsStorage.Load();
        _hotkeyService = new HotkeyService();
        _hotkeyService.SetBindings(_storedSettings.Hotkeys ?? new List<HotkeyBindingData>());

        // Initialize WebSocket client
        _webSocketClient = new HlaeWebSocketClient(_storedSettings.WebSocketHost, _storedSettings.WebSocketPort);
        _webSocketClient.MessageReceived += OnHlaeMessage;
        _ = _webSocketClient.ConnectAsync(); // Fire and forget

        // Initialize UDP input sender (send at 240Hz to HLAE)
        _inputSender = new HlaeInputSender(_storedSettings.WebSocketHost, _storedSettings.UdpPort);
        _inputSender.SendRate = 240; // Hz
        _inputSender.Start();

        _gsiServer = new GsiServer();
        _radarConfigProvider = new RadarConfigProvider();
        _vmixReplaySettings = new VmixReplaySettings
        {
            Enabled = _storedSettings.VmixReplayEnabled,
            Host = _storedSettings.VmixReplayHost,
            Port = _storedSettings.VmixReplayPort,
            PreSeconds = _storedSettings.VmixReplayPreSeconds,
            PostSeconds = _storedSettings.VmixReplayPostSeconds,
            ExtendWindowSeconds = _storedSettings.VmixReplayExtendWindowSeconds
        };
        _vmixReplayService = new VmixReplayService(_webSocketClient, _gsiServer, _vmixReplaySettings);

        // Initialize global raw input handler and periodically flush into UDP sender
        _rawInputHandler = new RawInputHandler();
        _rawInputHandler.CaptureOnlyWhenAppFocused = !_storedSettings.DisableFocusInputGate;
        _rawInputHandler.SetInputSender(_inputSender);
        _rawInputHandler.KeyPressed += OnRawInputKeyPressed;
        _rawInputHandler.KeyStateChanged += OnRawInputKeyStateChanged;
        _inputFlushTimer = new Timer(_ => _rawInputHandler.FlushToSender(), null, 0, 4);

        Console.WriteLine("Observer tools initialized: WebSocket (127.0.0.1:31338), UDP (127.0.0.1:31339)");
    }

    private void OnHlaeMessage(object? sender, string json)
    {
        // Handle messages from HLAE (state updates, events, etc.)
        Console.WriteLine($"HLAE message: {json}");
        // TODO: Parse JSON and update UI state
    }

    private async Task ApplyNetworkSettingsAsync(SettingsDockViewModel.NetworkSettingsData data)
    {
        _storedSettings.WebSocketHost = data.WebSocketHost;
        _storedSettings.WebSocketPort = data.WebSocketPort;
        _storedSettings.UdpPort = data.UdpPort;
        _storedSettings.RtpPort = data.RtpPort;
        _storedSettings.GsiPort = data.GsiPort;
        _settingsStorage.Save(_storedSettings);

        _webSocketClient.ConfigureEndpoint(data.WebSocketHost, data.WebSocketPort);
        await _webSocketClient.ReconnectAsync();

        _inputSender.ConfigureEndpoint(data.WebSocketHost, data.UdpPort, restartIfActive: true);

        if (_videoDisplayVm != null)
        {
            _videoDisplayVm.SetRtpConfig(new Services.Video.RTP.RtpReceiverConfig
            {
                Address = "0.0.0.0",
                Port = data.RtpPort
            });

            if (_videoDisplayVm.IsStreaming)
            {
                _videoDisplayVm.StopStream();
                _videoDisplayVm.StartStream();
            }
        }

        // Restart GSI listener with new endpoint
        _gsiServer.Stop();
        _gsiServer.Start(data.GsiPort, "/gsi/", "0.0.0.0");
    }

    public override IDocumentDock CreateDocumentDock() => new DocumentDock();
    public override IToolDock CreateToolDock() => new ToolDock();
    public override IProportionalDock CreateProportionalDock() => new ProportionalDock();
    public override IProportionalDockSplitter CreateProportionalDockSplitter() => new ProportionalDockSplitter();

    public override IRootDock CreateLayout()
    {
        // Shared settings for radar customization
        var radarSettings = new RadarSettings
        {
            RadarScale = _storedSettings.RadarScale,
            MarkerScale = _storedSettings.MarkerScale,
            HeightScaleMultiplier = _storedSettings.HeightScaleMultiplier,
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds,
            DisplayNumbersTopmost = _storedSettings.DisplayNumbersTopmost,
            ShowPlayerNames = _storedSettings.ShowPlayerNames
        };
        var hudSettings = new HudSettings
        {
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds
        };
        hudSettings.ActiveAttachPresetPage = _storedSettings.ActiveAttachPresetPage;
        hudSettings.ApplyAttachPresetPages(_storedSettings.AttachPresetPages, _storedSettings.AttachPresets);
        var freecamSettings = new FreecamSettings();
        if (_storedSettings.FreecamSettings != null)
        {
            freecamSettings.Apply(_storedSettings.FreecamSettings);
        }
        _freecamSettings = freecamSettings;
        var campathEditor = new CampathEditorViewModel();
        var viewport3DSettings = new Viewport3DSettings
        {
            MapObjPath = _storedSettings.MapObjPath ?? string.Empty,
            UseLegacyD3D11Viewport = _storedSettings.ViewportUseLegacyD3D11,
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds,
            PinScale = (float)_storedSettings.PinScale,
            PinOffsetZ = (float)_storedSettings.PinOffsetZ,
            ViewportMouseScale = (float)_storedSettings.ViewportMouseScale,
            MapScale = (float)_storedSettings.MapScale,
            MapYaw = (float)_storedSettings.MapYaw,
            MapPitch = (float)_storedSettings.MapPitch,
            MapRoll = (float)_storedSettings.MapRoll,
            MapOffsetX = (float)_storedSettings.MapOffsetX,
            MapOffsetY = (float)_storedSettings.MapOffsetY,
            MapOffsetZ = (float)_storedSettings.MapOffsetZ,
            ViewportFpsCap = (float)_storedSettings.ViewportFpsCap,
            PostprocessEnabled = _storedSettings.ViewportPostprocessEnabled,
            ColorCorrectionEnabled = _storedSettings.ViewportColorCorrectionEnabled,
            DynamicShadowsEnabled = _storedSettings.ViewportDynamicShadowsEnabled,
            WireframeEnabled = _storedSettings.ViewportWireframeEnabled,
            SkipWaterEnabled = _storedSettings.ViewportSkipWaterEnabled,
            SkipTranslucentEnabled = _storedSettings.ViewportSkipTranslucentEnabled,
            ShowFps = _storedSettings.ViewportShowFps,
            ViewportCampathMode = _storedSettings.ViewportCampathMode,
            ViewportCampathOverlayEnabled = _storedSettings.ViewportCampathOverlayEnabled,
            ViewportCampathSyncEnabled = _storedSettings.ViewportCampathSyncEnabled,
            CampathGizmoLocalSpace = _storedSettings.CampathGizmoLocalSpace,
            ShadowTextureSize = _storedSettings.ViewportShadowTextureSize,
            MaxTextureSize = _storedSettings.ViewportMaxTextureSize,
            RenderMode = _storedSettings.ViewportRenderMode
        };

        // Create the docks (top-right hosts the CS2 console)
        var bottomRight = new CampathsDockViewModel { Id = "BottomRight", Title = "Campaths" };
        var topLeft = new RadarDockViewModel(_gsiServer, _radarConfigProvider, radarSettings, bottomRight, _webSocketClient) { Id = "TopLeft", Title = "Radar" };
        _videoDisplayVm = new VideoDisplayDockViewModel { Id = "TopCenter", Title = "Video Stream" };
        var topRight = new NetConsoleDockViewModel { Id = "TopRight", Title = "Console" };
        var bottomLeft = new SettingsDockViewModel(
            radarSettings,
            hudSettings,
            freecamSettings,
            viewport3DSettings,
            _settingsStorage,
            _webSocketClient,
            _hotkeyService,
            ApplyNetworkSettingsAsync,
            _storedSettings,
            _vmixReplaySettings,
            setFocusInputGateDisabled: disable => _rawInputHandler.CaptureOnlyWhenAppFocused = !disable,
            campathEditor: campathEditor)
        { Id = "BottomLeft", Title = "Settings" };
        var bottomCenter = new Viewport3DDockViewModel(viewport3DSettings, freecamSettings, campathEditor, _webSocketClient, _videoDisplayVm, _gsiServer) { Id = "BottomCenter", Title = "3D Viewport" };
        bottomCenter.SetInputSender(_inputSender);

        // Inject WebSocket and UDP services into video display
        _videoDisplayVm.SetWebSocketClient(_webSocketClient);
        _videoDisplayVm.SetInputSender(_inputSender);
        _videoDisplayVm.SetFreecamSettings(freecamSettings);
        var hudOverlayVm = new HudOverlayViewModel(_gsiServer, hudSettings, _webSocketClient, bottomRight);
        _videoDisplayVm.SetHudOverlay(hudOverlayVm);
        _hotkeyService.RegisterCommandContext(topLeft);
        _hotkeyService.RegisterCommandContext(topRight);
        _hotkeyService.RegisterCommandContext(bottomLeft);
        _hotkeyService.RegisterCommandContext(bottomRight);
        _hotkeyService.RegisterCommandContext(bottomCenter);
        _hotkeyService.RegisterCommandContext(_videoDisplayVm);
        _hotkeyService.RegisterCommandContext(campathEditor);
        _hotkeyService.RegisterCommandContext(hudOverlayVm);
        _hotkeyService.RegisterCommandContext(bottomLeft.AttachPresetAnimationEditor);
        ConfigureAnalogInput(freecamSettings);
        _videoDisplayVm.SetRtpConfig(new Services.Video.RTP.RtpReceiverConfig
        {
            Address = "0.0.0.0",
            Port = _storedSettings.RtpPort
        });
        // Start GSI listener on all interfaces with configured port
        _gsiServer.Start(_storedSettings.GsiPort, "/gsi/", "0.0.0.0");
        bottomRight.SetWebSocketClient(_webSocketClient);

        // Wrap tools in ToolDocks for proper docking behavior
        // Top-left: Controls - 1:1 aspect ratio (roughly square)
        var topLeftDock = new ToolDock
        {
            Id = "TopLeftDock",
            Proportion = 0.3,
            ActiveDockable = topLeft,
            VisibleDockables = CreateList<IDockable>(topLeft)
        };

        // Top-center: Video Stream - 16:9 aspect ratio
        var topCenterDock = new ToolDock
        {
            Id = "TopCenterDock",
            Proportion = 0.5,
            ActiveDockable = _videoDisplayVm,
            VisibleDockables = CreateList<IDockable>(_videoDisplayVm)
        };

        // Top-right: Settings - remaining space
        var topRightDock = new ToolDock
        {
            Id = "TopRightDock",
            Proportion = 0.2,
            ActiveDockable = topRight,
            VisibleDockables = CreateList<IDockable>(topRight)
        };

        var bottomLeftDock = new ToolDock
        {
            Id = "BottomLeftDock",
            Proportion = 0.3,
            ActiveDockable = bottomLeft,
            VisibleDockables = CreateList<IDockable>(bottomLeft)
        };

        var bottomCenterDock = new ToolDock
        {
            Id = "BottomCenterDock",
            Proportion = 0.4,
            ActiveDockable = bottomCenter,
            VisibleDockables = CreateList<IDockable>(bottomCenter)
        };

        var bottomRightDock = new ToolDock
        {
            Id = "BottomRightDock",
            Proportion = 0.3,
            ActiveDockable = bottomRight,
            VisibleDockables = CreateList<IDockable>(bottomRight)
        };

        // Create top row (3 docks with splitters between them)
        var topRow = new ProportionalDock
        {
            Id = "TopRow",
            Proportion = double.NaN,
            Orientation = Orientation.Horizontal,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>
            (
                topLeftDock,
                new ProportionalDockSplitter(),
                topCenterDock,
                new ProportionalDockSplitter(),
                topRightDock
            )
        };

        // Create bottom row (3 docks with splitters between them)
        var bottomRow = new ProportionalDock
        {
            Id = "BottomRow",
            Proportion = double.NaN,
            Orientation = Orientation.Horizontal,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>
            (
                bottomLeftDock,
                new ProportionalDockSplitter(),
                bottomCenterDock,
                new ProportionalDockSplitter(),
                bottomRightDock
            )
        };

        // Create main layout (top and bottom rows with splitter between them)
        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Proportion = double.NaN,
            Orientation = Orientation.Vertical,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>
            (
                topRow,
                new ProportionalDockSplitter(),
                bottomRow
            )
        };

        // Set proportions for rows
        topRow.Proportion = 0.6; // Top takes 60%
        bottomRow.Proportion = 0.4; // Bottom takes 40%

        // Create root dock
        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "HLAE Observer Tools";
        rootDock.ActiveDockable = mainLayout;
        rootDock.DefaultDockable = mainLayout;
        rootDock.VisibleDockables = CreateList<IDockable>(mainLayout);

        return rootDock;
    }

    public override void InitLayout(IDockable layout)
    {
        ContextLocator = new Dictionary<string, Func<object?>>
        {
            ["Root"] = () => _context
        };

        DockableLocator = new Dictionary<string, Func<IDockable?>>
        {
        };

        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () =>
            {
                var hostWindow = new Views.DockHostWindow();
                hostWindow.SetKeyboardSuppressionHandler(SetKeyboardSuppression);
                hostWindow.SetHotkeyHandlers(HandleHotkeyKeyDown, HandleHotkeyPointerMoved);
                return hostWindow;
            }
        };

        base.InitLayout(layout);
    }

    public void SetKeyboardSuppression(bool suppress)
    {
        _rawInputHandler.SuppressKeyboard = suppress;
    }

    public bool HandleHotkeyKeyDown(KeyEventArgs e)
    {
        return _hotkeyService.HandleKeyDown(e);
    }

    public void HandleHotkeyPointerMoved(PointerEventArgs e)
    {
        _hotkeyService.HandlePointerMoved(e);
    }

    private void ConfigureAnalogInput(FreecamSettings freecamSettings)
    {
        _xinputHandler?.Dispose();
        _xinputHandler = new XInputHandler(_inputSender);
        _xinputHandler.Start();
        ApplyAnalogSettings(freecamSettings);

        freecamSettings.PropertyChanged -= OnFreecamSettingsChanged;
        freecamSettings.PropertyChanged += OnFreecamSettingsChanged;
    }

    private void OnFreecamSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_freecamSettings == null || _xinputHandler == null)
            return;

        if (e.PropertyName == nameof(FreecamSettings.AnalogKeyboardEnabled)
            || e.PropertyName == nameof(FreecamSettings.AnalogLeftDeadzone)
            || e.PropertyName == nameof(FreecamSettings.AnalogRightDeadzone)
            || e.PropertyName == nameof(FreecamSettings.AnalogCurve))
        {
            ApplyAnalogSettings(_freecamSettings);
        }
    }

    private void ApplyAnalogSettings(FreecamSettings freecamSettings)
    {
        if (_xinputHandler == null)
            return;

        _xinputHandler.SetSettings(
            freecamSettings.AnalogKeyboardEnabled,
            (float)freecamSettings.AnalogLeftDeadzone,
            (float)freecamSettings.AnalogRightDeadzone,
            (float)freecamSettings.AnalogCurve);
    }

    private void OnRawInputKeyPressed(object? sender, FormsKeys key)
    {
        if (key != FormsKeys.C)
            return;
        if (_videoDisplayVm == null || !_videoDisplayVm.IsFreecamActive)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_videoDisplayVm == null || !_videoDisplayVm.IsFreecamActive)
                return;

            _videoDisplayVm.EnableFreecamHold();
            _videoDisplayVm.RequestFreecamInputRelease();
        }, DispatcherPriority.Background);
    }

    private void OnRawInputKeyStateChanged(object? sender, (FormsKeys Key, bool IsDown) e)
    {
        if (e.Key != FormsKeys.LShiftKey && e.Key != FormsKeys.RShiftKey && e.Key != FormsKeys.ShiftKey)
            return;
        if (_videoDisplayVm == null)
            return;

        Dispatcher.UIThread.Post(() => _videoDisplayVm?.SetSprintModifierState(e.IsDown), DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Stop periodic flushing before disposing input resources
        try
        {
            _inputFlushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch
        {
            // Ignore if timer is already disposed or invalid
        }
        _inputFlushTimer.Dispose();

        _rawInputHandler.KeyPressed -= OnRawInputKeyPressed;
        _rawInputHandler.KeyStateChanged -= OnRawInputKeyStateChanged;
        _rawInputHandler.Dispose();
        _inputSender.Dispose();
        if (_freecamSettings != null)
        {
            _freecamSettings.PropertyChanged -= OnFreecamSettingsChanged;
        }
        _xinputHandler?.Dispose();

        _gsiServer.Dispose();
        _webSocketClient.MessageReceived -= OnHlaeMessage;
        _webSocketClient.Dispose();

        _videoDisplayVm?.Dispose();
        _vmixReplayService.Dispose();

        _disposed = true;
    }
}
