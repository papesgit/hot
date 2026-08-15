using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HlaeObsTools.Converters;

public sealed class CueTeamBrushConverter : IValueConverter
{
    private static readonly IBrush Ct = new SolidColorBrush(Color.Parse("#4DB3FF"));
    private static readonly IBrush T = new SolidColorBrush(Color.Parse("#FF9340"));
    private static readonly IBrush Unknown = new SolidColorBrush(Color.Parse("#C8CDD5"));
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value as string, "CT", StringComparison.OrdinalIgnoreCase) ? Ct :
        string.Equals(value as string, "T", StringComparison.OrdinalIgnoreCase) ? T : Unknown;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
