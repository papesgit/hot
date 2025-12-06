using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Threading;
using HlaeObsTools.Services.Input;
using HlaeObsTools.Services.WebSocket;
using HlaeObsTools.ViewModels.Docks;
using HlaeObsTools.Services.Gsi;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels;

public class MainDockFactory : Factory
{
    private readonly object _context;
    private readonly HlaeWebSocketClient _webSocketClient;
    private readonly HlaeInputSender _inputSender;
    private readonly RawInputHandler _rawInputHandler;
    private readonly Timer _inputFlushTimer;
    private readonly GsiServer _gsiServer;
    private readonly RadarConfigProvider _radarConfigProvider;

    public MainDockFactory(object context)
    {
        _context = context;

        // Initialize WebSocket client (connect to HLAE on localhost)
        _webSocketClient = new HlaeWebSocketClient("127.0.0.1", 31338);
        _webSocketClient.MessageReceived += OnHlaeMessage;
        _ = _webSocketClient.ConnectAsync(); // Fire and forget

        // Initialize UDP input sender (send at 240Hz to HLAE)
        _inputSender = new HlaeInputSender("127.0.0.1", 31339);
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

    public override IDocumentDock CreateDocumentDock() => new DocumentDock();
    public override IToolDock CreateToolDock() => new ToolDock();
    public override IProportionalDock CreateProportionalDock() => new ProportionalDock();
    public override IProportionalDockSplitter CreateProportionalDockSplitter() => new ProportionalDockSplitter();

    public override IRootDock CreateLayout()
    {
        // Shared settings for radar customization
        var radarSettings = new RadarSettings();

        // Create the 5 docks (top-right hosts the CS2 console)
        var topLeft = new RadarDockViewModel(_gsiServer, _radarConfigProvider, radarSettings) { Id = "TopLeft", Title = "Radar" };
        var browserSource = new BrowserSourceDockViewModel { Id = "BrowserSource" };
        var topCenter = new VideoDisplayDockViewModel { Id = "TopCenter", Title = "Video Stream" };
        var topRight = new NetConsoleDockViewModel { Id = "TopRight", Title = "Console" };
        var bottomLeft = new SettingsDockViewModel(radarSettings) { Id = "BottomLeft", Title = "Settings" };
        var bottomRight = new CampathsDockViewModel { Id = "BottomRight", Title = "Campaths" };

        // Inject WebSocket and UDP services into video display
        topCenter.SetWebSocketClient(_webSocketClient);
        topCenter.SetInputSender(_inputSender);
        bottomRight.SetWebSocketClient(_webSocketClient);

        // Wrap tools in ToolDocks for proper docking behavior
        // Top-left: Controls - 1:1 aspect ratio (roughly square)
        var topLeftDock = new ToolDock
        {
            Id = "TopLeftDock",
            Proportion = 0.3,
            ActiveDockable = topLeft,
            VisibleDockables = CreateList<IDockable>(topLeft, browserSource)
        };

        // Top-center: Video Stream - 16:9 aspect ratio
        var topCenterDock = new ToolDock
        {
            Id = "TopCenterDock",
            Proportion = 0.5,
            ActiveDockable = topCenter,
            VisibleDockables = CreateList<IDockable>(topCenter)
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
            Proportion = 0.5,
            ActiveDockable = bottomLeft,
            VisibleDockables = CreateList<IDockable>(bottomLeft)
        };

        var bottomRightDock = new ToolDock
        {
            Id = "BottomRightDock",
            Proportion = 0.5,
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

        // Create bottom row (2 docks with splitter between them)
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
            [nameof(IDockWindow)] = () => new Views.DockHostWindow()
        };

        base.InitLayout(layout);
    }
}
