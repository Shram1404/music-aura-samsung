namespace MusicAura.Core.Engine;

/// <summary>
/// Per-output mute ranges on the timeline, with short fades. Pause, stop, skip and seek are
/// implemented by gating the frames an output has not played yet, which makes them take
/// effect immediately even though the timeline itself runs seconds ahead of what is audible.
/// </summary>
internal sealed class GateList
{
    private readonly object _lock = new();
    private readonly List<Gate> _gates = [];

    public void Add(long start, long end, int fadeFrames)
    {
        if (end <= start)
        {
            return;
        }

        lock (_lock)
        {
            _gates.Add(new Gate(start, end, Math.Max(1, fadeFrames)));
        }
    }

    public void AddOpen(long start, int fadeFrames) => Add(start, long.MaxValue, fadeFrames);

    /// <summary>Ends every open gate at <paramref name="end"/> (content from there on plays again, faded in).</summary>
    public void CloseOpen(long end)
    {
        lock (_lock)
        {
            for (var i = 0; i < _gates.Count; i++)
            {
                var g = _gates[i];
                if (g.End == long.MaxValue)
                {
                    _gates[i] = g with { End = Math.Max(g.Start + 1, end) };
                }
            }
        }
    }

    public bool HasOpenGate
    {
        get
        {
            lock (_lock)
            {
                return _gates.Any(g => g.End == long.MaxValue);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _gates.Clear();
        }
    }

    public void Prune(long beforeFrame)
    {
        lock (_lock)
        {
            _gates.RemoveAll(g => g.End != long.MaxValue && g.End + g.Fade < beforeFrame);
        }
    }

    /// <summary>Applies all gates to interleaved stereo frames starting at timeline frame <paramref name="firstFrame"/>.</summary>
    public void Apply(long firstFrame, Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        var lastFrame = firstFrame + frames;
        lock (_lock)
        {
            foreach (var g in _gates)
            {
                var affectedTo = g.End == long.MaxValue ? long.MaxValue : g.End + g.Fade;
                if (g.Start >= lastFrame || affectedTo <= firstFrame)
                {
                    continue;
                }

                var from = (int)Math.Max(0, g.Start - firstFrame);
                var to = (int)Math.Min(frames, affectedTo - firstFrame);
                for (var i = from; i < to; i++)
                {
                    var gain = g.GainAt(firstFrame + i);
                    interleaved[i * 2] *= gain;
                    interleaved[i * 2 + 1] *= gain;
                }
            }
        }
    }

    private readonly record struct Gate(long Start, long End, int Fade)
    {
        public float GainAt(long frame)
        {
            if (frame < Start)
            {
                return 1f;
            }

            if (frame >= End)
            {
                return Math.Min(1f, (frame - End + 1) / (float)Fade);
            }

            return Math.Max(0f, 1f - (frame - Start + 1) / (float)Fade);
        }
    }
}

/// <summary>Per-output additive signals on the timeline (calibration probes).</summary>
internal sealed class OverlayList
{
    private readonly object _lock = new();
    private readonly List<Overlay> _overlays = [];

    public void Add(long startFrame, float[] mono)
    {
        lock (_lock)
        {
            _overlays.Add(new Overlay(startFrame, mono));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _overlays.Clear();
        }
    }

    public void Prune(long beforeFrame)
    {
        lock (_lock)
        {
            _overlays.RemoveAll(o => o.Start + o.Samples.Length < beforeFrame);
        }
    }

    public void Apply(long firstFrame, Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        lock (_lock)
        {
            foreach (var o in _overlays)
            {
                var from = Math.Max(firstFrame, o.Start);
                var to = Math.Min(firstFrame + frames, o.Start + o.Samples.Length);
                for (var f = from; f < to; f++)
                {
                    var s = o.Samples[f - o.Start];
                    var i = (int)(f - firstFrame) * 2;
                    interleaved[i] += s;
                    interleaved[i + 1] += s;
                }
            }
        }
    }

    private sealed record Overlay(long Start, float[] Samples);
}
