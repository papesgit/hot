using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Xml;
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

public sealed record CampathFileTrack(string Id, string Name, CampathFile Campath);

public sealed class CampathFileSet
{
    public CampathFileSet(IReadOnlyList<CampathFileTrack> tracks, double startTime, double endTime)
    {
        Tracks = tracks;
        StartTime = startTime;
        EndTime = Math.Max(startTime, endTime);
    }

    public IReadOnlyList<CampathFileTrack> Tracks { get; }
    public double StartTime { get; }
    public double EndTime { get; }
    public double Duration => EndTime - StartTime;
}

/// <summary>
/// Lightweight parser for .campath files used to render paths on the radar.
/// </summary>
public static class CampathFileParser
{
    public const long MaxInspectionFileSizeBytes = 8 * 1024 * 1024;

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
            if (reader.NodeType != XmlNodeType.Element
                || (!reader.Name.Equals("campath", StringComparison.Ordinal)
                    && !reader.Name.Equals("campathSequence", StringComparison.Ordinal)))
                return false;

            if (reader.IsEmptyElement)
                return false;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                if (reader.Name.Equals("points", StringComparison.Ordinal)
                    || reader.Name.Equals("curveEditor", StringComparison.Ordinal))
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
        return ParseSet(path)?.Tracks.FirstOrDefault()?.Campath;
    }

    public static CampathFileSet? ParseSet(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            var sequence = CampathFileIo.LoadSequence(path);
            if (sequence == null)
                return null;

            var tracks = new List<CampathFileTrack>();
            foreach (var camera in sequence.Cameras)
            {
                var parsed = ParseCamera(camera.Campath);
                if (parsed != null)
                    tracks.Add(new CampathFileTrack(camera.Id, camera.Name, parsed));
            }
            if (tracks.Count == 0)
                return null;

            double start;
            double end;
            if (sequence.CameraCuts.Count > 0)
            {
                start = sequence.TimeOffset + sequence.CameraCuts.Min(cut => cut.StartTime);
                end = sequence.TimeOffset + sequence.CameraCuts.Max(cut => cut.EndTime);
            }
            else
            {
                start = tracks.SelectMany(track => track.Campath.Points).Min(point => point.Time);
                end = tracks.SelectMany(track => track.Campath.Points).Max(point => point.Time);
            }
            return new CampathFileSet(tracks, start, end);
        }
        catch
        {
            return null;
        }
    }

    private static CampathFile? ParseCamera(CampathFileIo.CampathFileData data)
    {
        if (data.PathModel == CameraPathModel.Classic)
        {
            var classicPoints = data.Keyframes
                .OrderBy(key => key.Time)
                .Select(key => new CampathPoint
                {
                    Time = key.Time + data.TimeOffset,
                    Position = new Vec3(key.Position.X, key.Position.Y, key.Position.Z),
                    Forward = RotateForward(key.Rotation)
                })
                .ToList();
            return classicPoints.Count == 0
                ? null
                : new CampathFile(classicPoints,
                    data.ClassicInterpolation == ClassicCampathInterpolation.Linear);
        }

        var document = data.CurveDocument;
        if (document?.CanEvaluateCamera != true)
            return null;
        var times = document.GetCameraKeyTimes();
        if (times.Count == 0)
            return null;

        var start = times[0];
        var end = times[^1];
        var duration = Math.Max(0.0, end - start);
        var sampleCount = duration <= 0.0
            ? 0
            : Math.Clamp((int)Math.Ceiling(duration * 30.0), 32, 512);
        var curvePoints = new List<CampathPoint>(sampleCount + 1);
        for (var i = 0; i <= sampleCount; i++)
        {
            var time = sampleCount == 0 ? start : start + duration * i / sampleCount;
            var sample = document.Evaluate(time);
            curvePoints.Add(new CampathPoint
            {
                Time = time + data.TimeOffset,
                Position = new Vec3(sample.Position.X, sample.Position.Y, sample.Position.Z),
                Forward = RotateForward(sample.Rotation)
            });
        }

        // These points already sample the authored curves; the radar must connect
        // them directly instead of applying a second Catmull-Rom interpolation.
        return new CampathFile(curvePoints, isLinearPosition: true);
    }

    private static Vector3 RotateForward(in Quaternion rotation)
    {
        var forward = Vector3.Transform(Vector3.UnitX, rotation);
        if (forward == Vector3.Zero)
            forward = Vector3.UnitX;
        return forward;
    }
}
