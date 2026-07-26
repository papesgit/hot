using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using HlaeObsTools.ViewModels;

namespace HlaeObsTools.Services.Campaths;

public enum CurveInterpolationMode { Constant, Linear, Bezier }
public enum CurveTangentMode { Auto, Smooth, Broken, Linear }
public enum CurveEditorViewMode { Absolute, Stacked, Normalized }

public sealed record CampathCurveBundleMarker(double Time, bool Selected, bool IsComplete);

public sealed class CampathCurveKey : ViewModelBase
{
    private double _time;
    private double _value;
    private double _inTangent;
    private double _outTangent;
    private double _inWeight = 0.25;
    private double _outWeight = 0.25;
    private bool _selected;
    private CurveInterpolationMode _interpolation = CurveInterpolationMode.Bezier;
    private CurveTangentMode _tangentMode = CurveTangentMode.Auto;
    private bool _weightedTangents;

    public double Time { get => _time; set => SetProperty(ref _time, value); }
    public double Value { get => _value; set => SetProperty(ref _value, value); }
    public double InTangent { get => _inTangent; set => SetProperty(ref _inTangent, value); }
    public double OutTangent { get => _outTangent; set => SetProperty(ref _outTangent, value); }
    public double InWeight { get => _inWeight; set => SetProperty(ref _inWeight, Math.Max(0.001, value)); }
    public double OutWeight { get => _outWeight; set => SetProperty(ref _outWeight, Math.Max(0.001, value)); }
    public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }
    public CurveInterpolationMode Interpolation { get => _interpolation; set => SetProperty(ref _interpolation, value); }
    public CurveTangentMode TangentMode { get => _tangentMode; set => SetProperty(ref _tangentMode, value); }
    public bool WeightedTangents { get => _weightedTangents; set => SetProperty(ref _weightedTangents, value); }
}

public sealed class CampathCurveChannel : ViewModelBase
{
    private bool _isVisible = true;
    private bool _isLocked;
    private string _color = "#FFFFFF";

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Group { get; set; }
    public ObservableCollection<CampathCurveKey> Keys { get; } = new();
    public bool IsVisible { get => _isVisible; set => SetProperty(ref _isVisible, value); }
    public bool IsLocked { get => _isLocked; set => SetProperty(ref _isLocked, value); }
    public string Color { get => _color; set => SetProperty(ref _color, value); }

    public double Evaluate(double time)
    {
        if (Keys.Count == 0) return 0.0;
        if (Keys.Count == 1 || time <= Keys[0].Time) return Keys[0].Value;
        if (time >= Keys[^1].Time) return Keys[^1].Value;

        var index = 0;
        while (index + 1 < Keys.Count && Keys[index + 1].Time < time) index++;
        var a = Keys[index];
        var b = Keys[index + 1];
        var dt = Math.Max(1e-9, b.Time - a.Time);
        var u = Math.Clamp((time - a.Time) / dt, 0.0, 1.0);
        if (a.Interpolation == CurveInterpolationMode.Constant) return a.Value;
        if (a.Interpolation == CurveInterpolationMode.Linear) return a.Value + (b.Value - a.Value) * u;

        if (a.WeightedTangents || b.WeightedTangents)
            return EvaluateWeighted(a, b, time, dt);

        var u2 = u * u;
        var u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1) * a.Value
             + (u3 - 2 * u2 + u) * dt * a.OutTangent
             + (-2 * u3 + 3 * u2) * b.Value
             + (u3 - u2) * dt * b.InTangent;
    }

    private static double EvaluateWeighted(CampathCurveKey a, CampathCurveKey b, double time, double dt)
    {
        var outWeight = Math.Clamp(a.OutWeight, 0.001, dt * 0.999);
        var inWeight = Math.Clamp(b.InWeight, 0.001, dt * 0.999);
        var t0 = a.Time;
        var t1 = a.Time + outWeight;
        var t2 = b.Time - inWeight;
        var t3 = b.Time;
        var u = Math.Clamp((time - t0) / dt, 0.0, 1.0);

        // Time is itself a cubic Bezier when tangents are weighted. Solve it with a
        // short binary search; monotonic clamped handles make this deterministic.
        var lo = 0.0;
        var hi = 1.0;
        for (var i = 0; i < 18; i++)
        {
            var candidate = (lo + hi) * 0.5;
            if (Bezier(t0, t1, t2, t3, candidate) < time) lo = candidate; else hi = candidate;
        }
        u = (lo + hi) * 0.5;
        return Bezier(a.Value, a.Value + a.OutTangent * outWeight,
            b.Value - b.InTangent * inWeight, b.Value, u);
    }

    private static double Bezier(double p0, double p1, double p2, double p3, double u)
    {
        var inverse = 1.0 - u;
        return inverse * inverse * inverse * p0
             + 3.0 * inverse * inverse * u * p1
             + 3.0 * inverse * u * u * p2
             + u * u * u * p3;
    }
}

public sealed class CampathCurveDocument
{
    public ObservableCollection<CampathCurveChannel> Channels { get; } = new();
    public bool DofEnabled { get; set; }

    public CampathCurveChannel? Find(string id)
    {
        foreach (var channel in Channels)
            if (channel.Id == id) return channel;
        return null;
    }

    public bool CanEvaluateCamera => Has("position.x") && Has("position.y") && Has("position.z")
        && Has("rotation.pitch") && Has("rotation.yaw") && Has("rotation.roll") && Has("fov");

    public CampathSample Evaluate(double time)
    {
        if (!CanEvaluateCamera) throw new InvalidOperationException("Camera curve channels are incomplete.");
        var position = new Vector3((float)Value("position.x", time), (float)Value("position.y", time), (float)Value("position.z", time));
        var rotation = EulerToQuaternion(Value("rotation.pitch", time), Value("rotation.yaw", time), Value("rotation.roll", time));
        var dof = new CampathDofSettings(
            DofEnabled,
            Value("dof.nearBlurry", time, -100), Value("dof.nearCrisp", time, 0),
            Value("dof.farCrisp", time, 180), Value("dof.farBlurry", time, 2000),
            Math.Clamp(Value("dof.maxBlur", time, 5), 0.0, 11.0),
            Math.Clamp(Value("dof.radiusScale", time, .25), 0.25, 5.0));
        return new CampathSample(position, rotation, Value("fov", time, 90), false, dof);
    }

    public IReadOnlyList<double> GetCameraKeyTimes() => Channels
        .Where(channel => !channel.Id.StartsWith("dof.", StringComparison.Ordinal))
        .SelectMany(channel => channel.Keys).Select(key => key.Time).Distinct().OrderBy(time => time).ToList();

    public IReadOnlyList<CampathCurveBundleMarker> GetBundleMarkers(double timeEpsilon = 0.001)
    {
        var channels = Channels.Where(channel => channel.Keys.Count > 0).ToList();
        var allKeys = channels.SelectMany(channel => channel.Keys.Select(key => (channel, key)))
            .OrderBy(item => item.key.Time).ToList();
        var clusters = new List<List<(CampathCurveChannel channel, CampathCurveKey key)>>();
        foreach (var item in allKeys)
        {
            if (clusters.Count == 0 || Math.Abs(item.key.Time - clusters[^1][0].key.Time) > timeEpsilon)
                clusters.Add(new List<(CampathCurveChannel channel, CampathCurveKey key)>());
            clusters[^1].Add(item);
        }

        var result = new List<CampathCurveBundleMarker>();
        var requiredChannels = Math.Max(2, (channels.Count + 1) / 2);
        foreach (var cluster in clusters)
        {
            var center = cluster.Average(item => item.key.Time);
            var members = cluster.GroupBy(item => item.channel)
                .Select(group => group.MinBy(item => Math.Abs(item.key.Time - center)))
                .Where(item => item != default).ToList();
            if (members.Count < requiredChannels) continue;
            result.Add(new CampathCurveBundleMarker(
                members.Average(member => member.key.Time),
                members.All(member => member.key.Selected),
                members.Count == channels.Count));
        }
        return result;
    }

    private bool Has(string id) => Find(id)?.Keys.Count > 0;
    private double Value(string id, double time, double fallback = 0) => Find(id) is { Keys.Count: > 0 } channel ? channel.Evaluate(time) : fallback;

    private static Quaternion EulerToQuaternion(double pitchDeg, double yawDeg, double rollDeg)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (float)(rollDeg * Math.PI / 180.0));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(pitchDeg * Math.PI / 180.0));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, (float)(yawDeg * Math.PI / 180.0));
        return Quaternion.Normalize(qz * qy * qx);
    }
}
