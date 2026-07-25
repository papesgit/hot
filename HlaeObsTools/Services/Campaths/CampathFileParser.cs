using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Xml;
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
    public const long MaxInspectionFileSizeBytes = 256 * 1024;

    public static bool LooksLikeCampath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxInspectionFileSizeBytes)
                return false;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };

            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, settings);

            if (!reader.Read())
                return false;

            reader.MoveToContent();
            if (reader.NodeType != XmlNodeType.Element || !reader.Name.Equals("campath", StringComparison.Ordinal))
                return false;

            if (reader.IsEmptyElement)
                return false;

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element
                    && (reader.Name.Equals("points", StringComparison.Ordinal)
                        || reader.Name.Equals("curveEditor", StringComparison.Ordinal)))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

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

            if (string.Equals(root.Attribute("model")?.Value, "curves", StringComparison.OrdinalIgnoreCase))
                return ParseCurves(path);

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

    private static CampathFile? ParseCurves(string path)
    {
        var data = CampathFileIo.Load(path);
        var document = data?.CurveDocument;
        if (document?.CanEvaluateCamera != true)
            return null;

        var times = document.GetCameraKeyTimes();
        if (times.Count == 0)
            return null;

        var start = times[0];
        var end = times[^1];
        var duration = Math.Max(0.0, end - start);
        var sampleCount = duration <= 0.0
            ? 1
            : Math.Clamp((int)Math.Ceiling(duration * 30.0), 32, 512);
        var points = new List<CampathPoint>(sampleCount + 1);
        for (var i = 0; i <= sampleCount; i++)
        {
            var time = sampleCount == 0 ? start : start + duration * i / sampleCount;
            var sample = document.Evaluate(time);
            points.Add(new CampathPoint
            {
                Time = time + data!.TimeOffset,
                Position = new Vec3(sample.Position.X, sample.Position.Y, sample.Position.Z),
                Forward = RotateForward(sample.Rotation)
            });
        }

        // These points already sample the authored curves; the radar must connect
        // them directly instead of applying a second Catmull-Rom interpolation.
        return new CampathFile(points, isLinearPosition: true);
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
