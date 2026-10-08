namespace MusicAura.Core.Sync;

public readonly record struct PositionFit(double Slope, double Intercept, double ResidualSeconds, int Points, double SpanSeconds)
{
    /// <summary>Renderer clock rate relative to ours minus one, in ppm.</summary>
    public double DriftPpm => (Slope - 1) * 1e6;

    /// <summary>Renderer playback position (seconds) predicted at time <paramref name="t"/> (seconds since tracker base).</summary>
    public double PositionAt(double t) => Intercept + Slope * t;
}

/// <summary>
/// Turns polled renderer positions (UPnP GetPositionInfo RelTime, usually whole seconds)
/// into a precise line "position = a + b·t". With whole-second values, only the moments the
/// counter ticks over carry information; each tick is located between two polls. A
/// least-squares fit over many ticks gives the renderer's clock rate (→ drift) and an
/// estimate of its playback position (→ latency without a microphone).
/// </summary>
public sealed class PositionTracker
{
    private readonly List<(double T, double Pos)> _points = [];
    private readonly int _maxPoints;
    private double? _lastT;
    private double? _lastPos;
    private bool _fractional;

    public PositionTracker(int maxPoints = 3600)
    {
        _maxPoints = maxPoints;
    }

    public int Count => _points.Count;

    public void Reset()
    {
        _points.Clear();
        _lastT = null;
        _lastPos = null;
        _fractional = false;
    }

    /// <summary>Adds a poll result. <paramref name="t"/>: seconds since an arbitrary fixed base (midpoint of the request).</summary>
    /// <returns>False if the position jumped (renderer restarted or seeked) and history was cleared.</returns>
    public bool Add(double t, double position)
    {
        if (!_fractional && Math.Abs(position - Math.Round(position)) > 0.0005)
        {
            // The renderer reports sub-second positions: every sample is a data point.
            _fractional = true;
            _points.Clear();
        }

        var ok = true;
        if (_lastT is { } lt && _lastPos is { } lp)
        {
            var expected = lp + (t - lt);
            if (position < lp - 0.5 || position > expected + 2.5)
            {
                Reset();
                ok = false;
            }
        }

        if (_fractional)
        {
            AddPoint(t, position);
        }
        else if (_lastT is { } prevT && _lastPos is { } prevPos && Math.Abs(position - (prevPos + 1)) < 0.001)
        {
            // The counter ticked between the previous poll and this one.
            AddPoint((prevT + t) / 2, position);
        }

        _lastT = t;
        _lastPos = position;
        return ok;
    }

    public PositionFit? Fit(int minPoints = 8)
    {
        if (_points.Count < minPoints)
        {
            return null;
        }

        var fit = LeastSquares(_points);
        if (fit is null)
        {
            return null;
        }

        // One round of outlier rejection (e.g. a poll delayed by a busy TV).
        var limit = Math.Max(3 * fit.Value.ResidualSeconds, 0.02);
        var inliers = _points.Where(p => Math.Abs(p.Pos - fit.Value.PositionAt(p.T)) <= limit).ToList();
        if (inliers.Count >= minPoints && inliers.Count < _points.Count)
        {
            fit = LeastSquares(inliers) ?? fit;
        }

        return fit;
    }

    private void AddPoint(double t, double pos)
    {
        _points.Add((t, pos));
        if (_points.Count > _maxPoints)
        {
            _points.RemoveAt(0);
        }
    }

    private static PositionFit? LeastSquares(IReadOnlyList<(double T, double Pos)> points)
    {
        var n = points.Count;
        double mt = 0, mp = 0;
        foreach (var (t, p) in points)
        {
            mt += t;
            mp += p;
        }

        mt /= n;
        mp /= n;
        double stt = 0, stp = 0;
        foreach (var (t, p) in points)
        {
            stt += (t - mt) * (t - mt);
            stp += (t - mt) * (p - mp);
        }

        if (stt <= 1e-9)
        {
            return null;
        }

        var slope = stp / stt;
        var intercept = mp - slope * mt;
        double ss = 0;
        foreach (var (t, p) in points)
        {
            var r = p - (intercept + slope * t);
            ss += r * r;
        }

        var residual = Math.Sqrt(ss / Math.Max(1, n - 2));
        return new PositionFit(slope, intercept, residual, n, points[^1].T - points[0].T);
    }
}
