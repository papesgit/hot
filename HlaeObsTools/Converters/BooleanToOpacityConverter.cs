using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public class BooleanToOpacityConverter : IValueConverter
{
    public double TrueOpacity { get; set; } = 1.0;
    public double FalseOpacity { get; set; } = 0.5;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b)
        {
            return b ? TrueOpacity : FalseOpacity;
        }

        return FalseOpacity;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double d)
        {
            return Math.Abs(d - TrueOpacity) < Math.Abs(d - FalseOpacity);
        }

        return null;
    }
}
