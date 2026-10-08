namespace MusicAura.Core.Dsp;

/// <summary>
/// Calibration probes: short bursts of band-limited pseudo-random noise. Every (device,
/// repetition) pair gets its own seed, so the bursts are mutually uncorrelated and each one
/// can be found in a single microphone recording even when bursts overlap in time.
/// </summary>
public static class ProbeSignal
{
    public static float[] Create(int sampleRate, double seconds, int seed, double lowHz = 400, double highHz = 6000, double peak = 0.35)
    {
        var length = (int)Math.Round(seconds * sampleRate);
        if (length < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        // Generate longer than needed, band-limit in the frequency domain and cut the middle
        // so the circular filtering does not leave edge artefacts in the probe.
        var n = Fft.NextPowerOfTwo(length * 2);
        var re = new double[n];
        var im = new double[n];
        var rng = new XorShiftRandom(seed);
        for (var i = 0; i < n; i++)
        {
            re[i] = rng.NextGaussian();
        }

        var fft = Fft.ForSize(n);
        fft.Forward(re, im);
        var lowBin = (int)Math.Ceiling(lowHz * n / sampleRate);
        var highBin = (int)Math.Floor(highHz * n / sampleRate);
        for (var k = 0; k <= n / 2; k++)
        {
            if (k < lowBin || k > highBin)
            {
                re[k] = im[k] = 0;
                if (k > 0 && k < n / 2)
                {
                    re[n - k] = im[n - k] = 0;
                }
            }
        }

        fft.Inverse(re, im);

        var offset = (n - length) / 2;
        var result = new float[length];
        double max = 0;
        for (var i = 0; i < length; i++)
        {
            max = Math.Max(max, Math.Abs(re[offset + i]));
        }

        var fade = Math.Min(length / 8, (int)(0.008 * sampleRate));
        for (var i = 0; i < length; i++)
        {
            var g = 1.0;
            if (i < fade)
            {
                g = 0.5 - 0.5 * Math.Cos(Math.PI * i / fade);
            }
            else if (i >= length - fade)
            {
                g = 0.5 - 0.5 * Math.Cos(Math.PI * (length - 1 - i) / fade);
            }

            result[i] = (float)(re[offset + i] / max * peak * g);
        }

        return result;
    }
}

/// <summary>Deterministic, allocation-free PRNG (xorshift64*), stable across platforms.</summary>
public sealed class XorShiftRandom
{
    private ulong _state;
    private double? _spare;

    public XorShiftRandom(int seed)
    {
        _state = 0x9E3779B97F4A7C15UL ^ (ulong)(uint)seed * 0xBF58476D1CE4E5B9UL;
        if (_state == 0)
        {
            _state = 1;
        }

        for (var i = 0; i < 8; i++)
        {
            NextULong();
        }
    }

    public ulong NextULong()
    {
        _state ^= _state >> 12;
        _state ^= _state << 25;
        _state ^= _state >> 27;
        return _state * 0x2545F4914F6CDD1DUL;
    }

    /// <summary>Uniform in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    public double NextGaussian()
    {
        if (_spare is { } s)
        {
            _spare = null;
            return s;
        }

        double u, v, q;
        do
        {
            u = NextDouble() * 2 - 1;
            v = NextDouble() * 2 - 1;
            q = u * u + v * v;
        }
        while (q >= 1 || q == 0);

        var f = Math.Sqrt(-2 * Math.Log(q) / q);
        _spare = v * f;
        return u * f;
    }
}
