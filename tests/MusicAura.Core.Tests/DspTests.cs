using MusicAura.Core.Audio;
using MusicAura.Core.Dsp;

namespace MusicAura.Core.Tests;

public class DspTests
{
    private const int Rate = 44100;

    [Fact]
    public void Fft_round_trip_restores_signal()
    {
        var n = 1024;
        var re = new double[n];
        var im = new double[n];
        var rng = new XorShiftRandom(5);
        var original = new double[n];
        for (var i = 0; i < n; i++)
        {
            original[i] = re[i] = rng.NextGaussian();
        }

        var fft = Fft.ForSize(n);
        fft.Forward(re, im);
        fft.Inverse(re, im);
        for (var i = 0; i < n; i++)
        {
            Assert.Equal(original[i], re[i], 9);
            Assert.Equal(0, im[i], 9);
        }
    }

    [Fact]
    public void Fft_of_cosine_has_single_bin()
    {
        var n = 256;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
        {
            re[i] = Math.Cos(2 * Math.PI * 10 * i / n);
        }

        Fft.ForSize(n).Forward(re, im);
        Assert.Equal(n / 2.0, re[10], 6);
        Assert.Equal(n / 2.0, re[n - 10], 6);
        Assert.True(Math.Abs(re[11]) < 1e-6);
    }

    [Fact]
    public void Probe_signals_are_band_limited_and_mutually_uncorrelated()
    {
        var a = ProbeSignal.Create(Rate, 0.4, 1);
        var b = ProbeSignal.Create(Rate, 0.4, 2);
        Assert.Equal((int)(0.4 * Rate), a.Length);
        Assert.InRange(a.Max(Math.Abs), 0.3, 0.36);

        double aa = 0, ab = 0;
        for (var i = 0; i < a.Length; i++)
        {
            aa += a[i] * a[i];
            ab += a[i] * b[i];
        }

        Assert.True(Math.Abs(ab / aa) < 0.05, $"cross-correlation {ab / aa}");
        Assert.Equal(a, ProbeSignal.Create(Rate, 0.4, 1)); // deterministic
    }

    [Theory]
    [InlineData(12345.0)]
    [InlineData(23456.37)]
    [InlineData(100.5)]
    public void Delay_estimator_finds_probe_with_subsample_accuracy(double delay)
    {
        var probe = ProbeSignal.Create(Rate, 0.4, 42);
        var recording = new float[Rate * 2];
        AddDelayed(recording, probe, delay, 0.3f);
        AddNoise(recording, 0.01, 7);

        var est = DelayEstimator.Estimate(recording, probe, Rate, 0, recording.Length - probe.Length);
        Assert.True(est.Found);
        Assert.True(est.Confidence > 20, $"confidence {est.Confidence}");
        Assert.InRange(est.LagSamples, delay - 0.3, delay + 0.3);
    }

    [Fact]
    public void Delay_estimator_prefers_direct_sound_over_stronger_reflection()
    {
        var probe = ProbeSignal.Create(Rate, 0.4, 3);
        var recording = new float[Rate * 2];
        AddDelayed(recording, probe, 20000, 0.15f);        // direct sound, partly blocked
        AddDelayed(recording, probe, 20000 + 300, 0.3f);   // wall reflection 6.8 ms later, louder
        AddNoise(recording, 0.005, 9);

        var est = DelayEstimator.Estimate(recording, probe, Rate, 0, recording.Length - probe.Length);
        Assert.InRange(est.LagSamples, 19999.5, 20000.5);
    }

    [Fact]
    public void Delay_estimator_separates_overlapping_probes_by_seed()
    {
        var p1 = ProbeSignal.Create(Rate, 0.4, 100);
        var p2 = ProbeSignal.Create(Rate, 0.4, 200);
        var recording = new float[Rate * 2];
        AddDelayed(recording, p1, 30000, 0.2f);
        AddDelayed(recording, p2, 34000, 0.4f); // overlaps p1 and is louder
        AddNoise(recording, 0.01, 11);

        var e1 = DelayEstimator.Estimate(recording, p1, Rate, 0, recording.Length - p1.Length);
        var e2 = DelayEstimator.Estimate(recording, p2, Rate, 0, recording.Length - p2.Length);
        Assert.InRange(e1.LagSamples, 29999.5, 30000.5);
        Assert.InRange(e2.LagSamples, 33999.5, 34000.5);
    }

    [Fact]
    public void Delay_estimator_reports_low_confidence_without_probe()
    {
        var probe = ProbeSignal.Create(Rate, 0.4, 5);
        var recording = new float[Rate * 2];
        AddNoise(recording, 0.05, 13);
        var est = DelayEstimator.Estimate(recording, probe, Rate, 0, recording.Length - probe.Length);
        Assert.True(est.Confidence < 9, $"confidence {est.Confidence}");
    }

    [Fact]
    public void Delay_estimator_handles_reverb_and_polarity_inversion()
    {
        var probe = ProbeSignal.Create(Rate, 0.4, 77);
        var recording = new float[Rate * 2];
        var rng = new XorShiftRandom(3);
        AddDelayed(recording, probe, 15000, -0.25f); // speaker wired in reverse
        for (var k = 1; k < 60; k++)
        {
            // Diffuse decaying reflections over ~100 ms.
            AddDelayed(recording, probe, 15000 + 40 + k * 70 + rng.NextDouble() * 30, (float)(0.12 * Math.Exp(-k / 15.0) * (rng.NextDouble() - 0.5)));
        }

        AddNoise(recording, 0.01, 17);
        var est = DelayEstimator.Estimate(recording, probe, Rate, 0, recording.Length - probe.Length);
        Assert.InRange(est.LagSamples, 14999.5, 15000.5);
    }

    [Fact]
    public void Resampler_converts_48k_to_44k1_preserving_frequency_and_level()
    {
        var resampler = new StreamingResampler(48000, 44100);
        var input = new float[48000 * 2];
        for (var i = 0; i < 48000; i++)
        {
            input[i * 2] = input[i * 2 + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 48000));
        }

        var output = new float[50000 * 2];
        var produced = 0;
        for (var offset = 0; offset < input.Length; offset += 2000)
        {
            resampler.Push(input.AsSpan(offset, Math.Min(2000, input.Length - offset)));
            produced += resampler.Pull(output.AsSpan(produced * 2));
        }

        Assert.InRange(produced, 44000, 44100);

        // Compare against an ideal 1 kHz sine at 44.1 kHz (skip filter warm-up).
        double err = 0, sig = 0;
        for (var i = 1000; i < 43000; i++)
        {
            var ideal = 0.5 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0);
            err += Math.Pow(output[i * 2] - ideal, 2);
            sig += ideal * ideal;
        }

        var snrDb = 10 * Math.Log10(sig / err);
        Assert.True(snrDb > 60, $"SNR {snrDb:0.0} dB");
    }

    [Fact]
    public void Resampler_passes_equal_rates_through()
    {
        var resampler = new StreamingResampler(44100, 44100);
        var input = Enumerable.Range(0, 200).Select(i => (float)i).ToArray();
        resampler.Push(input);
        var output = new float[200];
        Assert.Equal(100, resampler.Pull(output));
        Assert.Equal(input, output);
    }

    [Fact]
    public void Sinc_kernel_is_identity_at_integer_positions()
    {
        var kernel = new SincKernel(16, 1.0);
        var c = new float[kernel.Taps];
        kernel.GetCoefficients(0, c);
        for (var m = 0; m < c.Length; m++)
        {
            Assert.Equal(m == kernel.HalfTaps - 1 ? 1f : 0f, c[m], 5);
        }
    }

    internal static void AddDelayed(float[] target, float[] signal, double delay, float gain)
    {
        // Fractional delay via windowed-sinc interpolation of the signal.
        var kernel = new SincKernel(16, 1.0);
        var coeff = new float[kernel.Taps];
        var whole = (int)Math.Floor(delay);
        var frac = delay - whole;
        if (frac < 1e-9)
        {
            for (var i = 0; i < signal.Length && whole + i < target.Length; i++)
            {
                target[whole + i] += gain * signal[i];
            }

            return;
        }

        // target[n] = signal(n - delay) => evaluate signal at position p = n - delay.
        kernel.GetCoefficients(1 - frac, coeff);
        for (var n = whole; n < Math.Min(target.Length, whole + signal.Length + 1); n++)
        {
            var p = n - delay;
            var i = (int)Math.Floor(p);
            double v = 0;
            for (var m = 0; m < kernel.Taps; m++)
            {
                var idx = i + m - (kernel.HalfTaps - 1);
                if (idx >= 0 && idx < signal.Length)
                {
                    v += coeff[m] * signal[idx];
                }
            }

            target[n] += (float)(gain * v);
        }
    }

    internal static void AddNoise(float[] target, double level, int seed)
    {
        var rng = new XorShiftRandom(seed);
        for (var i = 0; i < target.Length; i++)
        {
            target[i] += (float)(rng.NextGaussian() * level);
        }
    }
}
