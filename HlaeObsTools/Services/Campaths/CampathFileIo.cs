using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Xml.Linq;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Campaths;

public static class CampathFileIo
{
    public sealed class CampathFileData
    {
        public bool Hold { get; set; }
        public CameraPathModel PathModel { get; set; } = CameraPathModel.Classic;
        public ClassicCampathInterpolation ClassicInterpolation { get; set; } = ClassicCampathInterpolation.CatmullRom;
        public double TimeOffset { get; set; }
        public List<CampathKeyframe> Keyframes { get; } = new();
        public CampathCurveDocument? CurveDocument { get; set; }
    }

    public static CampathFileData? Load(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Element("campath");
            if (root == null)
                return null;

            var data = new CampathFileData();
            data.PathModel = string.Equals(root.Attribute("model")?.Value, "curves", StringComparison.OrdinalIgnoreCase)
                ? CameraPathModel.Curves
                : CameraPathModel.Classic;

            var positionInterp = root.Attribute("positionInterp")?.Value;
            var rotationInterp = root.Attribute("rotationInterp")?.Value;
            var fovInterp = root.Attribute("fovInterp")?.Value;

            var anyLinear = string.Equals(positionInterp, "linear", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(rotationInterp, "sLinear", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(fovInterp, "linear", StringComparison.OrdinalIgnoreCase);
            data.ClassicInterpolation = anyLinear
                ? ClassicCampathInterpolation.Linear
                : ClassicCampathInterpolation.CatmullRom;

            data.Hold = root.Attribute("hold") != null;

            var points = root.Element("points");
            if (data.PathModel == CameraPathModel.Classic && points != null)
            {
                foreach (var p in points.Elements("p"))
                {
                    var time = ParseDouble(p.Attribute("t")?.Value);
                    var x = ParseDouble(p.Attribute("x")?.Value);
                    var y = ParseDouble(p.Attribute("y")?.Value);
                    var z = ParseDouble(p.Attribute("z")?.Value);
                    var fov = ParseDouble(p.Attribute("fov")?.Value, 90.0);

                    Quaternion rotation;
                    if (HasQuaternion(p))
                    {
                        var qw = ParseDouble(p.Attribute("qw")?.Value, 1.0);
                        var qx = ParseDouble(p.Attribute("qx")?.Value);
                        var qy = ParseDouble(p.Attribute("qy")?.Value);
                        var qz = ParseDouble(p.Attribute("qz")?.Value);
                        rotation = Quaternion.Normalize(new Quaternion((float)qx, (float)qy, (float)qz, (float)qw));
                    }
                    else
                    {
                        var roll = ParseDouble(p.Attribute("rx")?.Value);
                        var pitch = ParseDouble(p.Attribute("ry")?.Value);
                        var yaw = ParseDouble(p.Attribute("rz")?.Value);
                        rotation = EulerToQuaternion(pitch, yaw, roll);
                    }

                    var selected = p.Attribute("selected") != null;
                    var dof = new CampathDofSettings(
                        p.Attribute("dofEnabled") != null,
                        ParseDouble(p.Attribute("dofNearBlurry")?.Value, -100.0),
                        ParseDouble(p.Attribute("dofNearCrisp")?.Value, 0.0),
                        ParseDouble(p.Attribute("dofFarCrisp")?.Value, 180.0),
                        ParseDouble(p.Attribute("dofFarBlurry")?.Value, 2000.0),
                        ParseDouble(p.Attribute("dofMaxBlurSize")?.Value, 5.0),
                        ParseDouble(p.Attribute("dofRadiusScale")?.Value, 0.25));
                    data.Keyframes.Add(new CampathKeyframe
                    {
                        Time = time,
                        Position = new Vector3((float)x, (float)y, (float)z),
                        Rotation = rotation,
                        Fov = fov,
                        Selected = selected,
                        Dof = dof
                    });
                }
            }

            var curveEditor = root.Element("curveEditor");
            if (data.PathModel == CameraPathModel.Curves && curveEditor != null)
            {
                var curveDocument = new CampathCurveDocument();
                foreach (var channelElement in curveEditor.Elements("channel"))
                {
                    var id = channelElement.Attribute("id")?.Value;
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var channel = new CampathCurveChannel
                    {
                        Id = id,
                        Name = channelElement.Attribute("name")?.Value ?? id,
                        Group = channelElement.Attribute("group")?.Value ?? "Other",
                        Color = channelElement.Attribute("color")?.Value ?? "#FFFFFF"
                    };
                    foreach (var keyElement in channelElement.Elements("key"))
                    {
                        var key = new CampathCurveKey
                        {
                            Time = ParseDouble(keyElement.Attribute("t")?.Value),
                            Value = ParseDouble(keyElement.Attribute("v")?.Value),
                            InTangent = ParseDouble(keyElement.Attribute("in")?.Value),
                            OutTangent = ParseDouble(keyElement.Attribute("out")?.Value),
                            InWeight = ParseDouble(keyElement.Attribute("inWeight")?.Value, .25),
                            OutWeight = ParseDouble(keyElement.Attribute("outWeight")?.Value, .25),
                            WeightedTangents = ParseBool(keyElement.Attribute("weighted")?.Value),
                            Interpolation = ParseEnum(keyElement.Attribute("interpolation")?.Value, CurveInterpolationMode.Bezier),
                            TangentMode = ParseEnum(keyElement.Attribute("tangentMode")?.Value, CurveTangentMode.Auto)
                        };
                        channel.Keys.Add(key);
                    }
                    curveDocument.Channels.Add(channel);
                }
                if (curveDocument.Channels.Any(channel => channel.Keys.Count > 0))
                    data.CurveDocument = curveDocument;
            }

            if (data.PathModel == CameraPathModel.Curves && data.CurveDocument?.CanEvaluateCamera == true)
            {
                var minTime = data.CurveDocument.GetCameraKeyTimes().DefaultIfEmpty(0.0).Min();
                data.TimeOffset = minTime;
                foreach (var key in data.CurveDocument.Channels.SelectMany(channel => channel.Keys))
                    key.Time -= minTime;
            }
            else if (data.Keyframes.Count > 0)
            {
                var minTime = data.Keyframes.Min(k => k.Time);
                data.TimeOffset = minTime;
                foreach (var key in data.Keyframes)
                    key.Time -= minTime;
            }

            return data;
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string path, CampathEditorViewModel editor)
    {
        var doc = new XDocument();
        var root = new XElement("campath",
            new XAttribute("model", editor.PathModel == CameraPathModel.Curves ? "curves" : "classic"));

        if (editor.PathModel == CameraPathModel.Classic
            && editor.ClassicInterpolation == ClassicCampathInterpolation.Linear)
        {
            root.SetAttributeValue("positionInterp", "linear");
            root.SetAttributeValue("rotationInterp", "sLinear");
            root.SetAttributeValue("fovInterp", "linear");
        }

        if (editor.Hold)
            root.SetAttributeValue("hold", string.Empty);

        if (editor.PathModel == CameraPathModel.Classic)
        {
            var points = new XElement("points");
            points.Add(new XComment(
                "Points are in Quake coordinates, meaning x=forward, y=left, z=up and rotation order is first rx, then ry and lastly rz.\n" +
                "Rotation direction follows the right-hand grip rule.\n" +
                "rx (roll), ry (pitch), rz(yaw) are the Euler angles in degrees.\n" +
                "qw, qx, qy, qz are the quaternion values.\n" +
                "When read it is sufficient that either rx, ry, rz OR qw, qx, qy, qz are present.\n" +
                "If both are present then qw, qx, qy, qz take precedence."));

            foreach (var key in editor.Keyframes.OrderBy(key => key.Time).Select(key => key.ToModel()))
            {
                var q = Quaternion.Normalize(key.Rotation);
                var (pitch, yaw, roll) = QuaternionToEuler(q);

                var p = new XElement("p");
                p.SetAttributeValue("t", ToXml(key.Time + editor.TimeOffset));
                p.SetAttributeValue("x", ToXml(key.Position.X));
                p.SetAttributeValue("y", ToXml(key.Position.Y));
                p.SetAttributeValue("z", ToXml(key.Position.Z));
                p.SetAttributeValue("fov", ToXml(key.Fov));
                p.SetAttributeValue("rx", ToXml(roll));
                p.SetAttributeValue("ry", ToXml(pitch));
                p.SetAttributeValue("rz", ToXml(yaw));
                p.SetAttributeValue("qw", ToXml(q.W));
                p.SetAttributeValue("qx", ToXml(q.X));
                p.SetAttributeValue("qy", ToXml(q.Y));
                p.SetAttributeValue("qz", ToXml(q.Z));
                if (key.Dof.Enabled)
                    p.SetAttributeValue("dofEnabled", string.Empty);
                p.SetAttributeValue("dofNearBlurry", ToXml(key.Dof.NearBlurry));
                p.SetAttributeValue("dofNearCrisp", ToXml(key.Dof.NearCrisp));
                p.SetAttributeValue("dofFarCrisp", ToXml(key.Dof.FarCrisp));
                p.SetAttributeValue("dofFarBlurry", ToXml(key.Dof.FarBlurry));
                p.SetAttributeValue("dofMaxBlurSize", ToXml(key.Dof.MaxBlurSize));
                p.SetAttributeValue("dofRadiusScale", ToXml(key.Dof.RadiusScale));
                points.Add(p);
            }
            root.Add(points);
        }
        else
        {
            root.Add(WriteCurveEditor(editor));
        }
        doc.Add(root);
        doc.Save(path);
    }

    private static XElement WriteCurveEditor(CampathEditorViewModel editor)
    {
        var curveEditor = new XElement("curveEditor", new XAttribute("version", "1"));
        foreach (var channel in editor.CurveDocument.Channels.Where(channel => channel.Keys.Count > 0))
        {
            var channelElement = new XElement("channel",
                new XAttribute("id", channel.Id), new XAttribute("name", channel.Name),
                new XAttribute("group", channel.Group), new XAttribute("color", channel.Color));
            foreach (var key in channel.Keys.OrderBy(key => key.Time))
            {
                channelElement.Add(new XElement("key",
                    new XAttribute("t", ToXml(key.Time + editor.TimeOffset)),
                    new XAttribute("v", ToXml(key.Value)),
                    new XAttribute("in", ToXml(key.InTangent)),
                    new XAttribute("out", ToXml(key.OutTangent)),
                    new XAttribute("inWeight", ToXml(key.InWeight)),
                    new XAttribute("outWeight", ToXml(key.OutWeight)),
                    new XAttribute("weighted", key.WeightedTangents),
                    new XAttribute("interpolation", key.Interpolation),
                    new XAttribute("tangentMode", key.TangentMode)));
            }
            curveEditor.Add(channelElement);
        }
        return curveEditor;
    }

    private static bool HasQuaternion(XElement p)
    {
        return p.Attribute("qw") != null && p.Attribute("qx") != null && p.Attribute("qy") != null && p.Attribute("qz") != null;
    }

    private static double ParseDouble(string? value, double fallback = 0.0)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : fallback;
    }

    private static bool ParseBool(string? value) => bool.TryParse(value, out var result) && result;

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, true, out var result) ? result : fallback;

    private static string ToXml(double value)
    {
        return value.ToString("G", CultureInfo.InvariantCulture);
    }

    private static Quaternion EulerToQuaternion(double pitchDeg, double yawDeg, double rollDeg)
    {
        var pitch = DegToRad(pitchDeg);
        var yaw = DegToRad(yawDeg);
        var roll = DegToRad(rollDeg);
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)roll);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)pitch);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)yaw);
        return Quaternion.Normalize(qz * qy * qx);
    }

    private static (double pitch, double yaw, double roll) QuaternionToEuler(Quaternion q)
    {
        var forward = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, q));
        var up = Vector3.Normalize(Vector3.Transform(Vector3.UnitZ, q));
        GetYawPitchFromForward(forward, out var yawDeg, out var pitchDeg);
        var rollDeg = ComputeRollForUp(pitchDeg, yawDeg, up);
        return (pitchDeg, yawDeg, rollDeg);
    }

    private static void GetYawPitchFromForward(Vector3 forward, out double yawDeg, out double pitchDeg)
    {
        forward = Vector3.Normalize(forward);
        var yaw = Math.Atan2(forward.Y, forward.X);
        var pitch = -Math.Asin(Math.Clamp(forward.Z, -1f, 1f));
        yawDeg = RadToDeg(yaw);
        pitchDeg = RadToDeg(pitch);
    }

    private static double ComputeRollForUp(double pitchDeg, double yawDeg, Vector3 desiredUp)
    {
        var forward = GetForwardVector(pitchDeg, yawDeg);
        var right = GetRightVector(yawDeg);
        var baseUp = Vector3.Normalize(Vector3.Cross(right, forward));
        var fwd = Vector3.Normalize(forward);
        var cross = Vector3.Cross(baseUp, desiredUp);
        var sin = Vector3.Dot(cross, fwd);
        var cos = Vector3.Dot(baseUp, desiredUp);
        var rollRad = Math.Atan2(sin, cos);
        return RadToDeg(rollRad);
    }

    private static Vector3 GetForwardVector(double pitchDeg, double yawDeg)
    {
        var pitch = DegToRad(pitchDeg);
        var yaw = DegToRad(yawDeg);
        var cosPitch = Math.Cos(pitch);
        return new Vector3(
            (float)(cosPitch * Math.Cos(yaw)),
            (float)(cosPitch * Math.Sin(yaw)),
            (float)Math.Sin(-pitch));
    }

    private static Vector3 GetRightVector(double yawDeg)
    {
        var yaw = DegToRad(yawDeg);
        return new Vector3((float)Math.Sin(yaw), (float)-Math.Cos(yaw), 0f);
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;

    private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

}
