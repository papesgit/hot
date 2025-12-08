using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

/// <summary>
/// Calculates a uniform HUD scale so the overlay fits inside the available area.
/// </summary>
public sealed class HudScaleConverter : IMultiValueConverter
{
    public double BaseWidth { get; set; } = 1600;
    public double BaseHeight { get; set; } = 900;
    public double MinScale { get; set; } = 0.0;
    public double MaxScale { get; set; } = 1.0;

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double width || values[1] is not double height)
        {
            return 1.0;
        }

        if (BaseWidth <= 0 || BaseHeight <= 0)
        {
            return 1.0;
        }

        var scale = Math.Min(width / BaseWidth, height / BaseHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return 1.0;
        }

        scale = Math.Clamp(scale, MinScale, MaxScale);
        return scale;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
