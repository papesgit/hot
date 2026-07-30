using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

/// <summary>Returns a scaled card width that never exceeds its available viewport.</summary>
public sealed class ClampedScaleToWidthConverter : IMultiValueConverter
{
    public double BaseWidth { get; set; } = 260;
    public double MinimumWidth { get; set; } = 100;
    public double HorizontalMargins { get; set; } = 12;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var scale = values.Count > 0 && values[0] is double value ? value : 1.0;
        var available = values.Count > 1 && values[1] is double width ? width : double.PositiveInfinity;
        var maximum = Math.Max(MinimumWidth, available - HorizontalMargins);
        return Math.Min(BaseWidth * scale, maximum);
    }
}
