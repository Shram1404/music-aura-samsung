using System.Diagnostics;

namespace MusicAura.Core.Timing;

/// <summary>Monotonic time source in nanoseconds.</summary>
public interface IClock
{
    long NowNs { get; }
}

/// <summary>
/// Monotonic clock backed by <see cref="Stopwatch"/>. On Linux and Android this is
/// CLOCK_MONOTONIC, the same time base as Java's System.nanoTime() and the
/// AudioTimestamp.TIMEBASE_MONOTONIC values reported by AudioTrack/AudioRecord.
/// </summary>
public sealed class MonotonicClock : IClock
{
    public static readonly MonotonicClock Instance = new();

    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    public long NowNs
    {
        get
        {
            var ticks = Stopwatch.GetTimestamp();
            return Stopwatch.Frequency == 1_000_000_000 ? ticks : (long)(ticks * NsPerTick);
        }
    }
}

public static class Ns
{
    public const long PerSecond = 1_000_000_000;
    public const long PerMillisecond = 1_000_000;

    public static long FromSeconds(double seconds) => (long)Math.Round(seconds * PerSecond);

    public static double ToSeconds(long ns) => ns / (double)PerSecond;

    public static double ToMilliseconds(long ns) => ns / (double)PerMillisecond;
}

/// <summary>
/// The shared session timeline. Frame <c>F</c> of the timeline is "due" at
/// <c>OriginNs + F / SampleRate</c>. Every output plays frame F at its due time plus the
/// session's target latency, which is what keeps the outputs in sync.
/// </summary>
public sealed class TimelineClock
{
    public TimelineClock(long originNs, int sampleRate)
    {
        OriginNs = originNs;
        SampleRate = sampleRate;
    }

    public long OriginNs { get; }

    public int SampleRate { get; }

    public double FrameAt(long ns) => (ns - OriginNs) * (double)SampleRate / Ns.PerSecond;

    public long TimeOf(double frame) => OriginNs + (long)Math.Round(frame * Ns.PerSecond / SampleRate);

    public double FramesToSeconds(double frames) => frames / SampleRate;

    public double SecondsToFrames(double seconds) => seconds * SampleRate;
}
