using System.Globalization;
using MusicAura.Core.Audio;
using MusicAura.Core.Dsp;
using MusicAura.Core.Engine.Sinks;
using MusicAura.Core.Sync;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Engine;

public sealed record SinkCalibrationResult(
    string SinkId,
    string SinkName,
    bool Success,
    double? CorrectionSeconds,
    double? LatencySeconds,
    double Confidence,
    double? LevelDb,
    int ProbesHeard,
    int ProbesSent,
    string? Message);

public sealed record CalibrationReport(DateTime TimeUtc, bool Success, IReadOnlyList<SinkCalibrationResult> Sinks, string Summary);

/// <summary>
/// Measures, with the phone's microphone, when each output really plays a given timeline
/// frame. Every output gets its own noise bursts (distinct seeds) while all others stay silent;
/// GCC-PHAT locates each burst in the recording. The difference between when a burst was
/// expected and when it was heard corrects that output's latency model.
/// </summary>
internal static class AcousticCalibration
{
    private const double ConfidenceComplete = 9;
    private const double ConfidencePartial = 14;
    private const double ConsistencySeconds = 0.006;

    public static async Task<CalibrationReport> RunAsync(
        EngineContext ctx,
        IReadOnlyList<SinkBase> sinks,
        IMicrophone microphone,
        double targetLatency,
        long earliestProbeDueNs,
        CancellationToken ct)
    {
        var log = ctx.Log;
        var opt = ctx.Options;
        var rate = ctx.SampleRate;
        var probeFrames = (int)Math.Round(opt.ProbeSeconds * rate);
        var slotFrames = probeFrames + (int)(0.25 * rate);
        IMicrophoneCapture capture;
        try
        {
            capture = microphone.Start(rate);
        }
        catch (Exception ex)
        {
            log.Error("calib", "microphone unavailable", ex);
            return Failed(sinks, $"microphone unavailable: {ex.Message}");
        }

        // Schedule the bursts only once the microphone is running.
        var now = ctx.Clock.NowNs;
        var firstDue = Math.Max(now + Ns.FromSeconds(0.4), earliestProbeDueNs);
        var frame0 = (long)Math.Ceiling(ctx.Timeline.FrameAt(firstDue));
        var seedBase = Environment.TickCount;

        var plans = new List<ProbePlan>();
        var slot = 0;
        for (var rep = 0; rep < Math.Max(1, opt.ProbeRepetitions); rep++)
        {
            foreach (var sink in sinks)
            {
                var frame = frame0 + (long)slot * slotFrames;
                var signal = ProbeSignal.Create(rate, opt.ProbeSeconds, unchecked(seedBase + slot * 7919), peak: opt.ProbeLevel);
                var (earliest, latest) = sink.ProbeArrivalWindow(frame, targetLatency);
                plans.Add(new ProbePlan(sink, rep, frame, signal, sink.ExpectedAudibleNs(frame, targetLatency), earliest, latest));
                sink.Overlays?.Add(frame, signal);
                slot++;
            }
        }

        log.Info("calib", string.Create(CultureInfo.InvariantCulture,
            $"{plans.Count} probes for {sinks.Count} outputs, target latency {targetLatency * 1000:0} ms"));

        var micRate = capture.SampleRate;
        var templates = new Dictionary<ProbePlan, float[]>();
        foreach (var p in plans)
        {
            templates[p] = micRate == rate ? p.Signal : ResampleMono(p.Signal, rate, micRate);
        }

        var deadline = plans.Max(p => p.LatestNs) + Ns.FromSeconds(opt.ProbeSeconds + 0.3);
        try
        {
            while (true)
            {
                await Task.Delay(400, ct).ConfigureAwait(false);
                var samples = capture.Snapshot(out var recStart);
                foreach (var p in plans.Where(p => p.Result is null))
                {
                    TryResolve(p, templates[p], samples, recStart, micRate);
                }

                if (plans.All(p => p.Result is not null) || ctx.Clock.NowNs > deadline)
                {
                    break;
                }
            }
        }
        finally
        {
            capture.Dispose();
            foreach (var s in sinks)
            {
                s.Overlays?.Clear();
            }
        }

        var applyNs = ctx.Clock.NowNs;
        var results = new List<SinkCalibrationResult>();
        foreach (var sink in sinks)
        {
            results.Add(Summarize(sink, plans.Where(p => p.Sink == sink).ToList(), applyNs, log));
        }

        var ok = results.All(r => r.Success);
        var summary = string.Join("; ", results.Select(r => r.Success
            ? string.Create(CultureInfo.InvariantCulture, $"{r.SinkName}: {r.LatencySeconds * 1000:0} ms (corr {r.CorrectionSeconds * 1000:+0;-0} ms, q {r.Confidence:0})")
            : $"{r.SinkName}: {r.Message}"));
        log.Info("calib", summary);
        return new CalibrationReport(DateTime.UtcNow, ok, results, summary);
    }

    private static void TryResolve(ProbePlan p, float[] template, float[] samples, long recStartNs, int micRate)
    {
        // Lag search range: where the probe start can be, in recording samples.
        var lo = (int)Math.Floor(Ns.ToSeconds(p.EarliestNs - recStartNs) * micRate);
        var hi = (int)Math.Ceiling(Ns.ToSeconds(p.LatestNs - recStartNs) * micRate);
        var maxStart = samples.Length - template.Length;
        var from = Math.Max(0, lo);
        if (maxStart < from || maxStart - p.LastAttemptMaxStart < micRate / 4)
        {
            return; // not enough (new) audio yet
        }

        p.LastAttemptMaxStart = maxStart;
        var to = Math.Min(hi, maxStart);
        var complete = to == hi;
        var segment = samples.AsSpan(from, to - from + template.Length);
        var est = DelayEstimator.Estimate(segment, template, micRate, 0, to - from);
        var threshold = complete ? ConfidenceComplete : ConfidencePartial;
        if (est.Found && est.Confidence >= threshold)
        {
            var lag = from + est.LagSamples;
            p.Result = new ProbeResult(true, recStartNs + (long)(lag / micRate * Ns.PerSecond), est.Confidence, LevelDb(samples, (int)lag, template.Length));
        }
        else if (complete)
        {
            p.Result = new ProbeResult(false, 0, est.Confidence, null);
        }
    }

    private static SinkCalibrationResult Summarize(SinkBase sink, List<ProbePlan> plans, long applyNs, Diagnostics.AuraLog log)
    {
        var heard = plans.Where(p => p.Result is { Heard: true }).ToList();
        var bestConfidence = plans.Select(p => p.Result?.Confidence ?? 0).DefaultIfEmpty(0).Max();
        if (heard.Count == 0)
        {
            return new SinkCalibrationResult(sink.Id, sink.Name, false, null, null, bestConfidence, null, 0, plans.Count,
                "calibration signal not heard — turn this device up or move the phone closer");
        }

        var errors = heard
            .Select(p => (Error: Ns.ToSeconds(p.Result!.ArrivalNs - p.ExpectedNs), p.Result.Confidence, p.Result.LevelDb))
            .OrderBy(e => e.Error)
            .ToList();
        double correction;
        string? message = null;
        if (errors.Count == 1)
        {
            correction = errors[0].Error;
            if (plans.Count > 1)
            {
                message = "only one of the calibration bursts was heard";
            }
        }
        else if (errors[^1].Error - errors[0].Error <= ConsistencySeconds)
        {
            correction = errors.Average(e => e.Error);
        }
        else
        {
            var best = errors.MaxBy(e => e.Confidence);
            correction = best.Error;
            message = string.Create(CultureInfo.InvariantCulture,
                $"measurements disagree by {(errors[^1].Error - errors[0].Error) * 1000:0} ms; used the clearest one");
        }

        var confidence = errors.Max(e => e.Confidence);
        var level = errors.Select(e => e.LevelDb).Where(l => l is not null).DefaultIfEmpty(null).Max();
        sink.ApplyCalibration(correction, confidence, applyNs);
        var latency = sink.CurrentLatencyAt(applyNs);
        if (message is not null)
        {
            log.Warn("calib", $"{sink.Name}: {message}");
        }

        return new SinkCalibrationResult(sink.Id, sink.Name, true, correction, latency, confidence, level, heard.Count, plans.Count, message);
    }

    private static double? LevelDb(float[] samples, int start, int length)
    {
        if (start < 0 || start + length > samples.Length)
        {
            return null;
        }

        double sum = 0;
        for (var i = start; i < start + length; i++)
        {
            sum += samples[i] * (double)samples[i];
        }

        var rms = Math.Sqrt(sum / length);
        return rms > 0 ? 20 * Math.Log10(rms) : null;
    }

    private static float[] ResampleMono(float[] input, int fromRate, int toRate)
    {
        var resampler = new StreamingResampler(fromRate, toRate);
        var stereo = new float[input.Length * 2];
        for (var i = 0; i < input.Length; i++)
        {
            stereo[i * 2] = stereo[i * 2 + 1] = input[i];
        }

        resampler.Push(stereo);
        resampler.Push(new float[64 * 2]);
        var output = new float[(int)Math.Ceiling(input.Length * (double)toRate / fromRate) * 2 + 64];
        var n = resampler.Pull(output);
        var mono = new float[Math.Min(n, (int)Math.Round(input.Length * (double)toRate / fromRate))];
        for (var i = 0; i < mono.Length; i++)
        {
            mono[i] = output[i * 2];
        }

        return mono;
    }

    private static CalibrationReport Failed(IReadOnlyList<SinkBase> sinks, string message) =>
        new(DateTime.UtcNow, false,
            sinks.Select(s => new SinkCalibrationResult(s.Id, s.Name, false, null, null, 0, null, 0, 0, message)).ToList(),
            message);

    private sealed class ProbePlan(SinkBase sink, int rep, long frame, float[] signal, long expectedNs, long earliestNs, long latestNs)
    {
        public SinkBase Sink { get; } = sink;

        public int Repetition { get; } = rep;

        public long Frame { get; } = frame;

        public float[] Signal { get; } = signal;

        public long ExpectedNs { get; } = expectedNs;

        public long EarliestNs { get; } = earliestNs;

        public long LatestNs { get; } = latestNs;

        public int LastAttemptMaxStart { get; set; } = int.MinValue / 2;

        public ProbeResult? Result { get; set; }
    }

    private sealed record ProbeResult(bool Heard, long ArrivalNs, double Confidence, double? LevelDb);
}
