namespace MusicAura.Core.Audio;

/// <summary>
/// Ring buffer of interleaved stereo float frames addressed by absolute timeline frame index.
/// One producer appends at <see cref="WriteHead"/>; any number of readers fetch arbitrary
/// ranges within the retained history. Unavailable frames read as silence.
/// </summary>
public sealed class TimelineBuffer
{
    public const int Channels = 2;

    private readonly object _lock = new();
    private readonly float[] _data;
    private long _writeHead;
    private long _validFrom;

    public TimelineBuffer(int capacityFrames, long startFrame = 0)
    {
        if (capacityFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        }

        CapacityFrames = capacityFrames;
        _data = new float[capacityFrames * Channels];
        _writeHead = startFrame;
        _validFrom = startFrame;
    }

    public int CapacityFrames { get; }

    public long WriteHead
    {
        get
        {
            lock (_lock)
            {
                return _writeHead;
            }
        }
    }

    /// <summary>Earliest frame still held in the buffer.</summary>
    public long OldestFrame
    {
        get
        {
            lock (_lock)
            {
                return Math.Max(_validFrom, _writeHead - CapacityFrames);
            }
        }
    }

    /// <summary>Discards everything and restarts the buffer at <paramref name="startFrame"/>.</summary>
    public void Reset(long startFrame)
    {
        lock (_lock)
        {
            _writeHead = startFrame;
            _validFrom = startFrame;
        }
    }

    public void Append(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / Channels;
        lock (_lock)
        {
            var offset = 0;
            while (offset < frames)
            {
                var slot = (int)(_writeHead % CapacityFrames);
                var n = Math.Min(frames - offset, CapacityFrames - slot);
                interleaved.Slice(offset * Channels, n * Channels).CopyTo(_data.AsSpan(slot * Channels));
                offset += n;
                _writeHead += n;
            }
        }
    }

    public void AppendSilence(int frames)
    {
        lock (_lock)
        {
            var remaining = frames;
            while (remaining > 0)
            {
                var slot = (int)(_writeHead % CapacityFrames);
                var n = Math.Min(remaining, CapacityFrames - slot);
                Array.Clear(_data, slot * Channels, n * Channels);
                remaining -= n;
                _writeHead += n;
            }
        }
    }

    /// <summary>
    /// Moves the write head to <paramref name="frame"/>: frames at or after it are discarded
    /// (so they can be rewritten), or silence is appended if the head is behind it.
    /// </summary>
    public void MoveWriteHead(long frame)
    {
        lock (_lock)
        {
            if (frame < _writeHead)
            {
                _writeHead = Math.Max(frame, Math.Max(_validFrom, _writeHead - CapacityFrames));
                return;
            }
        }

        var gap = frame - WriteHead;
        while (gap > 0)
        {
            var n = (int)Math.Min(gap, CapacityFrames);
            AppendSilence(n);
            gap -= n;
        }
    }

    /// <summary>
    /// Copies frames [<paramref name="startFrame"/>, startFrame + n) into <paramref name="destination"/>.
    /// Returns how many of them were actually available.
    /// </summary>
    public int Read(long startFrame, Span<float> destination)
    {
        var frames = destination.Length / Channels;
        lock (_lock)
        {
            var oldest = Math.Max(_validFrom, _writeHead - CapacityFrames);
            var from = Math.Max(startFrame, oldest);
            var to = Math.Min(startFrame + frames, _writeHead);
            if (to <= from)
            {
                destination.Clear();
                return 0;
            }

            var lead = (int)(from - startFrame);
            if (lead > 0)
            {
                destination[..(lead * Channels)].Clear();
            }

            var tailStart = (int)(to - startFrame);
            if (tailStart < frames)
            {
                destination[(tailStart * Channels)..].Clear();
            }

            var pos = from;
            while (pos < to)
            {
                var slot = (int)(pos % CapacityFrames);
                var n = (int)Math.Min(to - pos, CapacityFrames - slot);
                _data.AsSpan(slot * Channels, n * Channels)
                    .CopyTo(destination.Slice((int)(pos - startFrame) * Channels, n * Channels));
                pos += n;
            }

            return (int)(to - from);
        }
    }
}
