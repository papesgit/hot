using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace HlaeObsTools.Services.Campaths;

public enum CampathDoubleInterp
{
    Default = 0,
    Linear = 1,
    Cubic = 2
}

public enum CampathQuaternionInterp
{
    Default = 0,
    SLinear = 1,
    SCubic = 2
}

public sealed class CampathKeyframe
{
    public double Time { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public double Fov { get; set; } = 90.0;
    public bool Selected { get; set; }
    public CampathDofSettings Dof { get; set; } = CampathDofSettings.Default;
}

/// <summary>Viewport DOF values corresponding to CS2's r_dof2 and r_dof_override distance controls.</summary>
public readonly record struct CampathDofSettings(bool Enabled, double NearBlurry, double NearCrisp,
    double FarCrisp, double FarBlurry, double MaxBlurSize, double RadiusScale)
{
    public static CampathDofSettings Default { get; } = new(false, -100.0, 0.0, 180.0, 2000.0, 5.0, 0.25);
}

public readonly struct CampathSample
{
    public CampathSample(Vector3 position, Quaternion rotation, double fov, bool selected, CampathDofSettings dof)
    {
        Position = position;
        Rotation = rotation;
        Fov = fov;
        Selected = selected;
        Dof = dof;
    }

    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public double Fov { get; }
    public bool Selected { get; }
    public CampathDofSettings Dof { get; }
}

public sealed class CampathCurve
{
    private readonly List<CampathKeyframe> _keyframes = new();
    private IReadOnlyList<CampathKeyframe> _splineKeyframes = Array.Empty<CampathKeyframe>();
    private readonly CampathDoubleSpline _xSpline = new();
    private readonly CampathDoubleSpline _ySpline = new();
    private readonly CampathDoubleSpline _zSpline = new();
    private readonly CampathDoubleSpline _fovSpline = new();
    private readonly CampathQuaternionSpline _rotSpline = new();
    private readonly CampathBoolAndSpline _selectedSpline = new();
    private readonly CampathBoolAndSpline _dofEnabledSpline = new();
    private readonly CampathDoubleSpline _nearBlurrySpline = new();
    private readonly CampathDoubleSpline _nearCrispSpline = new();
    private readonly CampathDoubleSpline _farCrispSpline = new();
    private readonly CampathDoubleSpline _farBlurrySpline = new();
    private readonly CampathDoubleSpline _maxBlurSizeSpline = new();
    private readonly CampathDoubleSpline _radiusScaleSpline = new();
    private bool _dirty = true;

    public CampathDoubleInterp PositionInterp { get; set; } = CampathDoubleInterp.Default;
    public CampathQuaternionInterp RotationInterp { get; set; } = CampathQuaternionInterp.Default;
    public CampathDoubleInterp FovInterp { get; set; } = CampathDoubleInterp.Default;

    public IReadOnlyList<CampathKeyframe> Keyframes => _keyframes;

    public void SetKeyframes(IEnumerable<CampathKeyframe> keyframes)
    {
        _keyframes.Clear();
        _keyframes.AddRange(keyframes.OrderBy(k => k.Time));
        _dirty = true;
    }

    public void AddKeyframe(CampathKeyframe keyframe)
    {
        _keyframes.Add(keyframe);
        _keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
        _dirty = true;
    }

    public void Clear()
    {
        _keyframes.Clear();
        _dirty = true;
    }

    public bool CanEvaluate()
    {
        var points = BuildSplineKeyframes();
        var posMode = EffectivePositionInterp;
        var rotMode = EffectiveRotationInterp;
        var fovMode = EffectiveFovInterp;
        return CampathDoubleSpline.CanEval(points.Count, posMode)
               && CampathDoubleSpline.CanEval(points.Count, fovMode)
               && CampathQuaternionSpline.CanEval(points.Count, rotMode)
               && CampathBoolAndSpline.CanEval(points.Count);
    }

    public CampathSample Evaluate(double time)
    {
        if (_dirty)
            Rebuild();

        if (_splineKeyframes.Count > 0)
        {
            var minTime = _splineKeyframes[0].Time;
            var maxTime = _splineKeyframes[_splineKeyframes.Count - 1].Time;
            time = Math.Clamp(time, minTime, maxTime);
        }

        var x = _xSpline.Eval(time);
        var y = _ySpline.Eval(time);
        var z = _zSpline.Eval(time);
        var fov = _fovSpline.Eval(time);
        var rotation = _rotSpline.Eval(time);
        var selected = _selectedSpline.Eval(time);
        var dof = new CampathDofSettings(_dofEnabledSpline.Eval(time), _nearBlurrySpline.Eval(time),
            _nearCrispSpline.Eval(time), _farCrispSpline.Eval(time), _farBlurrySpline.Eval(time),
            _maxBlurSizeSpline.Eval(time), _radiusScaleSpline.Eval(time));
        return new CampathSample(new Vector3((float)x, (float)y, (float)z), rotation, fov, selected, dof);
    }

    private CampathDoubleInterp EffectivePositionInterp =>
        PositionInterp == CampathDoubleInterp.Default ? CampathDoubleInterp.Cubic : PositionInterp;

    private CampathQuaternionInterp EffectiveRotationInterp =>
        RotationInterp == CampathQuaternionInterp.Default ? CampathQuaternionInterp.SCubic : RotationInterp;

    private CampathDoubleInterp EffectiveFovInterp =>
        FovInterp == CampathDoubleInterp.Default ? CampathDoubleInterp.Cubic : FovInterp;

    private void Rebuild()
    {
        _dirty = false;

        _splineKeyframes = BuildSplineKeyframes();
        _xSpline.SetPoints(_splineKeyframes, k => k.Position.X, EffectivePositionInterp);
        _ySpline.SetPoints(_splineKeyframes, k => k.Position.Y, EffectivePositionInterp);
        _zSpline.SetPoints(_splineKeyframes, k => k.Position.Z, EffectivePositionInterp);
        _fovSpline.SetPoints(_splineKeyframes, k => k.Fov, EffectiveFovInterp);
        _rotSpline.SetPoints(_splineKeyframes, k => k.Rotation, EffectiveRotationInterp);
        _selectedSpline.SetPoints(_splineKeyframes, k => k.Selected);
        _dofEnabledSpline.SetPoints(_splineKeyframes, k => k.Dof.Enabled);
        _nearBlurrySpline.SetPoints(_splineKeyframes, k => k.Dof.NearBlurry, EffectiveFovInterp);
        _nearCrispSpline.SetPoints(_splineKeyframes, k => k.Dof.NearCrisp, EffectiveFovInterp);
        _farCrispSpline.SetPoints(_splineKeyframes, k => k.Dof.FarCrisp, EffectiveFovInterp);
        _farBlurrySpline.SetPoints(_splineKeyframes, k => k.Dof.FarBlurry, EffectiveFovInterp);
        _maxBlurSizeSpline.SetPoints(_splineKeyframes, k => k.Dof.MaxBlurSize, EffectiveFovInterp);
        _radiusScaleSpline.SetPoints(_splineKeyframes, k => k.Dof.RadiusScale, EffectiveFovInterp);
    }

    private IReadOnlyList<CampathKeyframe> BuildSplineKeyframes()
    {
        if (_keyframes.Count <= 1)
            return _keyframes;

        const double timeEpsilon = 1e-6;
        var ordered = _keyframes.OrderBy(k => k.Time).ToList();
        var result = new List<CampathKeyframe>(ordered.Count);
        foreach (var key in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(key);
                continue;
            }

            var last = result[result.Count - 1];
            if (Math.Abs(key.Time - last.Time) <= timeEpsilon)
            {
                result[result.Count - 1] = key;
                continue;
            }

            if (key.Time > last.Time)
            {
                result.Add(key);
            }
        }

        return result;
    }
}

internal sealed class CampathBoolAndSpline
{
    private IReadOnlyList<CampathKeyframe> _points = Array.Empty<CampathKeyframe>();
    private Func<CampathKeyframe, bool>? _selector;

    public void SetPoints(IReadOnlyList<CampathKeyframe> points, Func<CampathKeyframe, bool> selector)
    {
        _points = points;
        _selector = selector;
    }

    public static bool CanEval(int count) => count >= 2;

    public bool Eval(double t)
    {
        var (lower, upper) = GetNearestInterval(t);
        var lowerT = lower.Time;
        var lowerV = _selector!(lower);

        if (t <= lowerT)
            return lowerV;

        var upperT = upper.Time;
        var upperV = _selector(upper);

        if (upperT <= t)
            return upperV;

        return lowerV && upperV;
    }

    private (CampathKeyframe lower, CampathKeyframe upper) GetNearestInterval(double time)
    {
        var count = _points.Count;
        if (count == 0)
            throw new InvalidOperationException("No points.");
        if (count == 1)
            return (_points[0], _points[0]);

        var upperIndex = UpperBound(time);
        if (upperIndex >= count)
        {
            var upper = _points[count - 1];
            var lower = count > 1 ? _points[count - 2] : upper;
            return (lower, upper);
        }

        if (upperIndex == 0)
            return (_points[0], _points[1]);

        return (_points[upperIndex - 1], _points[upperIndex]);
    }

    private int UpperBound(double time)
    {
        int lo = 0;
        int hi = _points.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_points[mid].Time <= time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }
}

internal sealed class CampathDoubleSpline
{
    private double[] _t = Array.Empty<double>();
    private double[] _x = Array.Empty<double>();
    private double[] _x2 = Array.Empty<double>();
    private bool _useCubic;

    public static bool CanEval(int count, CampathDoubleInterp mode)
    {
        return mode == CampathDoubleInterp.Cubic ? count >= 4 : count >= 2;
    }

    public void SetPoints(IReadOnlyList<CampathKeyframe> points, Func<CampathKeyframe, double> selector, CampathDoubleInterp mode)
    {
        _useCubic = mode == CampathDoubleInterp.Cubic;

        _t = new double[points.Count];
        _x = new double[points.Count];
        for (var i = 0; i < points.Count; i++)
        {
            _t[i] = points[i].Time;
            _x[i] = selector(points[i]);
        }

        if (_useCubic)
        {
            _x2 = new double[points.Count];
            Spline(_t, _x, points.Count, false, 0.0, false, 0.0, _x2);
        }
        else
        {
            _x2 = Array.Empty<double>();
        }
    }

    public double Eval(double t)
    {
        if (_useCubic)
        {
            return Splint(_t, _x, _x2, _t.Length, t);
        }

        var (lower, upper) = GetNearestInterval(t);
        var lowerT = lower.t;
        var lowerV = lower.x;
        if (t <= lowerT)
            return lowerV;

        var upperT = upper.t;
        var upperV = upper.x;
        if (upperT <= t)
            return upperV;

        var deltaT = upperT - lowerT;
        return (1 - (t - lowerT) / deltaT) * lowerV + ((t - lowerT) / deltaT) * upperV;
    }

    private ( (double t, double x) lower, (double t, double x) upper ) GetNearestInterval(double time)
    {
        var count = _t.Length;
        if (count == 0)
            throw new InvalidOperationException("No points.");
        if (count == 1)
            return ((_t[0], _x[0]), (_t[0], _x[0]));

        var upperIndex = UpperBound(time);
        if (upperIndex >= count)
        {
            var upper = (_t[count - 1], _x[count - 1]);
            var lower = count > 1 ? (_t[count - 2], _x[count - 2]) : upper;
            return (lower, upper);
        }

        if (upperIndex == 0)
            return ((_t[0], _x[0]), (_t[1], _x[1]));

        return ((_t[upperIndex - 1], _x[upperIndex - 1]), (_t[upperIndex], _x[upperIndex]));
    }

    private int UpperBound(double time)
    {
        int lo = 0;
        int hi = _t.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_t[mid] <= time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private static void Spline(double[] x, double[] y, int n, bool y1Natural, double yp1, bool ynNatural, double ypn, double[] y2)
    {
        var u = new double[n - 1];

        if (y1Natural)
        {
            y2[0] = 0.0;
            u[0] = 0.0;
        }
        else
        {
            y2[0] = -0.5;
            u[0] = (3.0 / (x[1] - x[0])) * ((y[1] - y[0]) / (x[1] - x[0]) - yp1);
        }

        for (var i = 1; i <= n - 2; i++)
        {
            var sig = (x[i] - x[i - 1]) / (x[i + 1] - x[i - 1]);
            var p = sig * y2[i - 1] + 2.0;
            y2[i] = (sig - 1.0) / p;
            u[i] = (y[i + 1] - y[i]) / (x[i + 1] - x[i]) - (y[i] - y[i - 1]) / (x[i] - x[i - 1]);
            u[i] = (6.0 * u[i] / (x[i + 1] - x[i - 1]) - sig * u[i - 1]) / p;
        }

        double qn;
        double un;
        if (ynNatural)
        {
            qn = 0.0;
            un = 0.0;
        }
        else
        {
            qn = 0.5;
            un = (3.0 / (x[n - 1] - x[n - 2])) * (ypn - (y[n - 1] - y[n - 2]) / (x[n - 1] - x[n - 2]));
        }

        y2[n - 1] = (un - qn * u[n - 2]) / (qn * y2[n - 2] + 1.0);
        for (var k = n - 2; k >= 0; k--)
            y2[k] = y2[k] * y2[k + 1] + u[k];
    }

    private static double Splint(double[] xa, double[] ya, double[] y2a, int n, double x)
    {
        var klo = 0;
        var khi = n - 1;
        while (khi - klo > 1)
        {
            var k = (khi + klo) >> 1;
            if (xa[k] > x)
                khi = k;
            else
                klo = k;
        }

        var h = xa[khi] - xa[klo];
        if (h == 0.0)
            throw new InvalidOperationException("splint: Bad xa input.");

        var a = (xa[khi] - x) / h;
        var b = (x - xa[klo]) / h;
        return a * ya[klo] + b * ya[khi] + ((a * a * a - a) * y2a[klo] + (b * b * b - b) * y2a[khi]) * (h * h) / 6.0;
    }
}

internal sealed class CampathQuaternionSpline
{
    private const double Eps = 1.0e-6;
    private double[] _t = Array.Empty<double>();
    private double[,] _qy = new double[0, 0];
    private double[] _qh = Array.Empty<double>();
    private double[] _qdtheta = Array.Empty<double>();
    private double[,] _qe = new double[0, 0];
    private double[,] _qw = new double[0, 0];
    private bool _useCubic;

    private readonly QsplineWork _work = new();

    public static bool CanEval(int count, CampathQuaternionInterp mode)
    {
        return mode == CampathQuaternionInterp.SCubic ? count >= 4 : count >= 2;
    }

    public void SetPoints(IReadOnlyList<CampathKeyframe> points, Func<CampathKeyframe, Quaternion> selector, CampathQuaternionInterp mode)
    {
        _useCubic = mode == CampathQuaternionInterp.SCubic;

        _t = new double[points.Count];
        _qy = new double[points.Count, 4];

        QuaternionD? last = null;
        for (var i = 0; i < points.Count; i++)
        {
            _t[i] = points[i].Time;
            var q = QuaternionD.FromQuaternion(selector(points[i]));

            if (last.HasValue && QuaternionD.Dot(last.Value, q) < 0.0)
                q = q.Negate();

            _qy[i, 0] = q.X;
            _qy[i, 1] = q.Y;
            _qy[i, 2] = q.Z;
            _qy[i, 3] = q.W;
            last = q;
        }

        if (_useCubic)
        {
            _qh = new double[points.Count - 1];
            _qdtheta = new double[points.Count - 1];
            _qe = new double[points.Count - 1, 3];
            _qw = new double[points.Count, 3];

            var wi = new[] { 0.0, 0.0, 0.0 };
            var wf = new[] { 0.0, 0.0, 0.0 };
            QsplineInit(points.Count, 2, Eps, wi, wf, _t, _qy, _qh, _qdtheta, _qe, _qw);
        }
        else
        {
            _qh = Array.Empty<double>();
            _qdtheta = Array.Empty<double>();
            _qe = new double[0, 0];
            _qw = new double[0, 0];
        }
    }

    public Quaternion Eval(double t)
    {
        if (_useCubic)
        {
            var q = new double[4];
            var omega = new double[3];
            var alpha = new double[3];
            QsplineInterp(_t.Length, t, _t, _qy, _qh, _qdtheta, _qe, _qw, q, omega, alpha);
            return new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]);
        }

        var (lower, upper) = GetNearestInterval(t);
        var lowerT = lower.t;
        var lowerQ = lower.q;
        if (t <= lowerT)
            return lowerQ.ToQuaternion();

        var upperT = upper.t;
        var upperQ = upper.q;
        if (upperT <= t)
            return upperQ.ToQuaternion();

        if (QuaternionD.Dot(upperQ, lowerQ) < 0.0)
            upperQ = upperQ.Negate();

        var deltaT = upperT - lowerT;
        var ratio = (t - lowerT) / deltaT;
        return QuaternionD.Slerp(lowerQ, upperQ, ratio).ToQuaternion();
    }

    private ( (double t, QuaternionD q) lower, (double t, QuaternionD q) upper ) GetNearestInterval(double time)
    {
        var count = _t.Length;
        if (count == 0)
            throw new InvalidOperationException("No points.");
        if (count == 1)
        {
            var q0 = new QuaternionD(_qy[0, 3], _qy[0, 0], _qy[0, 1], _qy[0, 2]);
            return ((_t[0], q0), (_t[0], q0));
        }

        var upperIndex = UpperBound(time);
        if (upperIndex >= count)
        {
            var upper = (_t[count - 1], new QuaternionD(_qy[count - 1, 3], _qy[count - 1, 0], _qy[count - 1, 1], _qy[count - 1, 2]));
            var lowerIdx = count > 1 ? count - 2 : count - 1;
            var lower = (_t[lowerIdx], new QuaternionD(_qy[lowerIdx, 3], _qy[lowerIdx, 0], _qy[lowerIdx, 1], _qy[lowerIdx, 2]));
            return (lower, upper);
        }

        if (upperIndex == 0)
        {
            var q0 = new QuaternionD(_qy[0, 3], _qy[0, 0], _qy[0, 1], _qy[0, 2]);
            var q1 = new QuaternionD(_qy[1, 3], _qy[1, 0], _qy[1, 1], _qy[1, 2]);
            return ((_t[0], q0), (_t[1], q1));
        }

        var lowerQ = new QuaternionD(_qy[upperIndex - 1, 3], _qy[upperIndex - 1, 0], _qy[upperIndex - 1, 1], _qy[upperIndex - 1, 2]);
        var upperQ = new QuaternionD(_qy[upperIndex, 3], _qy[upperIndex, 0], _qy[upperIndex, 1], _qy[upperIndex, 2]);
        return ((_t[upperIndex - 1], lowerQ), (_t[upperIndex], upperQ));
    }

    private int UpperBound(double time)
    {
        int lo = 0;
        int hi = _t.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_t[mid] <= time)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    private void QsplineInit(int n, int maxit, double tol, double[] wi, double[] wf, double[] x, double[,] y,
        double[] h, double[] dtheta, double[,] e, double[,] w)
    {
        if (n < 4)
            throw new InvalidOperationException("qspline_init: insufficient input data.");

        var wprev = new double[n, 3];
        var a = new double[n - 1];
        var b = new double[n - 1];
        var c = new double[n - 1];

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < 3; j++)
                w[i, j] = 0.0;
        }

        for (var i = 0; i < n - 1; i++)
        {
            h[i] = x[i + 1] - x[i];
            if (h[i] <= 0.0)
                throw new InvalidOperationException("qspline_init: x is not monotonic.");
        }

        for (var i = 0; i < n - 1; i++)
        {
            var qi = new[] { y[i, 0], y[i, 1], y[i, 2], y[i, 3] };
            var qf = new[] { y[i + 1, 0], y[i + 1, 1], y[i + 1, 2], y[i + 1, 3] };
            var axis = new double[3];
            dtheta[i] = GetAng(qi, qf, axis);
            e[i, 0] = axis[0];
            e[i, 1] = axis[1];
            e[i, 2] = axis[2];
        }

        Rates(n, maxit, tol, wi, wf, h, a, b, c, dtheta, e, w, wprev);
    }

    private void QsplineInterp(int n, double xi, double[] x, double[,] y, double[] h, double[] dtheta, double[,] e, double[,] w,
        double[] q, double[] omega, double[] alpha)
    {
        int klo = 0;
        int khi = n - 1;
        while (khi - klo > 1)
        {
            var k = (khi + klo) >> 1;
            if (x[k] > xi)
                khi = k;
            else
                klo = k;
        }

        var dum1 = new double[3];
        var dum2 = new double[3];
        _work.Slew3Init(h[klo], dtheta[klo], GetRow(e, klo), GetRow(w, klo), dum1, GetRow(w, klo + 1), dum2);

        var qi = new[] { y[klo, 0], y[klo, 1], y[klo, 2], y[klo, 3] };
        _work.Slew3(xi - x[klo], h[klo], qi, q, omega, alpha, dum1);
    }

    private static void Rates(int n, int maxit, double tol, double[] wi, double[] wf, double[] h, double[] a, double[] b, double[] c,
        double[] dtheta, double[,] e, double[,] w, double[,] wprev)
    {
        int iter = 0;
        double dw;
        var temp1 = new double[3];
        var temp2 = new double[3];

        do
        {
            for (var i = 1; i < n - 1; i++)
                for (var j = 0; j < 3; j++)
                    wprev[i, j] = w[i, j];

            for (var i = 1; i < n - 1; i++)
            {
                a[i] = 2.0 / h[i - 1];
                b[i] = 4.0 / h[i - 1] + 4.0 / h[i];
                c[i] = 2.0 / h[i];

                Rf(GetRow(e, i - 1), dtheta[i - 1], GetRow(wprev, i), temp1);

                for (var j = 0; j < 3; j++)
                {
                    w[i, j] = 6.0 * (dtheta[i - 1] * e[i - 1, j] / (h[i - 1] * h[i - 1]) +
                                     dtheta[i] * e[i, j] / (h[i] * h[i])) - temp1[j];
                }
            }

            Bd(GetRow(e, 0), dtheta[0], 1, wi, temp1);
            Bd(GetRow(e, n - 2), dtheta[n - 2], 0, wf, temp2);

            for (var j = 0; j < 3; j++)
            {
                w[1, j] -= a[1] * temp1[j];
                w[n - 2, j] -= c[n - 2] * temp2[j];
            }

            for (var i = 1; i < n - 2; i++)
            {
                b[i + 1] -= c[i] * a[i + 1] / b[i];

                Bd(GetRow(e, i), dtheta[i], 1, GetRow(w, i), temp1);
                for (var j = 0; j < 3; j++)
                    w[i + 1, j] -= temp1[j] * a[i + 1] / b[i];
            }

            for (var j = 0; j < 3; j++)
                w[n - 2, j] /= b[n - 2];

            for (var i = n - 3; i > 0; i--)
            {
                Bd(GetRow(e, i), dtheta[i], 0, GetRow(w, i + 1), temp1);
                for (var j = 0; j < 3; j++)
                    w[i, j] = (w[i, j] - c[i] * temp1[j]) / b[i];
            }

            dw = 0.0;
            for (var i = 1; i < n - 1; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    var delta = w[i, j] - wprev[i, j];
                    dw += delta * delta;
                }
            }
            dw = Math.Sqrt(dw);
        } while (iter++ < maxit && dw > tol);

        for (var j = 0; j < 3; j++)
        {
            w[0, j] = wi[j];
            w[n - 1, j] = wf[j];
        }
    }

    private static int Bd(double[] e, double dtheta, int flag, double[] xin, double[] xout)
    {
        if (dtheta > Eps)
        {
            var ca = Math.Cos(dtheta);
            var sa = Math.Sin(dtheta);

            double b1;
            double b2;
            if (flag == 0)
            {
                b1 = 0.5 * dtheta * sa / (1.0 - ca);
                b2 = 0.5 * dtheta;
            }
            else if (flag == 1)
            {
                b1 = sa / dtheta;
                b2 = (ca - 1.0) / dtheta;
            }
            else
            {
                return -1;
            }

            var b0 = xin[0] * e[0] + xin[1] * e[1] + xin[2] * e[2];
            var temp2 = Cross(e, xin);
            var temp1 = Cross(temp2, e);

            for (var i = 0; i < 3; i++)
                xout[i] = b0 * e[i] + b1 * temp1[i] + b2 * temp2[i];
        }
        else
        {
            for (var i = 0; i < 3; i++)
                xout[i] = xin[i];
        }

        return 0;
    }

    private static void Rf(double[] e, double dtheta, double[] win, double[] rhs)
    {
        if (dtheta > Eps)
        {
            var ca = Math.Cos(dtheta);
            var sa = Math.Sin(dtheta);
            var temp2 = Cross(e, win);
            var temp1 = Cross(temp2, e);

            var dot = win[0] * e[0] + win[1] * e[1] + win[2] * e[2];
            var mag = win[0] * win[0] + win[1] * win[1] + win[2] * win[2];
            var c1 = 1.0 - ca;
            var r0 = 0.5 * (mag - dot * dot) * (dtheta - sa) / c1;
            var r1 = dot * (dtheta * sa - 2.0 * c1) / (dtheta * c1);

            for (var i = 0; i < 3; i++)
                rhs[i] = r0 * e[i] + r1 * temp1[i];
        }
        else
        {
            for (var i = 0; i < 3; i++)
                rhs[i] = 0.0;
        }
    }

    private static double GetAng(double[] qi, double[] qf, double[] e)
    {
        var temp = new double[3];
        temp[0] = qi[3] * qf[0] - qi[0] * qf[3] - qi[1] * qf[2] + qi[2] * qf[1];
        temp[1] = qi[3] * qf[1] - qi[1] * qf[3] - qi[2] * qf[0] + qi[0] * qf[2];
        temp[2] = qi[3] * qf[2] - qi[2] * qf[3] - qi[0] * qf[1] + qi[1] * qf[0];

        var ca = qi[0] * qf[0] + qi[1] * qf[1] + qi[2] * qf[2] + qi[3] * qf[3];
        var sa = Unvec(temp, e);
        return 2.0 * Math.Atan2(sa, ca);
    }

    private static double Unvec(double[] a, double[] au)
    {
        var amag = Math.Sqrt(a[0] * a[0] + a[1] * a[1] + a[2] * a[2]);
        if (amag > 0.0)
        {
            au[0] = a[0] / amag;
            au[1] = a[1] / amag;
            au[2] = a[2] / amag;
        }
        else
        {
            au[0] = 0.0;
            au[1] = 0.0;
            au[2] = 0.0;
        }
        return amag;
    }

    private static double[] Cross(double[] b, double[] c)
    {
        return new[]
        {
            b[1] * c[2] - b[2] * c[1],
            b[2] * c[0] - b[0] * c[2],
            b[0] * c[1] - b[1] * c[0]
        };
    }

    private static double[] GetRow(double[,] matrix, int row)
    {
        return new[] { matrix[row, 0], matrix[row, 1], matrix[row, 2] };
    }

    private sealed class QsplineWork
    {
        private readonly double[,] _a = new double[3, 3];
        private readonly double[,] _b = new double[3, 3];
        private readonly double[,] _c = new double[2, 3];
        private readonly double[] _d = new double[3];

        public void Slew3Init(double dt, double dtheta, double[] e, double[] wi, double[] ai, double[] wf, double[] af)
        {
            if (dt <= 0.0)
                return;

            var sa = Math.Sin(dtheta);
            var ca = Math.Cos(dtheta);

            var bvec = new double[3];
            if (dtheta > Eps)
            {
                var c1 = 0.5 * sa * dtheta / (1.0 - ca);
                var c2 = 0.5 * dtheta;
                var b0 = e[0] * wf[0] + e[1] * wf[1] + e[2] * wf[2];

                var bvec2 = Cross(e, wf);
                var bvec1 = Cross(bvec2, e);

                for (var i = 0; i < 3; i++)
                    bvec[i] = b0 * e[i] + c1 * bvec1[i] + c2 * bvec2[i];
            }
            else
            {
                for (var i = 0; i < 3; i++)
                    bvec[i] = wf[i];
            }

            for (var i = 0; i < 3; i++)
            {
                _b[0, i] = wi[i];
                _a[2, i] = e[i] * dtheta;
                _b[2, i] = bvec[i];

                _a[0, i] = _b[0, i] * dt;
                _a[1, i] = (_b[2, i] * dt - 3.0 * _a[2, i]);

                _b[1, i] = (2.0 * _a[0, i] + 2.0 * _a[1, i]) / dt;
                _c[0, i] = (2.0 * _b[0, i] + _b[1, i]) / dt;
                _c[1, i] = (_b[1, i] + 2.0 * _b[2, i]) / dt;

                _d[i] = (_c[0, i] + _c[1, i]) / dt;
            }
        }

        public void Slew3(double t, double dt, double[] qi, double[] q, double[] omega, double[] alpha, double[] jerk)
        {
            if (dt <= 0.0)
                return;

            var x = t / dt;
            var x1 = new[] { x - 1.0, 0.0 };
            x1[1] = x1[0] * x1[0];

            var th0 = new double[3];
            var th1 = new double[3];
            var th2 = new double[3];
            var th3 = new double[3];

            for (var i = 0; i < 3; i++)
            {
                th0[i] = ((x * _a[2, i] + x1[0] * _a[1, i]) * x + x1[1] * _a[0, i]) * x;
                th1[i] = (x * _b[2, i] + x1[0] * _b[1, i]) * x + x1[1] * _b[0, i];
                th2[i] = x * _c[1, i] + x1[0] * _c[0, i];
                th3[i] = _d[i];
            }

            var u = new double[3];
            var ang = Unvec(th0, u);
            var ca = Math.Cos(0.5 * ang);
            var sa = Math.Sin(0.5 * ang);

            q[0] = ca * qi[0] + sa * (u[2] * qi[1] - u[1] * qi[2] + u[0] * qi[3]);
            q[1] = ca * qi[1] + sa * (-u[2] * qi[0] + u[0] * qi[2] + u[1] * qi[3]);
            q[2] = ca * qi[2] + sa * (u[1] * qi[0] - u[0] * qi[1] + u[2] * qi[3]);
            q[3] = ca * qi[3] + sa * (-u[0] * qi[0] - u[1] * qi[1] - u[2] * qi[2]);

            ca = Math.Cos(ang);
            sa = Math.Sin(ang);

            if (ang > Eps)
            {
                var temp1 = new double[3];
                var temp2 = new double[3];
                var w = new double[3];
                var udot = new double[3];
                var wd1 = new double[3];
                var wd1xu = new double[3];
                var wd2 = new double[3];
                var wd2xu = new double[3];

                temp1 = Cross(u, th1);
                for (var i = 0; i < 3; i++)
                    w[i] = temp1[i] / ang;

                udot = Cross(w, u);
                var thd1 = u[0] * th1[0] + u[1] * th1[1] + u[2] * th1[2];

                for (var i = 0; i < 3; i++)
                    omega[i] = thd1 * u[i] + sa * udot[i] - (1.0 - ca) * w[i];

                var thd2 = udot[0] * th1[0] + udot[1] * th1[1] + udot[2] * th1[2] +
                           u[0] * th2[0] + u[1] * th2[1] + u[2] * th2[2];

                temp1 = Cross(u, th2);
                for (var i = 0; i < 3; i++)
                    wd1[i] = (temp1[i] - 2.0 * thd1 * w[i]) / ang;

                wd1xu = Cross(wd1, u);

                var temp0 = new double[3];
                for (var i = 0; i < 3; i++)
                    temp0[i] = thd1 * u[i] - w[i];

                temp1 = Cross(omega, temp0);

                for (var i = 0; i < 3; i++)
                    alpha[i] = thd2 * u[i] + sa * wd1xu[i] - (1.0 - ca) * wd1[i] +
                               thd1 * udot[i] + temp1[i];

                var w2 = w[0] * w[0] + w[1] * w[1] + w[2] * w[2];

                var thd3 = wd1xu[0] * th1[0] + wd1xu[1] * th1[1] + wd1xu[2] * th1[2] -
                           w2 * (u[0] * th1[0] + u[1] * th1[1] + u[2] * th1[2]) +
                           2.0 * (udot[0] * th2[0] + udot[1] * th2[1] + udot[2] * th2[2]) +
                           u[0] * th3[0] + u[1] * th3[1] + u[2] * th3[2];

                temp1 = Cross(th1, th2);
                for (var i = 0; i < 3; i++)
                    temp1[i] /= ang;

                temp2 = Cross(u, th3);

                var td2 = (th1[0] * th1[0] + th1[1] * th1[1] + th1[2] * th1[2]) / ang;
                var ut2 = u[0] * th2[0] + u[1] * th2[1] + u[2] * th2[2];
                var wwd = w[0] * wd1[0] + w[1] * wd1[1] + w[2] * wd1[2];

                for (var i = 0; i < 3; i++)
                    wd2[i] = (temp1[i] + temp2[i] - 2.0 * (td2 + ut2) * w[i] -
                              4.0 * thd1 * wd1[i]) / ang;

                wd2xu = Cross(wd2, u);

                for (var i = 0; i < 3; i++)
                    temp2[i] = thd2 * u[i] + thd1 * udot[i] - wd1[i];

                temp1 = Cross(omega, temp2);
                temp2 = Cross(alpha, temp0);

                for (var i = 0; i < 3; i++)
                    jerk[i] = thd3 * u[i] + sa * wd2xu[i] - (1.0 - ca) * wd2[i] +
                              2.0 * thd2 * udot[i] + thd1 * ((1.0 + ca) * wd1xu[i] - w2 * u[i] - sa * wd1[i]) -
                              wwd * sa * u[i] + temp1[i] + temp2[i];
            }
            else
            {
                var temp1 = Cross(th1, th2);
                for (var i = 0; i < 3; i++)
                {
                    omega[i] = th1[i];
                    alpha[i] = th2[i];
                    jerk[i] = th3[i] - 0.5 * temp1[i];
                }
            }
        }
    }

    private readonly struct QuaternionD
    {
        public readonly double W;
        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public QuaternionD(double w, double x, double y, double z)
        {
            W = w;
            X = x;
            Y = y;
            Z = z;
        }

        public static QuaternionD FromQuaternion(Quaternion q) => new QuaternionD(q.W, q.X, q.Y, q.Z);

        public Quaternion ToQuaternion() => new Quaternion((float)X, (float)Y, (float)Z, (float)W);

        public QuaternionD Negate() => new QuaternionD(-W, -X, -Y, -Z);

        public static double Dot(QuaternionD a, QuaternionD b) => a.W * b.W + a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        public static QuaternionD Slerp(QuaternionD a, QuaternionD b, double t)
        {
            var dot = Dot(a, b);
            if (dot < 0.0)
            {
                b = b.Negate();
                dot = -dot;
            }

            if (dot > 0.9995)
            {
                var w = a.W + (b.W - a.W) * t;
                var x = a.X + (b.X - a.X) * t;
                var y = a.Y + (b.Y - a.Y) * t;
                var z = a.Z + (b.Z - a.Z) * t;
                var inv = 1.0 / Math.Sqrt(w * w + x * x + y * y + z * z);
                return new QuaternionD(w * inv, x * inv, y * inv, z * inv);
            }

            var theta0 = Math.Acos(dot);
            var theta = theta0 * t;
            var sinTheta = Math.Sin(theta);
            var sinTheta0 = Math.Sin(theta0);

            var s0 = Math.Cos(theta) - dot * sinTheta / sinTheta0;
            var s1 = sinTheta / sinTheta0;

            return new QuaternionD(
                s0 * a.W + s1 * b.W,
                s0 * a.X + s1 * b.X,
                s0 * a.Y + s1 * b.Y,
                s0 * a.Z + s1 * b.Z
            );
        }
    }
}
