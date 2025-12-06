using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Xml.Linq;
using HlaeObsTools.Services.Gsi;

namespace HlaeObsTools.Services.Campaths;

public sealed class CampathPoint
{
    public double Time { get; init; }
    public Vec3 Position { get; init; }
    public Vector3 Forward { get; init; }
}

public sealed class CampathFile
{
    public CampathFile(IReadOnlyList<CampathPoint> points, bool isLinearPosition)
    {
        Points = points;
        IsLinearPosition = isLinearPosition;
    }

    public IReadOnlyList<CampathPoint> Points { get; }
    public bool IsLinearPosition { get; }
}

/// <summary>
/// Lightweight parser for .campath files used to render paths on the radar.
/// </summary>
public static class CampathFileParser
{
    public static CampathFile? Parse(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var doc = XDocument.Load(path);
            var root = doc.Element("campath");
            if (root == null)
                return null;

            var positionInterp = root.Attribute("positionInterp")?.Value;
            bool isLinearPosition = string.Equals(positionInterp, "linear", StringComparison.OrdinalIgnoreCase);

            var pointsElement = root.Element("points");
            if (pointsElement == null)
                return null;

            var points = new List<CampathPoint>();
            foreach (var p in pointsElement.Elements("p"))
            {
                var time = ParseDouble(p.Attribute("t")?.Value);

                var pos = new Vec3(
                    ParseDouble(p.Attribute("x")?.Value),
                    ParseDouble(p.Attribute("y")?.Value),
                    ParseDouble(p.Attribute("z")?.Value));

                var forward = TryParseQuaternion(p, out var q)
                    ? RotateForward(q)
                    : RotateForward(FromEuler(p));

                points.Add(new CampathPoint
                {
                    Time = time,
                    Position = pos,
                    Forward = forward
                });
            }

            if (points.Count == 0)
                return null;

            return new CampathFile(points, isLinearPosition);
        }
        catch
        {
            return null;
        }
    }

    private static double ParseDouble(string? value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0.0;
    }

    private static bool TryParseQuaternion(XElement p, out Quaternion quaternion)
    {
        quaternion = default;
        if (p.Attribute("qw") == null || p.Attribute("qx") == null || p.Attribute("qy") == null || p.Attribute("qz") == null)
            return false;

        quaternion = new Quaternion(
            (float)ParseDouble(p.Attribute("qx")!.Value),
            (float)ParseDouble(p.Attribute("qy")!.Value),
            (float)ParseDouble(p.Attribute("qz")!.Value),
            (float)ParseDouble(p.Attribute("qw")!.Value));

        quaternion = Quaternion.Normalize(quaternion);
        return true;
    }

    private static Quaternion FromEuler(XElement p)
    {
        // Quake coords: roll (x), pitch (y), yaw (z), applied in order rx -> ry -> rz
        var rx = DegreesToRadians(ParseDouble(p.Attribute("rx")?.Value));
        var ry = DegreesToRadians(ParseDouble(p.Attribute("ry")?.Value));
        var rz = DegreesToRadians(ParseDouble(p.Attribute("rz")?.Value));

        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)rx);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)ry);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)rz);

        var combined = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Multiply(qz, qy), qx));
        return combined;
    }

    private static Vector3 RotateForward(in Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitX, rotation);
        if (forward == Vector3.Zero)
            forward = Vector3.UnitX;
        return forward;
    }

    private static double DegreesToRadians(double deg) => deg * Math.PI / 180.0;
}
