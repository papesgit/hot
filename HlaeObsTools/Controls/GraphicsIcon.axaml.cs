using Avalonia;
using Avalonia.Controls;

namespace HlaeObsTools.Controls;

public partial class GraphicsIcon : UserControl
{
    public static readonly StyledProperty<string?> IconPathProperty =
        AvaloniaProperty.Register<GraphicsIcon, string?>(nameof(IconPath));

    public static readonly StyledProperty<string?> SvgCssProperty =
        AvaloniaProperty.Register<GraphicsIcon, string?>(nameof(SvgCss));

    public GraphicsIcon()
    {
        InitializeComponent();
    }

    public string? IconPath
    {
        get => GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value);
    }

    public string? SvgCss
    {
        get => GetValue(SvgCssProperty);
        set => SetValue(SvgCssProperty, value);
    }
}
