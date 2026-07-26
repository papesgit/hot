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
        public bool DofEnabled { get; set; }
        public double TimeOffset { get; set; }
        public List<CampathKeyframe> Keyframes { get; } = new();
        public CampathCurveDocument? CurveDocument { get; set; }
    }

    public sealed class CampathSequenceFileData
    {
        public double TimeOffset { get; set; }
        public List<CameraTrackFileData> Cameras { get; } = new();
        public List<CameraCutFileData> CameraCuts { get; } = new();
    }

    public sealed record CameraTrackFileData(string Id, string Name, CampathFileData Campath);
    public sealed record CameraCutFileData(string CameraId, double StartTime, double EndTime);

    public static CampathFileData? Load(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            if (doc.Element("campath") is { } root)
                return ReadCampath(root, normalizeTimes: true);
            var sequenceRoot = doc.Element("campathSequence");
            var first = sequenceRoot?.Element("cameras")?.Elements("camera")
                .Select(camera => camera.Element("campath"))
                .FirstOrDefault(element => element != null);
            if (first == null)
                return null;
            var data = ReadCampath(first, normalizeTimes: false);
            data.TimeOffset = ParseDouble(sequenceRoot?.Attribute("offset")?.Value);
            return data;
        }
        catch
        {
            return null;
        }
    }

    public static CampathSequenceFileData? LoadSequence(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            if (doc.Element("campath") is { } legacyRoot)
            {
                var legacy = ReadCampath(legacyRoot, normalizeTimes: true);
                var result = new CampathSequenceFileData { TimeOffset = legacy.TimeOffset };
                result.Cameras.Add(new CameraTrackFileData("camera-1", "Camera 1", legacy));
                return result;
            }

            var root = doc.Element("campathSequence");
            var camerasElement = root?.Element("cameras");
            if (root == null || camerasElement == null)
                return null;

            var sequence = new CampathSequenceFileData
            {
                TimeOffset = ParseDouble(root.Attribute("offset")?.Value)
            };
            foreach (var cameraElement in camerasElement.Elements("camera"))
            {
                var campathElement = cameraElement.Element("campath");
                if (campathElement == null)
                    continue;
                var id = cameraElement.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var camera = ReadCampath(campathElement, normalizeTimes: false);
                camera.TimeOffset = sequence.TimeOffset;
                sequence.Cameras.Add(new CameraTrackFileData(
                    id, cameraElement.Attribute("name")?.Value ?? id, camera));
            }
            foreach (var cutElement in root.Element("cameraCuts")?.Elements("cut") ?? [])
            {
                var cameraId = cutElement.Attribute("camera")?.Value ?? string.Empty;
                var start = ParseDouble(cutElement.Attribute("start")?.Value);
                var end = ParseDouble(cutElement.Attribute("end")?.Value, start);
                if (end > start)
                    sequence.CameraCuts.Add(new CameraCutFileData(cameraId, start, end));
            }
            return sequence.Cameras.Count > 0 ? sequence : null;
        }
        catch
        {
            return null;
        }
    }

    private static CampathFileData ReadCampath(XElement root, bool normalizeTimes)
    {
        var data = new CampathFileData
        {
            PathModel = string.Equals(root.Attribute("model")?.Value, "curves",
                StringComparison.OrdinalIgnoreCase)
                ? CameraPathModel.Curves
                : CameraPathModel.Classic,
            DofEnabled = ParseBool(root.Attribute("dofEnabled")?.Value),
            Hold = root.Attribute("hold") != null
        };

        var anyLinear = string.Equals(root.Attribute("positionInterp")?.Value, "linear",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(root.Attribute("rotationInterp")?.Value, "sLinear",
                            StringComparison.OrdinalIgnoreCase)
                        || string.Equals(root.Attribute("fovInterp")?.Value, "linear",
                            StringComparison.OrdinalIgnoreCase);
        data.ClassicInterpolation = anyLinear
            ? ClassicCampathInterpolation.Linear
            : ClassicCampathInterpolation.CatmullRom;

        if (data.PathModel == CameraPathModel.Classic && root.Element("points") is { } points)
        {
            foreach (var p in points.Elements("p"))
            {
                Quaternion rotation;
                if (HasQuaternion(p))
                {
                    rotation = Quaternion.Normalize(new Quaternion(
                        (float)ParseDouble(p.Attribute("qx")?.Value),
                        (float)ParseDouble(p.Attribute("qy")?.Value),
                        (float)ParseDouble(p.Attribute("qz")?.Value),
                        (float)ParseDouble(p.Attribute("qw")?.Value, 1.0)));
                }
                else
                {
                    rotation = EulerToQuaternion(
                        ParseDouble(p.Attribute("ry")?.Value),
                        ParseDouble(p.Attribute("rz")?.Value),
                        ParseDouble(p.Attribute("rx")?.Value));
                }

                data.Keyframes.Add(new CampathKeyframe
                {
                    Time = ParseDouble(p.Attribute("t")?.Value),
                    Position = new Vector3(
                        (float)ParseDouble(p.Attribute("x")?.Value),
                        (float)ParseDouble(p.Attribute("y")?.Value),
                        (float)ParseDouble(p.Attribute("z")?.Value)),
                    Rotation = rotation,
                    Fov = ParseDouble(p.Attribute("fov")?.Value, 90.0),
                    Selected = p.Attribute("selected") != null,
                    Dof = new CampathDofSettings(
                        data.DofEnabled,
                        ParseDouble(p.Attribute("dofNearBlurry")?.Value, -100.0),
                        ParseDouble(p.Attribute("dofNearCrisp")?.Value),
                        ParseDouble(p.Attribute("dofFarCrisp")?.Value, 180.0),
                        ParseDouble(p.Attribute("dofFarBlurry")?.Value, 2000.0),
                        Math.Clamp(ParseDouble(p.Attribute("dofMaxBlurSize")?.Value, 5.0), 0.0, 11.0),
                        Math.Clamp(ParseDouble(p.Attribute("dofRadiusScale")?.Value, 0.25), 0.25, 5.0))
                });
            }
        }

        if (data.PathModel == CameraPathModel.Curves && root.Element("curveEditor") is { } curveEditor)
        {
            var curveDocument = new CampathCurveDocument
            {
                DofEnabled = ParseBool(curveEditor.Attribute("dofEnabled")?.Value)
            };
            data.DofEnabled = curveDocument.DofEnabled;
            foreach (var channelElement in curveEditor.Elements("channel"))
            {
                var id = channelElement.Attribute("id")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                var channel = new CampathCurveChannel
                {
                    Id = id,
                    Name = channelElement.Attribute("name")?.Value ?? id,
                    Group = channelElement.Attribute("group")?.Value ?? "Other",
                    Color = channelElement.Attribute("color")?.Value ?? "#FFFFFF"
                };
                foreach (var keyElement in channelElement.Elements("key"))
                {
                    channel.Keys.Add(new CampathCurveKey
                    {
                        Time = ParseDouble(keyElement.Attribute("t")?.Value),
                        Value = ParseDouble(keyElement.Attribute("v")?.Value),
                        InTangent = ParseDouble(keyElement.Attribute("in")?.Value),
                        OutTangent = ParseDouble(keyElement.Attribute("out")?.Value),
                        InWeight = ParseDouble(keyElement.Attribute("inWeight")?.Value, .25),
                        OutWeight = ParseDouble(keyElement.Attribute("outWeight")?.Value, .25),
                        WeightedTangents = ParseBool(keyElement.Attribute("weighted")?.Value),
                        Interpolation = ParseEnum(keyElement.Attribute("interpolation")?.Value,
                            CurveInterpolationMode.Bezier),
                        TangentMode = ParseEnum(keyElement.Attribute("tangentMode")?.Value,
                            CurveTangentMode.Auto)
                    });
                }
                curveDocument.Channels.Add(channel);
            }
            if (curveDocument.Channels.Any(channel => channel.Keys.Count > 0))
                data.CurveDocument = curveDocument;
        }

        if (!normalizeTimes)
            return data;

        if (data.PathModel == CameraPathModel.Curves && data.CurveDocument?.CanEvaluateCamera == true)
        {
            var minTime = data.CurveDocument.GetCameraKeyTimes().DefaultIfEmpty(0.0).Min();
            data.TimeOffset = minTime;
            foreach (var key in data.CurveDocument.Channels.SelectMany(channel => channel.Keys))
                key.Time -= minTime;
        }
        else if (data.Keyframes.Count > 0)
        {
            var minTime = data.Keyframes.Min(key => key.Time);
            data.TimeOffset = minTime;
            foreach (var key in data.Keyframes)
                key.Time -= minTime;
        }
        return data;
    }

    public static void Save(string path, CampathEditorViewModel editor)
    {
        new XDocument(WriteCampath(editor, applyTimeOffset: true)).Save(path);
    }

    public static void Save(string path, CampathSequenceViewModel sequence)
    {
        if (sequence.Cameras.Count == 1)
        {
            Save(path, sequence.Cameras[0].Editor);
            return;
        }

        var offset = sequence.Cameras.FirstOrDefault()?.Editor.TimeOffset ?? 0.0;
        var root = new XElement("campathSequence",
            new XAttribute("version", "1"));
        if (offset != 0.0)
            root.SetAttributeValue("offset", ToXml(offset));

        var cameras = new XElement("cameras");
        foreach (var camera in sequence.Cameras)
        {
            cameras.Add(new XElement("camera",
                new XAttribute("id", camera.Id.ToString("D")),
                new XAttribute("name", camera.Name),
                WriteCampath(camera.Editor, applyTimeOffset: false)));
        }
        root.Add(cameras);

        var cuts = new XElement("cameraCuts");
        foreach (var cut in sequence.CameraCuts.OrderBy(cut => cut.StartTime))
        {
            cuts.Add(new XElement("cut",
                new XAttribute("start", ToXml(cut.StartTime)),
                new XAttribute("end", ToXml(cut.EndTime)),
                new XAttribute("camera", cut.CameraId == Guid.Empty
                    ? string.Empty
                    : cut.CameraId.ToString("D"))));
        }
        root.Add(cuts);
        new XDocument(root).Save(path);
    }

    private static XElement WriteCampath(CampathEditorViewModel editor, bool applyTimeOffset)
    {
        var root = new XElement("campath",
            new XAttribute("model", editor.PathModel == CameraPathModel.Curves ? "curves" : "classic"));

        if (editor.PathModel == CameraPathModel.Classic
            && editor.ClassicInterpolation == ClassicCampathInterpolation.Linear)
        {
            root.SetAttributeValue("positionInterp", "linear");
            root.SetAttributeValue("rotationInterp", "sLinear");
            root.SetAttributeValue("fovInterp", "linear");
        }
        if (editor.PathModel == CameraPathModel.Classic)
            root.SetAttributeValue("dofEnabled", editor.CurveDocument.DofEnabled);

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
                p.SetAttributeValue("t", ToXml(key.Time + (applyTimeOffset ? editor.TimeOffset : 0.0)));
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
            root.Add(WriteCurveEditor(editor, applyTimeOffset));
        }
        return root;
    }

    private static XElement WriteCurveEditor(CampathEditorViewModel editor, bool applyTimeOffset)
    {
        var curveEditor = new XElement("curveEditor",
            new XAttribute("version", "1"),
            new XAttribute("dofEnabled", editor.CurveDocument.DofEnabled));
        foreach (var channel in editor.CurveDocument.Channels.Where(channel => channel.Keys.Count > 0))
        {
            var channelElement = new XElement("channel",
                new XAttribute("id", channel.Id), new XAttribute("name", channel.Name),
                new XAttribute("group", channel.Group), new XAttribute("color", channel.Color));
            foreach (var key in channel.Keys.OrderBy(key => key.Time))
            {
                channelElement.Add(new XElement("key",
                    new XAttribute("t", ToXml(key.Time + (applyTimeOffset ? editor.TimeOffset : 0.0))),
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
