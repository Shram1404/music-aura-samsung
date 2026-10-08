using MusicAura.Core.Audio;

namespace MusicAura.Core.Engine;

/// <summary>What the timeline contains from a given frame on: a track (at some position) or silence.</summary>
internal readonly record struct ContentSegment(long StartFrame, TrackInfo? Track, long TrackStartFrame, long? EndFrame);

/// <summary>
/// Maps timeline frames back to "track + position". Needed because the timeline runs ahead of
/// what is audible: pause, seek and the position display all work in terms of what the listener
/// is hearing right now.
/// </summary>
internal sealed class SegmentMap
{
    private readonly object _lock = new();
    private readonly List<ContentSegment> _segments = [];

    /// <summary>Starts a new segment at <paramref name="startFrame"/>, discarding any later ones.</summary>
    public void Start(long startFrame, TrackInfo? track, long trackStartFrame)
    {
        lock (_lock)
        {
            _segments.RemoveAll(s => s.StartFrame >= startFrame);
            if (_segments.Count > 0)
            {
                var last = _segments[^1];
                if (last.EndFrame is null || last.EndFrame > startFrame)
                {
                    _segments[^1] = last with { EndFrame = startFrame };
                }
            }

            _segments.Add(new ContentSegment(startFrame, track, trackStartFrame, null));
        }
    }

    /// <summary>Marks where the current track's audio actually ends (the next segment may start later).</summary>
    public void EndCurrent(long endFrame)
    {
        lock (_lock)
        {
            if (_segments.Count > 0 && _segments[^1].EndFrame is null)
            {
                _segments[^1] = _segments[^1] with { EndFrame = endFrame };
            }
        }
    }

    public ContentSegment? At(long frame)
    {
        lock (_lock)
        {
            for (var i = _segments.Count - 1; i >= 0; i--)
            {
                var s = _segments[i];
                if (s.StartFrame <= frame)
                {
                    if (s.EndFrame is { } end && frame >= end)
                    {
                        return new ContentSegment(end, null, 0, null);
                    }

                    return s;
                }
            }

            return null;
        }
    }

    /// <summary>Track and position (in session frames) audible at <paramref name="frame"/>.</summary>
    public (TrackInfo? Track, long TrackFrame) Locate(long frame)
    {
        var s = At(frame);
        if (s is not { Track: not null } seg)
        {
            return (null, 0);
        }

        return (seg.Track, seg.TrackStartFrame + (frame - seg.StartFrame));
    }

    public void Prune(long beforeFrame)
    {
        lock (_lock)
        {
            // Keep the segment covering beforeFrame.
            var keepFrom = _segments.FindLastIndex(s => s.StartFrame <= beforeFrame);
            if (keepFrom > 0)
            {
                _segments.RemoveRange(0, keepFrom);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _segments.Clear();
        }
    }
}
