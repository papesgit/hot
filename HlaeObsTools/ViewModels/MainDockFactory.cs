using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
using HlaeObsTools.Services.ReplayDirector;
using HlaeObsTools.Services.Graphics;
using HlaeObsTools.Services.Hotkeys;
using HlaeObsTools.Services.LiveLink;
using HlaeObsTools.Services.Video;

namespace HlaeObsTools.ViewModels;

public class MainDockFactory : Factory, IDisposable
{
    private const string DefaultCs2GameFolder = @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive";

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
    private readonly VmixApiClient _vmixApiClient;
    private readonly ReplayEventRegistry _replayEventRegistry;
    private readonly VmixReplayCoordinator _vmixReplayCoordinator;
    private readonly VmixReplayService _vmixReplayService;
    private readonly VmixSettings _vmixSettings;
    private readonly VmixReplaySettings _vmixReplaySettings;
    private readonly ReplayDirectorSettings _replayDirectorSettings;
    private readonly VmixReplayMarker _delayedReplayMarker;
    private readonly ReplayDirectorPublisher _replayDirectorPublisher;
    private readonly ReplayDirectorFollower _replayDirectorFollower;
    private readonly GraphicsProfileStorage _graphicsProfileStorage;
    private readonly GraphicsService _graphicsService;
    private readonly GraphicsProducerClient _producerClient;
    private readonly Cs2LiveLinkReceiver _liveLinkReceiver;
    private VideoDisplayDockViewModel? _videoDisplayVm;
    private GraphicsDockViewModel? _graphicsDockVm;
    private ReplayDockViewModel? _replayDockVm;
    private bool _disposed;
    public HotkeyService HotkeyService => _hotkeyService;

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
        _gsiServer.ConfigureRelayEndpoints(_storedSettings.GsiRelayUris);
        _radarConfigProvider = new RadarConfigProvider();
        _vmixSettings = new VmixSettings
        {
            Host = _storedSettings.VmixReplayHost,
            Port = _storedSettings.VmixReplayPort
        };
        _vmixReplaySettings = new VmixReplaySettings
        {
            Enabled = _storedSettings.VmixReplayEnabled,
            PreSeconds = _storedSettings.VmixReplayPreSeconds,
            PostSeconds = _storedSettings.VmixReplayPostSeconds,
            ExtendWindowSeconds = _storedSettings.VmixReplayExtendWindowSeconds,
            Channel = _storedSettings.VmixReplayChannel,
            Camera = _storedSettings.VmixReplayCamera
        };
        _replayDirectorSettings = new ReplayDirectorSettings
        {
            Role = _storedSettings.ReplayDirectorRole,
            PublisherPort = _storedSettings.ReplayDirectorPublisherPort,
            PublisherIp = GetReplayDirectorPublisherHost(_storedSettings),
            PreSwitchSeconds = _storedSettings.ReplayDirectorPreSwitchSeconds,
            MergeWindowSeconds = _storedSettings.ReplayDirectorMergeWindowSeconds,
            SwitchLockSeconds = _storedSettings.ReplayDirectorSwitchLockSeconds,
            OnlyFollowMissedKills = _storedSettings.ReplayDirectorOnlyFollowMissedKills,
            DelayedVmixEnabled = _storedSettings.ReplayDirectorDelayedVmixEnabled,
            DelayedVmixChannel = _storedSettings.ReplayDirectorDelayedVmixChannel,
            DelayedVmixCamera = _storedSettings.ReplayDirectorDelayedVmixCamera
        };
        _vmixApiClient = new VmixApiClient(_vmixSettings);
        _replayEventRegistry = new ReplayEventRegistry();
        _vmixReplayCoordinator = new VmixReplayCoordinator(_vmixApiClient, _replayEventRegistry);
        _hotkeyService.SetVmixApiClient(_vmixApiClient);
        _vmixReplayService = new VmixReplayService(_webSocketClient, _gsiServer, _vmixApiClient, _vmixReplayCoordinator, _vmixReplaySettings);
        _delayedReplayMarker = new VmixReplayMarker(_vmixApiClient, _vmixReplayCoordinator);
        _replayDirectorPublisher = new ReplayDirectorPublisher(_webSocketClient, _gsiServer, _replayDirectorSettings, _vmixReplaySettings, _delayedReplayMarker);
        _replayDirectorFollower = new ReplayDirectorFollower(_webSocketClient, _gsiServer, _replayDirectorSettings);

        _graphicsProfileStorage = new GraphicsProfileStorage();
        _producerClient = new GraphicsProducerClient(_storedSettings.WebSocketHost, _storedSettings.GraphicsProducerPort);
        _ = _producerClient.ConnectAsync();
        _graphicsService = new GraphicsService(_webSocketClient, _producerClient, _gsiServer, _graphicsProfileStorage, _storedSettings.GraphicsTargetFps);
        _graphicsService.LoadProfile(_graphicsService.CurrentProfileName);
        _liveLinkReceiver = new Cs2LiveLinkReceiver
        {
            Port = _storedSettings.ViewportLiveLinkPort,
            Enabled = _storedSettings.ViewportLiveLinkEnabled
        };

        // Initialize global raw input handler and periodically flush into UDP sender
        _rawInputHandler = new RawInputHandler();
        _rawInputHandler.CaptureOnlyWhenAppFocused = !_storedSettings.DisableFocusInputGate;
        _rawInputHandler.SetInputSender(_inputSender);
        KeyboardInputGate.SetSuppressionSink(suppress => _rawInputHandler.SuppressKeyboard = suppress);
        _rawInputHandler.KeyPressed += OnRawInputKeyPressed;
        _rawInputHandler.MiddleMousePressed += OnRawInputMiddleMousePressed;
        _rawInputHandler.KeyStateChanged += OnRawInputKeyStateChanged;
        _inputFlushTimer = new Timer(_ => _rawInputHandler.FlushToSender(), null, 0, 4);

        Console.WriteLine("Observer tools initialized: WebSocket (127.0.0.1:31338), UDP (127.0.0.1:31339)");
    }

    private static string GetReplayDirectorPublisherHost(AppSettingsData settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ReplayDirectorPublisherIp))
            return settings.ReplayDirectorPublisherIp;

        if (Uri.TryCreate(settings.ReplayDirectorFollowerEndpoint, UriKind.Absolute, out var endpoint) && !string.IsNullOrWhiteSpace(endpoint.Host))
            return endpoint.Host;

        return "127.0.0.1";
    }

    private void OnHlaeMessage(object? sender, string json)
    {
        if (IsHlaeErrorMessage(json))
        {
            Console.WriteLine($"HLAE error message: {json}");
        }
        // TODO: Parse JSON and update UI state
    }

    private static bool IsHlaeErrorMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var ok)
                && ok.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task ApplyNetworkSettingsAsync(SettingsDockViewModel.NetworkSettingsData data)
    {
        _storedSettings.WebSocketHost = data.WebSocketHost;
        _storedSettings.WebSocketPort = data.WebSocketPort;
        _storedSettings.GraphicsProducerHost = data.WebSocketHost;
        _storedSettings.GraphicsProducerPort = data.GraphicsProducerPort;
        _storedSettings.UdpPort = data.UdpPort;
        _storedSettings.RtpPort = data.RtpPort;
        _storedSettings.GsiPort = data.GsiPort;
        _storedSettings.GsiRelayUris = data.GsiRelayUris.ToList();
        _settingsStorage.Save(_storedSettings);
        _gsiServer.ConfigureRelayEndpoints(data.GsiRelayUris);

        _webSocketClient.ConfigureEndpoint(data.WebSocketHost, data.WebSocketPort);
        await _webSocketClient.ReconnectAsync();

        _producerClient.ConfigureEndpoint(data.WebSocketHost, data.GraphicsProducerPort);
        await _producerClient.ReconnectAsync();

        _inputSender.ConfigureEndpoint(data.WebSocketHost, data.UdpPort, restartIfActive: true);

        if (_videoDisplayVm != null)
        {
            _videoDisplayVm.SetRtpConfig(new RtpReceiverConfig
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
        _gsiServer.Start(data.GsiPort, "/gsi/");
    }

    public override IDocumentDock CreateDocumentDock() => new DocumentDock();
    public override IToolDock CreateToolDock() => new ToolDock();
    public override IProportionalDock CreateProportionalDock() => new ProportionalDock();
    public override IProportionalDockSplitter CreateProportionalDockSplitter() => new ProportionalDockSplitter();

    public async Task<IRootDock> CreateLayoutAsync(Func<string, string, double, Task> reportProgressAsync)
    {
        return await CreateLayoutCoreAsync(reportProgressAsync);
    }

    public override IRootDock CreateLayout()
    {
        return CreateLayoutCoreAsync(null).GetAwaiter().GetResult();
    }

    private static string GetDefaultCs2GameFolder()
    {
        return Directory.Exists(DefaultCs2GameFolder) ? DefaultCs2GameFolder : string.Empty;
    }

    private async Task<IRootDock> CreateLayoutCoreAsync(Func<string, string, double, Task>? reportProgressAsync)
    {
        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Preparing shared settings...", "Creating the runtime settings models used across the workspace.", 3);
        }

        // Shared settings for radar customization
        var radarSettings = new RadarSettings
        {
            RadarScale = _storedSettings.RadarScale,
            MarkerScale = _storedSettings.MarkerScale,
            HeightScaleMultiplier = _storedSettings.HeightScaleMultiplier,
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds,
            DisplayNumbersTopmost = _storedSettings.DisplayNumbersTopmost,
            ShowPlayerNames = _storedSettings.ShowPlayerNames,
            RadarStyle = _storedSettings.RadarStyle
        };
        var hudSettings = new HudSettings
        {
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds,
            HudSize = _storedSettings.HudSize
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
        var cs2GameFolder = string.IsNullOrWhiteSpace(_storedSettings.Cs2GameFolder)
            ? GetDefaultCs2GameFolder()
            : _storedSettings.Cs2GameFolder;
        var viewport3DSettings = new Viewport3DSettings
        {
            MapObjPath = _storedSettings.MapObjPath ?? string.Empty,
            Cs2GameFolder = cs2GameFolder,
            SelectedMapName = _storedSettings.ViewportSelectedMapName ?? string.Empty,
            ActiveDutyMapsOnly = _storedSettings.ViewportActiveDutyMapsOnly,
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds,
            ShowPlayerPins = _storedSettings.ViewportShowPlayerPins,
            PinScale = (float)_storedSettings.PinScale,
            PinOffsetZ = (float)_storedSettings.PinOffsetZ,
            ViewportMouseScale = (float)_storedSettings.ViewportMouseScale,
            ViewportFpsCap = (float)_storedSettings.ViewportFpsCap,
            PostprocessEnabled = _storedSettings.ViewportPostprocessEnabled,
            ColorCorrectionEnabled = _storedSettings.ViewportColorCorrectionEnabled,
            DynamicShadowsEnabled = _storedSettings.ViewportDynamicShadowsEnabled,
            WireframeEnabled = _storedSettings.ViewportWireframeEnabled,
            SkipWaterEnabled = _storedSettings.ViewportSkipWaterEnabled,
            SkipTranslucentEnabled = _storedSettings.ViewportSkipTranslucentEnabled,
            ShowFps = _storedSettings.ViewportShowFps,
            ViewportCampathOverlayEnabled = _storedSettings.ViewportCampathOverlayEnabled,
            ViewportCampathGizmoEnabled = _storedSettings.ViewportCampathGizmoEnabled,
            ViewportCampathSyncEnabled = _storedSettings.ViewportCampathSyncEnabled,
            CampathGizmoLocalSpace = _storedSettings.CampathGizmoLocalSpace,
            LiveLinkEnabled = _storedSettings.ViewportLiveLinkEnabled,
            LiveLinkItemIconsEnabled = _storedSettings.ViewportLiveLinkItemIconsEnabled,
            LiveLinkWeaponIconsEnabled = _storedSettings.ViewportLiveLinkWeaponIconsEnabled,
            LiveLinkGrenadeIconsEnabled = _storedSettings.ViewportLiveLinkGrenadeIconsEnabled,
            LiveLinkProjectileIconsEnabled = _storedSettings.ViewportLiveLinkProjectileIconsEnabled,
            LiveLinkObjectiveIconsEnabled = _storedSettings.ViewportLiveLinkObjectiveIconsEnabled,
            LiveLinkDeadPlayerIconsEnabled = _storedSettings.ViewportLiveLinkDeadPlayerIconsEnabled,
            LiveLinkPort = _storedSettings.ViewportLiveLinkPort,
            ShadowTextureSize = _storedSettings.ViewportShadowTextureSize,
            MaxTextureSize = _storedSettings.ViewportMaxTextureSize,
            RenderMode = _storedSettings.ViewportRenderMode
        };

        // Create the docks (top-right hosts the CS2 console)
        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Creating campaths dock...", "Preparing campath profiles, groups, and editor integration.", 4);
        }

        var bottomRight = new CampathsDockViewModel { Id = "BottomRight", Title = "Campaths" };

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Creating radar dock...", "Initializing radar state and player tracking models.", 5);
        }

        var topLeft = new RadarDockViewModel(_gsiServer, _radarConfigProvider, radarSettings, bottomRight, _webSocketClient) { Id = "TopLeft", Title = "Radar" };

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Creating video display dock...", "Preparing the video stream and overlay host view models.", 6);
        }

        _videoDisplayVm = new VideoDisplayDockViewModel { Id = "TopCenter", Title = "Video Stream" };

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Creating console and graphics docks...", "Preparing console integration and graphics control models.", 7);
        }

        var topRight = new NetConsoleDockViewModel(_gsiServer, _settingsStorage, _storedSettings) { Id = "TopRight", Title = "Console" };
        _graphicsDockVm = new GraphicsDockViewModel(_graphicsService)
        {
            Id = "Graphics",
            Title = "Graphics"
        };
        _replayDockVm = new ReplayDockViewModel(_vmixReplayCoordinator)
        {
            Id = "Replay",
            Title = "Replay"
        };

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Creating settings dock...", "Loading configuration editors, hotkeys, and attach preset tools.", 8);
        }

        var bottomLeft = new SettingsDockViewModel(
            radarSettings,
            hudSettings,
            freecamSettings,
            viewport3DSettings,
            _settingsStorage,
            _webSocketClient,
            _hotkeyService,
            bottomRight,
            _graphicsDockVm,
            ApplyNetworkSettingsAsync,
            _storedSettings,
            _vmixSettings,
            _vmixReplaySettings,
            _replayDirectorSettings,
            _vmixApiClient,
            setFocusInputGateDisabled: disable => _rawInputHandler.CaptureOnlyWhenAppFocused = !disable,
            campathEditor: campathEditor,
            gsiServer: _gsiServer,
            inputSender: _inputSender,
            videoDisplayDockViewModel: _videoDisplayVm,
            graphicsProducerClient: _producerClient)
        { Id = "BottomLeft", Title = "Settings" };

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Creating 3D viewport dock...", "Preparing viewport state, freecam integration, and campath editing.", 9);
        }

        var bottomCenter = new Viewport3DDockViewModel(viewport3DSettings, freecamSettings, campathEditor, _webSocketClient, _videoDisplayVm, _gsiServer, _liveLinkReceiver) { Id = "BottomCenter", Title = "3D Viewport" };
        var sequence = new CampathSequenceViewModel(
            campathEditor, bottomLeft.DefaultCampathInterpMode);
        var sequencer = new CampathSequencerDockViewModel(sequence);
        bottomLeft.SetCampathSequence(sequence);
        var curveEditor = new CurveEditorDockViewModel(campathEditor, bottomCenter);
        curveEditor.SetSequence(sequence);
        bottomCenter.SetCurveEditor(curveEditor);
        bottomCenter.SetSequence(sequence);
        bottomCenter.SetInputSender(_inputSender);

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Wiring services and hotkeys...", "Connecting dock models to shared services, input, and overlay state.", 10);
        }

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
        _hotkeyService.RegisterCommandContext(sequencer);
        _hotkeyService.RegisterCommandContext(_videoDisplayVm);
        _hotkeyService.RegisterCommandContext(campathEditor);
        _hotkeyService.RegisterCommandContext(hudOverlayVm);
        _hotkeyService.RegisterCommandContext(_graphicsDockVm);
        _hotkeyService.RegisterCommandContext(_replayDockVm);
        _hotkeyService.RegisterCommandContext(bottomLeft.AttachPresetAnimationEditor);
        ConfigureAnalogInput(freecamSettings);
        _videoDisplayVm.SetRtpConfig(new RtpReceiverConfig
        {
            Address = "0.0.0.0",
            Port = _storedSettings.RtpPort
        });
        // Start GSI listener on all interfaces with configured port
        _gsiServer.Start(_storedSettings.GsiPort, "/gsi/");
        bottomRight.SetWebSocketClient(_webSocketClient);

        if (reportProgressAsync != null)
        {
            await reportProgressAsync("Building workspace layout...", "Creating dock containers, rows, and the root workspace shell.", 11);
        }

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

        // Top-right: Console + Graphics
        var topRightDock = new ToolDock
        {
            Id = "TopRightDock",
            Proportion = 0.2,
            ActiveDockable = topRight,
            VisibleDockables = CreateList<IDockable>(topRight, _graphicsDockVm, _replayDockVm)
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
            VisibleDockables = CreateList<IDockable>(bottomCenter, sequencer, curveEditor)
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
                hostWindow.SetHotkeyOverlaySource(_hotkeyService);
                return hostWindow;
            }
        };

        base.InitLayout(layout);
    }

    public void SetKeyboardSuppression(bool suppress)
    {
        KeyboardInputGate.SetFocusSuppression(suppress);
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
        if (key == FormsKeys.C)
            RequestFreecamHold();
    }

    private void OnRawInputMiddleMousePressed(object? sender, EventArgs e)
    {
        RequestFreecamHold();
    }

    private void RequestFreecamHold()
    {
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

        // Stop shared texture renderer before service teardown.
        if (_videoDisplayVm != null)
        {
            try
            {
                _videoDisplayVm.UseD3DHost = false;
            }
            catch
            {
                // Ignore shutdown ordering races.
            }
            _videoDisplayVm.Dispose();
            _videoDisplayVm = null;
        }

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
        _rawInputHandler.MiddleMousePressed -= OnRawInputMiddleMousePressed;
        _rawInputHandler.KeyStateChanged -= OnRawInputKeyStateChanged;
        KeyboardInputGate.SetSuppressionSink(null);
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
        _producerClient.Dispose();
        _liveLinkReceiver.Dispose();
        _vmixReplayService.Dispose();
        _replayDirectorFollower.Dispose();
        _replayDirectorPublisher.Dispose();
        _delayedReplayMarker.Dispose();
        _vmixApiClient.Dispose();
        _replayDockVm?.Dispose();
        _graphicsDockVm?.Dispose();
        _graphicsService.Dispose();

        _disposed = true;
    }
}
