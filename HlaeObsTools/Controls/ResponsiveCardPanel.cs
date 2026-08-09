using System;
using Avalonia;
using Avalonia.Controls;

namespace HlaeObsTools.Controls;

public sealed class ResponsiveCardPanel : Panel
{
    public static readonly StyledProperty<double> MinimumCardWidthProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(MinimumCardWidth), 250d);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ResponsiveCardPanel, double>(nameof(Spacing), 8d);

    static ResponsiveCardPanel()
    {
        AffectsMeasure<ResponsiveCardPanel>(MinimumCardWidthProperty, SpacingProperty);
        AffectsArrange<ResponsiveCardPanel>(MinimumCardWidthProperty, SpacingProperty);
    }

    public double MinimumCardWidth
    {
        get => GetValue(MinimumCardWidthProperty);
        set => SetValue(MinimumCardWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? MinimumCardWidth
            : Math.Max(0, availableSize.Width);
        var columns = GetColumns(width);
        var cardWidth = GetCardWidth(width, columns);
        var totalHeight = 0d;

        for (var index = 0; index < Children.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < Children.Count; column++)
            {
                var child = Children[index + column];
                child.Measure(new Size(cardWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }

            if (index > 0)
                totalHeight += Spacing;
            totalHeight += rowHeight;
        }

        return new Size(width, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = Math.Max(0, finalSize.Width);
        var columns = GetColumns(width);
        var cardWidth = GetCardWidth(width, columns);
        var y = 0d;

        for (var index = 0; index < Children.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < Children.Count; column++)
                rowHeight = Math.Max(rowHeight, Children[index + column].DesiredSize.Height);

            for (var column = 0; column < columns && index + column < Children.Count; column++)
            {
                var x = column * (cardWidth + Spacing);
                Children[index + column].Arrange(new Rect(x, y, cardWidth, rowHeight));
            }

            y += rowHeight + Spacing;
        }

        return finalSize;
    }

    private int GetColumns(double availableWidth)
    {
        return Math.Max(1, (int)Math.Floor((availableWidth + Spacing) / (MinimumCardWidth + Spacing)));
    }

    private double GetCardWidth(double availableWidth, int columns)
    {
        return Math.Max(0, (availableWidth - (columns - 1) * Spacing) / columns);
    }
}
