using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

/// <summary>Scales a badge with the actual card width while preserving a usable compact-card minimum.</summary>
public sealed class CardWidthToBadgeSizeConverter : IValueConverter
{
    public double MinimumCardWidth { get; set; } = 110;
    public double MinimumSize { get; set; } = 28;
    public double SizePerCardWidth { get; set; } = 0.1111111111;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var cardWidth = value is double width ? width : MinimumCardWidth;
        return MinimumSize + Math.Max(0, cardWidth - MinimumCardWidth) * SizePerCardWidth;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
