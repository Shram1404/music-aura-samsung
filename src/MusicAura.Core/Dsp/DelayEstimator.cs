namespace MusicAura.Core.Dsp;

public sealed class DelayEstimatorOptions
{
    /// <summary>PHAT weighting exponent: 0 = plain cross-correlation, 1 = full PHAT.</summary>
    public double Beta { get; init; } = 0.75;

    public double LowHz { get; init; } = 300;

    public double HighHz { get; init; } = 7000;

    /// <summary>How far before the strongest peak to look for an earlier direct-path arrival.</summary>
    public double EarlyArrivalWindowSeconds { get; init; } = 0.02;

    /// <summary>An earlier peak counts as the direct path if it reaches this fraction of the strongest one.</summary>
    public double EarlyArrivalRatio { get; init; } = 0.4;
}

public readonly record struct DelayEstimate(bool Found, double LagSamples, double Confidence, double PeakValue)
{
    public static readonly DelayEstimate None = new(false, 0, 0, 0);
}

/// <summary>
/// Finds where a known reference signal occurs inside a recording using generalised
/// cross-correlation with (partial) phase transform weighting. PHAT whitening keeps the main
/// peak sharp and reliable in reverberant rooms; the plain (unweighted) correlation is then
/// used to check for an earlier, weaker direct-sound arrival, which PHAT tends to suppress
/// when a reflection is stronger.
/// </summary>
public static class DelayEstimator
{
    /// <summary>
    /// Returns the lag <c>L</c> (in samples, sub-sample accurate) for which
    /// <c>recording[L + i] ≈ a · reference[i]</c>, searching <c>L ∈ [minLag, maxLag]</c>.
    /// <see cref="DelayEstimate.Confidence"/> is the peak-to-noise ratio of the correlation.
    /// </summary>
    public static DelayEstimate Estimate(
        ReadOnlySpan<float> recording,
        ReadOnlySpan<float> reference,
        int sampleRate,
        int minLag,
        int maxLag,
        DelayEstimatorOptions? options = null)
    {
        options ??= new DelayEstimatorOptions();
        minLag = Math.Max(0, minLag);
        maxLag = Math.Min(maxLag, recording.Length - 1);
        if (reference.Length == 0 || maxLag < minLag)
        {
            return DelayEstimate.None;
        }

        var n = Fft.NextPowerOfTwo(recording.Length + reference.Length);
        var fft = Fft.ForSize(n);
        var xr = new double[n];
        var xi = new double[n];
        var yr = new double[n];
        var yi = new double[n];
        for (var i = 0; i < recording.Length; i++)
        {
            xr[i] = recording[i];
        }

        for (var i = 0; i < reference.Length; i++)
        {
            yr[i] = reference[i];
        }

        fft.Forward(xr, xi);
        fft.Forward(yr, yi);

        var lowBin = Math.Max(1, (int)Math.Floor(options.LowHz * n / sampleRate));
        var highBin = Math.Min(n / 2 - 1, (int)Math.Ceiling(options.HighHz * n / sampleRate));

        // Band-limited cross spectrum X·conj(Y): plain version in (yr, yi), weighted in (xr, xi).
        double maxMag = 0;
        for (var k = 0; k <= n / 2; k++)
        {
            if (k < lowBin || k > highBin)
            {
                xr[k] = xi[k] = yr[k] = yi[k] = 0;
                continue;
            }

            var gr = xr[k] * yr[k] + xi[k] * yi[k];
            var gi = xi[k] * yr[k] - xr[k] * yi[k];
            xr[k] = yr[k] = gr;
            xi[k] = yi[k] = gi;
            maxMag = Math.Max(maxMag, Math.Sqrt(gr * gr + gi * gi));
        }

        if (maxMag <= 0)
        {
            return DelayEstimate.None;
        }

        var floor = maxMag * 1e-6;
        for (var k = lowBin; k <= highBin; k++)
        {
            var mag = Math.Sqrt(xr[k] * xr[k] + xi[k] * xi[k]);
            var w = 1.0 / Math.Pow(mag + floor, options.Beta);
            xr[k] *= w;
            xi[k] *= w;
        }

        MakeHermitian(xr, xi, n);
        MakeHermitian(yr, yi, n);
        fft.Inverse(xr, xi);
        fft.Inverse(yr, yi);
        var weighted = xr;
        var plain = yr;

        var (peakIndex, peak) = MaxAbs(weighted, minLag, maxLag);
        if (peak <= 0)
        {
            return DelayEstimate.None;
        }

        var exclude = Math.Max(4, sampleRate / 500);
        var rms = RmsExcluding(weighted, minLag, maxLag, peakIndex, exclude, peak);
        var confidence = peak / rms;

        // Earlier direct sound: look in the plain correlation, where peak heights are proportional
        // to arrival amplitudes.
        var chosen = peakIndex;
        var refined = plain;
        var plainPeak = Math.Abs(plain[peakIndex]);
        for (var d = -2; d <= 2; d++)
        {
            var idx = peakIndex + d;
            if (idx >= minLag && idx <= maxLag)
            {
                plainPeak = Math.Max(plainPeak, Math.Abs(plain[idx]));
            }
        }

        var plainRms = RmsExcluding(plain, minLag, maxLag, peakIndex, exclude, plainPeak);
        var earlyFrom = Math.Max(minLag + 1, peakIndex - (int)(options.EarlyArrivalWindowSeconds * sampleRate));
        var threshold = Math.Max(options.EarlyArrivalRatio * plainPeak, 8 * plainRms);
        for (var l = earlyFrom; l < peakIndex - 3; l++)
        {
            var v = Math.Abs(plain[l]);
            if (v >= threshold && v >= Math.Abs(plain[l - 1]) && v >= Math.Abs(plain[l + 1]))
            {
                chosen = l;
                break;
            }
        }

        if (chosen == peakIndex)
        {
            refined = weighted;
        }

        // Parabolic interpolation for sub-sample precision.
        var lag = (double)chosen;
        if (chosen > minLag && chosen < maxLag)
        {
            var a = Math.Abs(refined[chosen - 1]);
            var b = Math.Abs(refined[chosen]);
            var c = Math.Abs(refined[chosen + 1]);
            var denom = a - 2 * b + c;
            if (Math.Abs(denom) > 1e-18)
            {
                var delta = 0.5 * (a - c) / denom;
                if (Math.Abs(delta) <= 1)
                {
                    lag += delta;
                }
            }
        }

        return new DelayEstimate(true, lag, confidence, Math.Abs(weighted[chosen]));
    }

    private static void MakeHermitian(double[] re, double[] im, int n)
    {
        for (var k = 1; k < n / 2; k++)
        {
            re[n - k] = re[k];
            im[n - k] = -im[k];
        }

        im[0] = 0;
        im[n / 2] = 0;
    }

    private static (int Index, double Value) MaxAbs(double[] data, int from, int to)
    {
        var index = from;
        double max = 0;
        for (var l = from; l <= to; l++)
        {
            var v = Math.Abs(data[l]);
            if (v > max)
            {
                max = v;
                index = l;
            }
        }

        return (index, max);
    }

    private static double RmsExcluding(double[] data, int from, int to, int center, int exclude, double peak)
    {
        double sumSq = 0;
        var count = 0;
        for (var l = from; l <= to; l++)
        {
            if (Math.Abs(l - center) <= exclude)
            {
                continue;
            }

            sumSq += data[l] * data[l];
            count++;
        }

        var rms = count > 0 ? Math.Sqrt(sumSq / count) : 0;
        return rms > 0 ? rms : Math.Max(peak * 1e-3, 1e-30);
    }
}
