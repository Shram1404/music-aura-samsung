using MusicAura.Core.Timing;

namespace MusicAura.Droid.Platform;

/// <summary>
/// Converts Java System.nanoTime() / AudioTimestamp nanoTime values to the engine clock.
/// Both are CLOCK_MONOTONIC on Android, so the offset is normally zero, but it is measured
/// once rather than assumed.
/// </summary>
internal static class JavaClock
{
    private static readonly Lazy<long> OffsetNs = new(Measure);

    public static long ToEngineNs(long javaNanoTime) => javaNanoTime + OffsetNs.Value;

    public static long FromEngineNs(long engineNs) => engineNs - OffsetNs.Value;

    private static long Measure()
    {
        var samples = new List<long>();
        for (var i = 0; i < 15; i++)
        {
            var a = MonotonicClock.Instance.NowNs;
            var j = Java.Lang.JavaSystem.NanoTime();
            var b = MonotonicClock.Instance.NowNs;
            samples.Add((a + b) / 2 - j);
        }

        samples.Sort();
        var offset = samples[samples.Count / 2];
        // Sub-millisecond differences are measurement noise between the two calls.
        return Math.Abs(offset) < 1_000_000 ? 0 : offset;
    }
}
