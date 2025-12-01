using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using HlaeObsTools.ViewModels.Docks;
using System;
using System.Collections.Generic;

namespace HlaeObsTools.ViewModels;

public class MainDockFactory : Factory
{
    private readonly object _context;

    public MainDockFactory(object context)
    {
        _context = context;
    }

    public override IDocumentDock CreateDocumentDock() => new DocumentDock();
    public override IToolDock CreateToolDock() => new ToolDock();
    public override IProportionalDock CreateProportionalDock() => new ProportionalDock();

    public override IRootDock CreateLayout()
    {
        // Create the 5 placeholder docks
        var topLeft = new PlaceholderDockViewModel { Id = "TopLeft", Title = "Controls" };
        var topCenter = new VideoDisplayDockViewModel { Id = "TopCenter", Title = "Video Stream" };
        var topRight = new PlaceholderDockViewModel { Id = "TopRight", Title = "Timeline" };
        var bottomLeft = new PlaceholderDockViewModel { Id = "BottomLeft", Title = "Events" };
        var bottomRight = new PlaceholderDockViewModel { Id = "BottomRight", Title = "Settings" };

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
            ActiveDockable = topCenter,
            VisibleDockables = CreateList<IDockable>(topCenter)
        };

        // Top-right: Timeline - remaining space
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

        // Create top row (3 docks) with splitters between each dock
        var topRow = new ProportionalDock
        {
            Id = "TopRow",
            Proportion = double.NaN,
            Orientation = Orientation.Horizontal,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>
            (
                topLeftDock,
                new ProportionalDockSplitter { Id = "TopSplitter1" },
                topCenterDock,
                new ProportionalDockSplitter { Id = "TopSplitter2" },
                topRightDock
            )
        };

        // Create bottom row (2 docks) with splitter between them
        var bottomRow = new ProportionalDock
        {
            Id = "BottomRow",
            Proportion = double.NaN,
            Orientation = Orientation.Horizontal,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>
            (
                bottomLeftDock,
                new ProportionalDockSplitter { Id = "BottomSplitter1" },
                bottomRightDock
            )
        };

        // Create main layout (top and bottom rows)
        var mainLayout = new ProportionalDock
        {
            Id = "MainLayout",
            Proportion = double.NaN,
            Orientation = Orientation.Vertical,
            ActiveDockable = null,
            VisibleDockables = CreateList<IDockable>
            (
                topRow,
                new ProportionalDockSplitter
                {
                    Id = "MainSplitter",
                    Title = "MainSplitter"
                },
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
        };

        base.InitLayout(layout);
    }
}
