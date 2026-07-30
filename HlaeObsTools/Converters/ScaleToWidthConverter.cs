using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public class ScaleToWidthConverter : IValueConverter
{
    public double BaseWidth { get; set; } = 260;
    public double MinimumWidth { get; set; }
    public double MinimumScale { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double scale)
        {
            if (MinimumWidth > 0 && MinimumScale > 0)
                return MinimumWidth + Math.Max(0, scale - MinimumScale) * BaseWidth;

            return Math.Max(MinimumWidth, BaseWidth * scale);
        }

        return MinimumScale > 0 ? MinimumWidth + Math.Max(0, 1 - MinimumScale) * BaseWidth : Math.Max(MinimumWidth, BaseWidth);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
