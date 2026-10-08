namespace MusicAura.Core.Sync;

/// <summary>
/// PI controller that turns a timing error into a playback-rate correction. The correction
/// is applied by reading the timeline slightly faster or slower (band-limited interpolation),
/// so an output converges on its target without audible jumps. Maximum adjustment is a few
/// tenths of a percent — far below what anyone can hear as a pitch change.
/// </summary>
public sealed class RateController
{
    private double _integral;
    private double _filtered;
    private bool _hasFiltered;

    /// <summary>Proportional gain in 1/s: an error of e seconds changes the rate by Kp·e.</summary>
    public double Kp { get; init; } = 0.5;

    /// <summary>Integral gain in 1/s²; absorbs constant clock drift between output and timeline.</summary>
    public double Ki { get; init; } = 0.05;

    /// <summary>Largest relative rate change (0.003 = 0.3 %, about 5 cents).</summary>
    public double MaxAdjust { get; init; } = 0.003;

    /// <summary>Errors larger than this are corrected by a jump instead of slewing.</summary>
    public double HardResyncSeconds { get; init; } = 0.045;

    /// <summary>Time constant of the measurement smoothing filter.</summary>
    public double FilterSeconds { get; init; } = 0.25;

    public double LastAdjust { get; private set; }

    public double FilteredError => _filtered;

    public void Reset()
    {
        _integral = 0;
        _filtered = 0;
        _hasFiltered = false;
        LastAdjust = 0;
    }

    /// <summary>
    /// Feeds the current error (positive = content is ahead of where it should be) and the
    /// time since the previous update; returns the content step per output frame.
    /// </summary>
    public double Update(double errorSeconds, double dtSeconds)
    {
        dtSeconds = Math.Clamp(dtSeconds, 0, 0.5);
        if (!_hasFiltered)
        {
            _filtered = errorSeconds;
            _hasFiltered = true;
        }
        else
        {
            var alpha = FilterSeconds <= 0 ? 1 : 1 - Math.Exp(-dtSeconds / FilterSeconds);
            _filtered += alpha * (errorSeconds - _filtered);
        }

        var e = _filtered;
        if (Ki > 0)
        {
            var limit = MaxAdjust / Ki;
            _integral = Math.Clamp(_integral + e * dtSeconds, -limit, limit);
        }

        LastAdjust = Math.Clamp(Kp * e + Ki * _integral, -MaxAdjust, MaxAdjust);
        return 1 - LastAdjust;
    }
}
