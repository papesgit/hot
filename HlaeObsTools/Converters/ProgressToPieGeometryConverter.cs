using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace HlaeObsTools.Converters;

public sealed class ProgressToPieGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double d ? d : 0;
        progress = Math.Clamp(progress, 0, 1);
        if (progress <= 0)
        {
            return null;
        }

        var radius = 40.0;
        if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            radius = parsed;
        }

        if (progress >= 1)
        {
            return new EllipseGeometry(new Rect(0, 0, radius * 2, radius * 2));
        }

        var center = new Point(radius, radius);
        var startAngle = -90.0;
        var sweepAngle = progress * 360.0;
        var endAngle = startAngle - sweepAngle;

        var startRadians = startAngle * Math.PI / 180.0;
        var endRadians = endAngle * Math.PI / 180.0;

        var startPoint = new Point(
            center.X + radius * Math.Cos(startRadians),
            center.Y + radius * Math.Sin(startRadians));
        var endPoint = new Point(
            center.X + radius * Math.Cos(endRadians),
            center.Y + radius * Math.Sin(endRadians));

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(center, isFilled: true);
            ctx.LineTo(startPoint);
            ctx.ArcTo(endPoint, new Size(radius, radius), rotationAngle: 0,
                isLargeArc: sweepAngle > 180.0, SweepDirection.CounterClockwise);
            ctx.LineTo(center);
            ctx.EndFigure(isClosed: true);
        }

        return geom;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return null;
    }
}
