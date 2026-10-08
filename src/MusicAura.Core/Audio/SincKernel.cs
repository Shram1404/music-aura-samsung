namespace MusicAura.Core.Audio;

/// <summary>
/// Polyphase table of a Kaiser-windowed sinc. Used for band-limited interpolation at
/// arbitrary fractional positions, both for sample-rate conversion and for the tiny
/// playback-rate corrections that keep every output locked to the shared timeline.
/// </summary>
public sealed class SincKernel
{
    private readonly float[] _table;

    /// <param name="halfTaps">Taps on each side of the interpolation point (filter length = 2 * halfTaps).</param>
    /// <param name="cutoff">Cut-off relative to the input Nyquist frequency (0..1].</param>
    /// <param name="phases">Number of tabulated fractional phases; intermediate phases are linearly interpolated.</param>
    /// <param name="beta">Kaiser window beta.</param>
    public SincKernel(int halfTaps = 16, double cutoff = 1.0, int phases = 512, double beta = 8.0)
    {
        if (halfTaps < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(halfTaps));
        }

        if (cutoff <= 0 || cutoff > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(cutoff));
        }

        HalfTaps = halfTaps;
        Taps = halfTaps * 2;
        Phases = phases;
        Cutoff = cutoff;
        _table = new float[(phases + 1) * Taps];

        var i0Beta = BesselI0(beta);
        for (var p = 0; p <= phases; p++)
        {
            var frac = p / (double)phases;
            var row = _table.AsSpan(p * Taps, Taps);
            double sum = 0;
            for (var m = 0; m < Taps; m++)
            {
                // Tap m multiplies input sample (floor(pos) + m - (halfTaps - 1)).
                var t = m - (halfTaps - 1) - frac;
                var x = t / halfTaps;
                var window = Math.Abs(x) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - x * x)) / i0Beta;
                var v = cutoff * Sinc(cutoff * t) * window;
                row[m] = (float)v;
                sum += v;
            }

            // Unity DC gain for every phase.
            for (var m = 0; m < Taps; m++)
            {
                row[m] = (float)(row[m] / sum);
            }
        }
    }

    public int HalfTaps { get; }

    public int Taps { get; }

    public int Phases { get; }

    public double Cutoff { get; }

    /// <summary>Fills <paramref name="coefficients"/> (length <see cref="Taps"/>) for a fractional offset in [0, 1).</summary>
    public void GetCoefficients(double fraction, Span<float> coefficients)
    {
        var p = fraction * Phases;
        var ip = (int)p;
        if (ip >= Phases)
        {
            ip = Phases - 1;
        }

        var a = (float)(p - ip);
        var r0 = _table.AsSpan(ip * Taps, Taps);
        var r1 = _table.AsSpan((ip + 1) * Taps, Taps);
        for (var m = 0; m < Taps; m++)
        {
            coefficients[m] = r0[m] + a * (r1[m] - r0[m]);
        }
    }

    /// <summary>
    /// Interpolates one stereo frame. <paramref name="window"/> holds interleaved stereo frames
    /// starting at input index <c>floor(pos) - (HalfTaps - 1)</c>.
    /// </summary>
    public static void InterpolateStereo(ReadOnlySpan<float> window, ReadOnlySpan<float> coefficients, out float left, out float right)
    {
        float l = 0, r = 0;
        for (var m = 0; m < coefficients.Length; m++)
        {
            var c = coefficients[m];
            l += c * window[m * 2];
            r += c * window[m * 2 + 1];
        }

        left = l;
        right = r;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-12)
        {
            return 1;
        }

        var px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    private static double BesselI0(double x)
    {
        // Power series; converges quickly for the beta values used here.
        double sum = 1, term = 1, half = x / 2;
        for (var k = 1; k < 64; k++)
        {
            term *= half / k;
            var t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }
}
