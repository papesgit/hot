using System;
using System.Globalization;
using System.Collections.Generic;
using Avalonia.Data.Converters;

namespace HlaeObsTools.Converters;

public sealed class CenterOnPointConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
            return 0d;

        var pointValue = values[0];
        var sizeValue = values[1];

        if (pointValue is not double point || sizeValue is not double size)
            return pointValue ?? 0d;

        return point - (size * 0.5);
    }
}
