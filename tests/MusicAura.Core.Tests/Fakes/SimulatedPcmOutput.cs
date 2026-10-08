using MusicAura.Core.Engine.Sinks;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Tests.Fakes;

/// <summary>
/// Real-time model of a phone audio output (AudioTrack-like): a DAC that consumes the written
/// frames at its own (slightly skewed) clock, honest presentation timestamps, and an extra
/// acoustic latency the timestamps do not know about (as with Bluetooth).
/// </summary>
internal sealed class SimulatedPcmOutput : IPcmOutput
{
    private readonly object _lock = new();
    private readonly IClock _clock;
    private readonly double _rateFactor;
    private readonly long _extraLatencyNs;
    private readonly List<float> _history = [];
    private readonly List<(long T, double Pos)> _dacLog = [];
    private double _dacPos;
    private long _lastUpdateNs;
    private bool _started;

    public SimulatedPcmOutput(IClock clock, int sampleRate, double bufferSeconds, double skewPpm, double extraLatencySeconds)
    {
        _clock = clock;
        SampleRate = sampleRate;
        BufferFrames = (int)(bufferSeconds * sampleRate);
        _rateFactor = 1 + skewPpm * 1e-6;
        _extraLatencyNs = Ns.FromSeconds(extraLatencySeconds);
    }

    public int SampleRate { get; }

    public int BufferFrames { get; }

    public long FramesWritten { get; private set; }

    public string? RoutingProblem => null;

    public int Underruns { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            _started = true;
            _lastUpdateNs = _clock.NowNs;
        }
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        lock (_lock)
        {
            for (var i = 0; i < frames; i++)
            {
                _history.Add((interleaved[i * 2] + interleaved[i * 2 + 1]) * 0.5f);
            }

            FramesWritten += frames;
        }

        while (true)
        {
            lock (_lock)
            {
                Advance();
                if (FramesWritten - _dacPos <= BufferFrames)
                {
                    return;
                }
            }

            Thread.Sleep(1);
        }
    }

    public bool TryGetTimestamp(out long framePosition, out long timeNs)
    {
        lock (_lock)
        {
            Advance();
            framePosition = (long)_dacPos;
            timeNs = _lastUpdateNs - (long)((_dacPos - framePosition) / (SampleRate * _rateFactor) * Ns.PerSecond);
            return _started && framePosition > 0;
        }
    }

    /// <summary>What a listener hears from this output at <paramref name="tNs"/>.</summary>
    public float AudibleAt(long tNs)
    {
        var dacTime = tNs - _extraLatencyNs;
        lock (_lock)
        {
            var pos = DacPositionAt(dacTime);
            if (pos is null)
            {
                return 0;
            }

            var i = (long)Math.Floor(pos.Value);
            if (i < 0 || i + 1 >= _history.Count)
            {
                return 0;
            }

            var f = (float)(pos.Value - i);
            return _history[(int)i] * (1 - f) + _history[(int)i + 1] * f;
        }
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }

    private void Advance()
    {
        var now = _clock.NowNs;
        if (!_started)
        {
            return;
        }

        var dt = (now - _lastUpdateNs) / (double)Ns.PerSecond;
        var next = _dacPos + dt * SampleRate * _rateFactor;
        if (next > FramesWritten)
        {
            if (FramesWritten > 0)
            {
                Underruns++;
            }

            next = FramesWritten;
        }

        _dacPos = next;
        _lastUpdateNs = now;
        _dacLog.Add((now, _dacPos));
        if (_dacLog.Count > 200_000)
        {
            _dacLog.RemoveRange(0, 100_000);
        }
    }

    private double? DacPositionAt(long t)
    {
        if (_dacLog.Count < 2 || t < _dacLog[0].T)
        {
            return null;
        }

        var lo = 0;
        var hi = _dacLog.Count - 1;
        if (t >= _dacLog[hi].T)
        {
            return null; // not played yet
        }

        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (_dacLog[mid].T <= t)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var a = _dacLog[lo];
        var b = _dacLog[hi];
        var w = b.T == a.T ? 0 : (t - a.T) / (double)(b.T - a.T);
        return a.Pos + (b.Pos - a.Pos) * w;
    }
}
