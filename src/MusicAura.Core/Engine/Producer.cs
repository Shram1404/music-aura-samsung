using MusicAura.Core.Audio;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Engine;

/// <summary>
/// Decodes the playlist into the shared timeline, staying a little ahead of real time.
/// Tracks follow each other gaplessly; when nothing is playing the timeline is filled with
/// silence so every output (and the TV's HTTP stream) keeps running and stays in sync.
/// </summary>
internal sealed class Producer : IDisposable
{
    private const int ChunkFrames = 2048;

    private readonly object _lock = new();
    private readonly TimelineBuffer _timeline;
    private readonly TimelineClock _clock;
    private readonly IClock _sysClock;
    private readonly SegmentMap _segments;
    private readonly Func<TrackInfo, IAudioSource> _open;
    private readonly Playlist _playlist;
    private readonly AuraLog _log;
    private readonly EngineOptions _options;
    private readonly float[] _decode = new float[ChunkFrames * 2];
    private readonly float[] _out = new float[ChunkFrames * 2];
    private Thread? _thread;
    private volatile bool _running;

    private IAudioSource? _source;
    private StreamingResampler? _resampler;
    private TrackInfo? _track;
    private bool _flushed;

    public Producer(
        TimelineBuffer timeline,
        TimelineClock clock,
        IClock sysClock,
        SegmentMap segments,
        Func<TrackInfo, IAudioSource> open,
        Playlist playlist,
        EngineOptions options,
        AuraLog log)
    {
        _timeline = timeline;
        _clock = clock;
        _sysClock = sysClock;
        _segments = segments;
        _open = open;
        _playlist = playlist;
        _options = options;
        _log = log;
    }

    /// <summary>Raised (on the producer thread) when the playlist runs out; argument is the frame where audio ends.</summary>
    public event Action<long>? ContentEnded;

    public TrackInfo? CurrentDecodingTrack
    {
        get
        {
            lock (_lock)
            {
                return _track;
            }
        }
    }

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        lock (_lock)
        {
            FillTo(TargetFrame());
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "aura-producer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Replaces whatever the timeline holds from <paramref name="atFrame"/> on with
    /// <paramref name="source"/> (already opened and positioned at <paramref name="trackStartFrame"/>), or silence.
    /// </summary>
    public void SetContent(long atFrame, TrackInfo? track, IAudioSource? source, long trackStartFrame)
    {
        lock (_lock)
        {
            _timeline.MoveWriteHead(atFrame);
            ReplaceSource(track, source);
            _segments.Start(_timeline.WriteHead, track, trackStartFrame);
            FillTo(TargetFrame());
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_thread is { } t && t != Thread.CurrentThread)
        {
            t.Join(1000);
        }

        lock (_lock)
        {
            ReplaceSource(null, null);
        }
    }

    private long TargetFrame() =>
        (long)Math.Ceiling(_clock.FrameAt(_sysClock.NowNs) + _options.ProducerLeadSeconds * _clock.SampleRate);

    private void Loop()
    {
        _options.ConfigureAudioThread?.Invoke("producer");
        while (_running)
        {
            try
            {
                lock (_lock)
                {
                    FillTo(TargetFrame());
                }
            }
            catch (Exception ex)
            {
                _log.Error("producer", "fill failed", ex);
            }

            Thread.Sleep(15);
        }
    }

    private void ReplaceSource(TrackInfo? track, IAudioSource? source)
    {
        if (!ReferenceEquals(_source, source))
        {
            _source?.Dispose();
        }

        _source = source;
        _track = source is null ? null : track;
        _flushed = false;
        _resampler = source is null ? null : new StreamingResampler(source.SampleRate, _clock.SampleRate);
    }

    private void FillTo(long target)
    {
        while (_timeline.WriteHead < target)
        {
            var want = (int)Math.Min(target - _timeline.WriteHead, ChunkFrames);
            if (_source is null)
            {
                _timeline.AppendSilence(want);
                continue;
            }

            var got = ReadConverted(want);
            if (got > 0)
            {
                _timeline.Append(_out.AsSpan(0, got * 2));
            }

            if (got < want)
            {
                OnTrackEnded();
            }
        }
    }

    private int ReadConverted(int frames)
    {
        var produced = 0;
        var resampler = _resampler!;
        while (produced < frames)
        {
            produced += resampler.Pull(_out.AsSpan(produced * 2, (frames - produced) * 2));
            if (produced >= frames)
            {
                break;
            }

            int read;
            try
            {
                read = _source!.Read(_decode);
            }
            catch (Exception ex)
            {
                _log.Error("producer", $"decoding '{_track?.Title}' failed", ex);
                read = 0;
                _flushed = true;
            }

            if (read > 0)
            {
                resampler.Push(_decode.AsSpan(0, read * 2));
            }
            else if (!_flushed && !resampler.IsPassThrough)
            {
                // Push a few zeros so the interpolator releases the last input frames.
                _flushed = true;
                Array.Clear(_decode);
                resampler.Push(_decode.AsSpan(0, 32 * 2));
            }
            else
            {
                break;
            }
        }

        return produced;
    }

    private void OnTrackEnded()
    {
        var endFrame = _timeline.WriteHead;
        _segments.EndCurrent(endFrame);
        var current = _track;
        ReplaceSource(null, null);

        var candidate = _playlist.After(current);
        for (var attempts = 0; candidate is not null && attempts < Math.Max(1, _playlist.Count); attempts++)
        {
            try
            {
                var source = _open(candidate);
                ReplaceSource(candidate, source);
                _segments.Start(endFrame, candidate, 0);
                _log.Info("producer", $"next track: {candidate.DisplayName}");
                return;
            }
            catch (Exception ex)
            {
                _log.Error("producer", $"cannot open '{candidate.Title}'", ex);
                candidate = _playlist.After(candidate);
            }
        }

        _segments.Start(endFrame, null, 0);
        ContentEnded?.Invoke(endFrame);
    }
}
