using Avalonia;
using Avalonia.Media;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.ViewModels.Hud;

public sealed class HudPlayerActionOption : ViewModelBase
{
    private readonly int _index;
    private readonly string _id;
    private readonly string _displayName;
    private readonly string? _description;
    private Geometry? _sliceGeometry;
    private Point _labelPosition;
    private bool _isHighlighted;
    private IBrush _accentBrush = Brushes.White;
    private IBrush _fillBrush = new SolidColorBrush(Color.FromArgb(180, 30, 30, 35));
    private bool _hasSubMenu;

    public HudPlayerActionOption(string id, string displayName, int index, string? description = null, bool hasSubMenu = false)
    {
        _id = id;
        _displayName = displayName;
        _index = index;
        _description = description;
        _hasSubMenu = hasSubMenu;
    }

    public int Index => _index;

    public string Id => _id;

    public string DisplayName => _displayName;

    public string? Description => _description;

    public bool HasSubMenu
    {
        get => _hasSubMenu;
        set => SetProperty(ref _hasSubMenu, value);
    }

    public Geometry? SliceGeometry
    {
        get => _sliceGeometry;
        private set => SetProperty(ref _sliceGeometry, value);
    }

    public Point LabelPosition
    {
        get => _labelPosition;
        private set => SetProperty(ref _labelPosition, value);
    }

    public bool IsHighlighted
    {
        get => _isHighlighted;
        private set
        {
            if (SetProperty(ref _isHighlighted, value))
            {
                UpdateFillBrush();
            }
        }
    }

    public IBrush FillBrush
    {
        get => _fillBrush;
        private set => SetProperty(ref _fillBrush, value);
    }

    public void SetLayout(Geometry geometry, Point labelPosition)
    {
        SliceGeometry = geometry;
        LabelPosition = labelPosition;
    }

    public void SetAccentBrush(IBrush accent)
    {
        _accentBrush = accent;
        UpdateFillBrush();
    }

    public void SetHighlighted(bool isHighlighted)
    {
        IsHighlighted = isHighlighted;
    }

    private void UpdateFillBrush()
    {
        var accentColor = (_accentBrush as ISolidColorBrush)?.Color ?? Colors.White;
        if (IsHighlighted)
        {
            FillBrush = new SolidColorBrush(Color.FromArgb(220, accentColor.R, accentColor.G, accentColor.B));
        }
        else
        {
            FillBrush = new SolidColorBrush(Color.FromArgb(180, 30, 30, 35));
        }
    }
}
