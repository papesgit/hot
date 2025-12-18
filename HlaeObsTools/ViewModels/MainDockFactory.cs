using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.ViewModels;
using HlaeObsTools.Services.Settings;

namespace HlaeObsTools.ViewModels;

public class MainDockFactory : Factory, IDisposable
{
    private readonly object _context;
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly HlaeInputSender _inputSender;
    private readonly RawInputHandler _rawInputHandler;
    private readonly Timer _inputFlushTimer;
    private readonly GsiServer _gsiServer;
    private readonly RadarConfigProvider _radarConfigProvider;
    private readonly SettingsStorage _settingsStorage;
    private readonly AppSettingsData _storedSettings;
    private VideoDisplayDockViewModel? _videoDisplayVm;
    private bool _disposed;

    public MainDockFactory(object context)
    {
        _context = context;

        _settingsStorage = new SettingsStorage();
        _storedSettings = _settingsStorage.Load();

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

        // Initialize global raw input handler and periodically flush into UDP sender
        _rawInputHandler = new RawInputHandler();
        _rawInputHandler.SetInputSender(_inputSender);
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
            MarkerScale = _storedSettings.MarkerScale,
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds
        };
        var hudSettings = new HudSettings
        {
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds
        };
        hudSettings.ApplyAttachPresets(_storedSettings.AttachPresets);
        var freecamSettings = new FreecamSettings();
        var viewport3DSettings = new Viewport3DSettings
        {
            MapObjPath = _storedSettings.MapObjPath ?? string.Empty,
            UseAltPlayerBinds = _storedSettings.UseAltPlayerBinds,
            PinScale = (float)_storedSettings.PinScale,
            WorldScale = (float)_storedSettings.WorldScale,
            WorldYaw = (float)_storedSettings.WorldYaw,
            WorldPitch = (float)_storedSettings.WorldPitch,
            WorldRoll = (float)_storedSettings.WorldRoll,
            WorldOffsetX = (float)_storedSettings.WorldOffsetX,
            WorldOffsetY = (float)_storedSettings.WorldOffsetY,
            WorldOffsetZ = (float)_storedSettings.WorldOffsetZ,
            MapScale = (float)_storedSettings.MapScale,
            MapYaw = (float)_storedSettings.MapYaw,
            MapPitch = (float)_storedSettings.MapPitch,
            MapRoll = (float)_storedSettings.MapRoll,
            MapOffsetX = (float)_storedSettings.MapOffsetX,
            MapOffsetY = (float)_storedSettings.MapOffsetY,
            MapOffsetZ = (float)_storedSettings.MapOffsetZ
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
            ApplyNetworkSettingsAsync,
            _storedSettings)
        { Id = "BottomLeft", Title = "Settings" };
        var bottomCenter = new Viewport3DDockViewModel(viewport3DSettings, _gsiServer) { Id = "BottomCenter", Title = "3D Viewport" };

        // Inject WebSocket and UDP services into video display
        _videoDisplayVm.SetWebSocketClient(_webSocketClient);
        _videoDisplayVm.SetInputSender(_inputSender);
        _videoDisplayVm.SetFreecamSettings(freecamSettings);
        _videoDisplayVm.SetHudSettings(hudSettings);
        _videoDisplayVm.SetGsiServer(_gsiServer);
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
                return hostWindow;
            }
        };

        base.InitLayout(layout);
    }

    public void SetKeyboardSuppression(bool suppress)
    {
        _rawInputHandler.SuppressKeyboard = suppress;
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

        _rawInputHandler.Dispose();
        _inputSender.Dispose();

        _gsiServer.Dispose();
        _webSocketClient.MessageReceived -= OnHlaeMessage;
        _webSocketClient.Dispose();

        _videoDisplayVm?.Dispose();

        _disposed = true;
    }
}
