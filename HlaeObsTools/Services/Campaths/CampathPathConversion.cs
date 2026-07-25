using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HlaeObsTools.Services.Campaths;

public enum CameraPathModel
{
    Classic,
    Curves
}

public enum ClassicCampathInterpolation
{
    Linear,
    CatmullRom
}

public enum CampathEditorMode
{
    Linear,
    CatmullRom,
    Curves
}

public static class CampathPathConversion
{
    public static readonly (string Id, string Name, string Group, string Color)[] StandardChannels =
    [
        ("position.x", "X", "Position", "#F05A5A"), ("position.y", "Y", "Position", "#62C96B"),
        ("position.z", "Z", "Position", "#5C8FF0"), ("rotation.pitch", "Pitch", "Rotation", "#E68A45"),
        ("rotation.yaw", "Yaw", "Rotation", "#AF6BE8"), ("rotation.roll", "Roll", "Rotation", "#47C6CE"),
        ("fov", "FOV", "Camera", "#F1D65C"), ("dof.enabled", "Enabled", "DOF", "#F18AB8"),
        ("dof.nearBlurry", "Near blurry", "DOF", "#EF6AA8"),
        ("dof.nearCrisp", "Near crisp", "DOF", "#D981B5"), ("dof.farCrisp", "Far crisp", "DOF", "#67B7E8"),
        ("dof.farBlurry", "Far blurry", "DOF", "#438BC7"), ("dof.maxBlur", "Max blur", "DOF", "#C9A65C"),
        ("dof.radiusScale", "Radius scale", "DOF", "#8FCB71")
    ];

    public static void EnsureStandardChannels(CampathCurveDocument document)
    {
        foreach (var definition in StandardChannels)
        {
            if (document.Find(definition.Id) == null)
            {
                document.Channels.Add(new CampathCurveChannel
                {
                    Id = definition.Id,
                    Name = definition.Name,
                    Group = definition.Group,
                    Color = definition.Color
                });
            }
        }
    }

    public static void ClassicToCurves(IEnumerable<CampathKeyframe> keyframes,
        ClassicCampathInterpolation interpolation, CampathCurveDocument document)
    {
        EnsureStandardChannels(document);
        foreach (var channel in document.Channels)
            channel.Keys.Clear();

        var curveInterpolation = interpolation == ClassicCampathInterpolation.Linear
            ? CurveInterpolationMode.Linear
            : CurveInterpolationMode.Bezier;

        foreach (var key in keyframes.OrderBy(key => key.Time))
        {
            var (pitch, yaw, roll) = QuaternionToEuler(key.Rotation);
            Add(document, "position.x", key.Time, key.Position.X, curveInterpolation);
            Add(document, "position.y", key.Time, key.Position.Y, curveInterpolation);
            Add(document, "position.z", key.Time, key.Position.Z, curveInterpolation);
            Add(document, "rotation.pitch", key.Time, pitch, curveInterpolation);
            Add(document, "rotation.yaw", key.Time, yaw, curveInterpolation);
            Add(document, "rotation.roll", key.Time, roll, curveInterpolation);
            Add(document, "fov", key.Time, key.Fov, curveInterpolation);
            Add(document, "dof.enabled", key.Time, key.Dof.Enabled ? 1 : 0, CurveInterpolationMode.Constant);
            Add(document, "dof.nearBlurry", key.Time, key.Dof.NearBlurry, curveInterpolation);
            Add(document, "dof.nearCrisp", key.Time, key.Dof.NearCrisp, curveInterpolation);
            Add(document, "dof.farCrisp", key.Time, key.Dof.FarCrisp, curveInterpolation);
            Add(document, "dof.farBlurry", key.Time, key.Dof.FarBlurry, curveInterpolation);
            Add(document, "dof.maxBlur", key.Time, key.Dof.MaxBlurSize, curveInterpolation);
            Add(document, "dof.radiusScale", key.Time, key.Dof.RadiusScale, curveInterpolation);
        }

        UnwrapAngles(document, "rotation.pitch");
        UnwrapAngles(document, "rotation.yaw");
        UnwrapAngles(document, "rotation.roll");
        if (interpolation == ClassicCampathInterpolation.CatmullRom)
            AutoTangents(document);
    }

    public static IReadOnlyList<CampathKeyframe> CurvesToClassic(CampathCurveDocument document)
    {
        if (!document.CanEvaluateCamera)
            return Array.Empty<CampathKeyframe>();

        // Every independent channel key time becomes a compound classic key. This
        // preserves all authored timing events while intentionally flattening the channels.
        var times = document.Channels.SelectMany(channel => channel.Keys)
            .Select(key => key.Time).Distinct().OrderBy(time => time).ToList();
        return times.Select(time =>
        {
            var sample = document.Evaluate(time);
            return new CampathKeyframe
            {
                Time = time,
                Position = sample.Position,
                Rotation = sample.Rotation,
                Fov = sample.Fov,
                Dof = sample.Dof
            };
        }).ToList();
    }

    public static void AutoTangents(CampathCurveDocument document)
    {
        foreach (var channel in document.Channels)
        {
            for (var i = 0; i < channel.Keys.Count; i++)
            {
                var key = channel.Keys[i];
                if (key.TangentMode != CurveTangentMode.Auto)
                    continue;

                var previous = channel.Keys[Math.Max(0, i - 1)];
                var next = channel.Keys[Math.Min(channel.Keys.Count - 1, i + 1)];
                var slope = Math.Abs(next.Time - previous.Time) < 1e-9
                    ? 0
                    : (next.Value - previous.Value) / (next.Time - previous.Time);
                key.InTangent = key.OutTangent = slope;
                key.InWeight = i > 0 ? Math.Max(0.001, (key.Time - channel.Keys[i - 1].Time) / 3.0) : 0.25;
                key.OutWeight = i + 1 < channel.Keys.Count
                    ? Math.Max(0.001, (channel.Keys[i + 1].Time - key.Time) / 3.0)
                    : 0.25;
            }
        }
    }

    private static void Add(CampathCurveDocument document, string id, double time, double value,
        CurveInterpolationMode interpolation)
    {
        document.Find(id)!.Keys.Add(new CampathCurveKey
        {
            Time = time,
            Value = value,
            Interpolation = interpolation
        });
    }

    private static void UnwrapAngles(CampathCurveDocument document, string id)
    {
        var keys = document.Find(id)?.Keys;
        if (keys == null)
            return;

        for (var i = 1; i < keys.Count; i++)
        {
            while (keys[i].Value - keys[i - 1].Value > 180) keys[i].Value -= 360;
            while (keys[i].Value - keys[i - 1].Value < -180) keys[i].Value += 360;
        }
    }

    private static (double Pitch, double Yaw, double Roll) QuaternionToEuler(Quaternion rotation)
    {
        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, rotation));
        var yaw = Math.Atan2(forward.Y, forward.X);
        var pitch = -Math.Asin(Math.Clamp(forward.Z, -1f, 1f));
        var right = new Vector3((float)Math.Sin(yaw), (float)-Math.Cos(yaw), 0);
        var baseUp = Vector3.Normalize(Vector3.Cross(right, forward));
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, rotation));
        var roll = Math.Atan2(Vector3.Dot(Vector3.Cross(baseUp, up), forward), Vector3.Dot(baseUp, up));
        const double radiansToDegrees = 180.0 / Math.PI;
        return (pitch * radiansToDegrees, yaw * radiansToDegrees, roll * radiansToDegrees);
    }
}
