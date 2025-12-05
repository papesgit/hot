using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

/// <summary>
/// Converts a normalized position (0..1) and an actual size into a canvas coordinate, centering a marker.
/// MultiBinding: [relative, actualSize], ConverterParameter = markerSize (optional, default 16).
/// </summary>
public class RelativeToCanvasConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not double relative || values[1] is not double size || double.IsNaN(size))
            return 0d;

        var markerSize = 16d;
        if (parameter is double d) markerSize = d;
        else if (parameter is string s && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            markerSize = parsed;

        var coord = relative * size - markerSize / 2d;
        if (double.IsNaN(coord) || double.IsInfinity(coord)) return 0d;
        return coord;
    }

    public object? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
