using System.Globalization;
using System.Net;
using MusicAura.Core.Audio;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Engine.Sinks;
using MusicAura.Core.Http;
using MusicAura.Core.Sync;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Engine;

/// <summary>
/// Plays one playlist on several outputs at once, in sync.
/// <para>
/// All outputs read the same timeline. Timeline frame F is due at <c>origin + F/rate</c> and
/// must be heard on every output at <c>due(F) + L</c>, where the target latency L is the largest
/// latency any output needs (normally the TV's buffering). Faster outputs simply play later:
/// phone outputs steer themselves with hardware timestamps, network renderers get each frame
/// sent with the appropriate delay. Latencies are measured acoustically (microphone) or
/// estimated, and adjusted by the user's per-device offsets.
/// </para>
/// </summary>
public sealed class MultiroomEngine : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _command = new(1, 1);
    private readonly List<SinkBase> _sinks = [];
    private readonly Dictionary<string, TimeSpan> _durations = [];
    private readonly IAudioSourceFactory _sourceFactory;
    private readonly IMicrophone? _microphone;
    private readonly IClock _clock;
    private readonly Func<IPAddress, IPAddress?>? _localAddressResolver;
    private readonly SincKernel _kernel = new(16, 1.0);

    private Session? _session;
    private EngineState _state = EngineState.Idle;
    private double _targetLatency = 0.3;
    private bool _freezeTarget;
    private bool _calibrating;
    private Task<CalibrationReport>? _calibrationTask;
    private (TrackInfo Track, long Frame)? _resume;
    private TrackInfo? _startingTrack;
    private long? _contentEndFrame;
    private string? _message;
    private bool _playAfterCalibration;

    public MultiroomEngine(
        EngineOptions options,
        IAudioSourceFactory sourceFactory,
        IMicrophone? microphone = null,
        AuraLog? log = null,
        IClock? clock = null,
        Func<IPAddress, IPAddress?>? localAddressResolver = null)
    {
        Options = options;
        _sourceFactory = sourceFactory;
        _microphone = microphone;
        Log = log ?? new AuraLog();
        _clock = clock ?? MonotonicClock.Instance;
        _localAddressResolver = localAddressResolver;
        HttpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            AutomaticDecompression = DecompressionMethods.None,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public EngineOptions Options { get; }

    public AuraLog Log { get; }

    public Playlist Playlist { get; } = new();

    /// <summary>LAN HTTP client (no proxy) shared with DLNA discovery.</summary>
    public HttpClient HttpClient { get; }

    public bool HasMicrophone => _microphone is not null;

    public CalibrationReport? LastCalibration { get; private set; }

    public event Action<CalibrationReport>? CalibrationCompleted;

    public EngineState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public bool SessionActive
    {
        get
        {
            lock (_sync)
            {
                return _session is not null;
            }
        }
    }

    public double TargetLatencySeconds => Volatile.Read(ref _targetLatency);

    public IReadOnlyList<SinkBase> Sinks
    {
        get
        {
            lock (_sync)
            {
                return _sinks.ToArray();
            }
        }
    }

    public SinkBase? FindSink(string id)
    {
        lock (_sync)
        {
            return _sinks.FirstOrDefault(s => s.Id == id);
        }
    }

    // ------------------------------------------------------------------ outputs

    public void AddSink(SinkBase sink)
    {
        Session? session;
        lock (_sync)
        {
            if (_sinks.Any(s => s.Id == sink.Id))
            {
                return;
            }

            _sinks.Add(sink);
            session = _session;
        }

        if (session is not null && sink.Enabled)
        {
            _ = AttachDuringSessionAsync(sink);
        }
    }

    public async Task RemoveSinkAsync(string id)
    {
        SinkBase? sink;
        lock (_sync)
        {
            sink = _sinks.FirstOrDefault(s => s.Id == id);
            if (sink is null)
            {
                return;
            }

            _sinks.Remove(sink);
        }

        if (sink.IsRunning)
        {
            await sink.DetachAsync().ConfigureAwait(false);
        }
    }

    public async Task SetSinkEnabledAsync(string id, bool enabled)
    {
        var sink = FindSink(id);
        if (sink is null || sink.Enabled == enabled)
        {
            return;
        }

        sink.Enabled = enabled;
        if (!SessionActive)
        {
            return;
        }

        if (enabled)
        {
            await AttachDuringSessionAsync(sink).ConfigureAwait(false);
        }
        else if (sink.IsRunning)
        {
            await sink.DetachAsync().ConfigureAwait(false);
            Log.Info("engine", $"{sink.Name} left the session");
        }
    }

    public void SetUserOffset(string id, double seconds)
    {
        if (FindSink(id) is { } sink)
        {
            sink.UserOffsetSeconds = Math.Clamp(seconds, -2, 2);
        }
    }

    public void SetVolume(string id, double volume)
    {
        if (FindSink(id) is { } sink)
        {
            sink.Volume = volume;
        }
    }

    // ------------------------------------------------------------------ session

    /// <summary>
    /// Connects all enabled outputs (the TV starts pulling the stream) and, if enabled and a
    /// microphone is available, measures their latencies.
    /// </summary>
    public async Task StartSessionAsync(CancellationToken ct = default)
    {
        await _command.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (SessionActive)
            {
                return;
            }

            await StartSessionCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _command.Release();
        }

        await AutoCalibrateIfUsefulAsync(ct).ConfigureAwait(false);
    }

    public async Task StopSessionAsync()
    {
        await _command.WaitAsync().ConfigureAwait(false);
        Session? session;
        try
        {
            lock (_sync)
            {
                session = _session;
                _session = null;
                _state = EngineState.Idle;
                _resume = null;
                _startingTrack = null;
                _contentEndFrame = null;
            }

            if (session is null)
            {
                return;
            }

            session.Cancel.Cancel();
            await Task.WhenAll(Sinks.Where(s => s.IsRunning).Select(s => s.DetachAsync())).ConfigureAwait(false);
            try
            {
                await session.Supervisor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            session.Producer.Dispose();
            await session.Http.DisposeAsync().ConfigureAwait(false);
            session.Cancel.Dispose();
            Log.Info("engine", "session stopped");
        }
        finally
        {
            _command.Release();
        }
    }

    private async Task StartSessionCoreAsync(CancellationToken ct)
    {
        SetState(EngineState.Starting, "connecting outputs");
        var rate = Options.SampleRate;
        var timeline = new TimelineClock(_clock.NowNs, rate);
        var buffer = new TimelineBuffer(Options.TimelineCapacityFrames);
        var segments = new SegmentMap();
        var http = new MiniHttpServer(IPAddress.Any, 0, Log);
        http.Start();

        var ctx = new EngineContext
        {
            Clock = _clock,
            Timeline = timeline,
            Buffer = buffer,
            Kernel = _kernel,
            Options = Options,
            Log = Log,
            TargetLatency = () => Volatile.Read(ref _targetLatency),
            HttpServer = http,
            HttpClient = HttpClient,
            LocalAddressResolver = _localAddressResolver,
        };

        var producer = new Producer(buffer, timeline, _clock, segments, OpenTrack, Playlist, Options, Log);
        producer.ContentEnded += frame =>
        {
            lock (_sync)
            {
                _contentEndFrame = frame;
            }
        };

        var session = new Session(ctx, producer, segments, http, new CancellationTokenSource());
        var participants = Sinks.Where(s => s.Enabled).ToList();
        lock (_sync)
        {
            _session = session;
        }

        producer.Start();
        foreach (var sink in participants)
        {
            // Outputs not measured yet are calibrated as soon as they are up (now or later).
            sink.LatencyInvalidated = !sink.IsCalibrated;
            sink.Attach(ctx);
        }

        Volatile.Write(ref _targetLatency, ComputeTargetLatency(_clock.NowNs));
        session.Supervisor = Task.Run(() => SuperviseAsync(session));
        Log.Info("engine", $"session started with {participants.Count} outputs; HTTP stream server on port {http.Port}");

        await WaitForOutputsAsync(participants, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        Volatile.Write(ref _targetLatency, ComputeTargetLatency(_clock.NowNs));
        SetState(EngineState.Ready, null);
    }

    private async Task AttachDuringSessionAsync(SinkBase sink)
    {
        Session? session;
        lock (_sync)
        {
            session = _session;
        }

        if (session is null || sink.IsRunning)
        {
            return;
        }

        sink.LatencyInvalidated = !sink.IsCalibrated;
        sink.Attach(session.Context);
        Log.Info("engine", $"{sink.Name} joined the session");
        await WaitForOutputsAsync([sink], TimeSpan.FromSeconds(25), CancellationToken.None).ConfigureAwait(false);
        // The supervisor notices the new latency requirement (and calibrates if needed).
    }

    private async Task WaitForOutputsAsync(IReadOnlyList<SinkBase> sinks, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _clock.NowNs + Ns.FromSeconds(timeout.TotalSeconds);
        while (_clock.NowNs < deadline)
        {
            var pending = sinks.Where(s => s.IsRunning && s.Status is SinkStatus.Connecting or SinkStatus.Buffering).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            SetMessage("waiting for " + string.Join(", ", pending.Select(p => p.Name)));
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        foreach (var s in sinks.Where(s => s.Status is SinkStatus.Connecting or SinkStatus.Buffering))
        {
            Log.Warn("engine", $"{s.Name} is not ready after {timeout.TotalSeconds:0} s ({s.StatusMessage})");
        }
    }

    // ------------------------------------------------------------------ transport

    /// <summary>Starts playing <paramref name="track"/> (or resumes / starts the playlist). Starts a session if needed.</summary>
    public async Task PlayAsync(TrackInfo? track = null, TimeSpan? position = null, CancellationToken ct = default)
    {
        if (!SessionActive)
        {
            await StartSessionAsync(ct).ConfigureAwait(false);
        }

        lock (_sync)
        {
            if (_calibrating)
            {
                _playAfterCalibration = true;
                if (track is not null)
                {
                    _resume = (track, 0);
                }

                return;
            }
        }

        await _command.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = State;
            if (track is null && position is null)
            {
                if (state == EngineState.Playing)
                {
                    return;
                }

                if (state == EngineState.Paused && _resume is { } r)
                {
                    await StartContentAsync(r.Track, r.Frame).ConfigureAwait(false);
                    return;
                }

                track = _resume?.Track ?? Playlist.At(0);
                if (track is null)
                {
                    SetMessage("playlist is empty");
                    return;
                }
            }

            track ??= CurrentTrack();
            if (track is null)
            {
                return;
            }

            var frame = (long)((position ?? TimeSpan.Zero).TotalSeconds * Options.SampleRate);
            await StartContentAsync(track, frame).ConfigureAwait(false);
        }
        finally
        {
            _command.Release();
        }
    }

    public async Task PauseAsync()
    {
        await _command.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != EngineState.Playing || _session is not { } s)
            {
                return;
            }

            PauseCore(s);
        }
        finally
        {
            _command.Release();
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (State == EngineState.Playing)
        {
            await PauseAsync().ConfigureAwait(false);
        }
        else
        {
            await PlayAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Stops playback; the session (TV connection) stays up.</summary>
    public async Task StopAsync()
    {
        await _command.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is not { } s || State is not (EngineState.Playing or EngineState.Paused))
            {
                return;
            }

            if (State == EngineState.Playing)
            {
                var now = _clock.NowNs;
                ApplyCut(s, now, open: true);
                s.Producer.SetContent(CommandFrame(s, now), null, null, 0);
            }

            lock (_sync)
            {
                _resume = null;
                _startingTrack = null;
            }

            SetState(EngineState.Ready, null);
        }
        finally
        {
            _command.Release();
        }
    }

    public async Task NextAsync()
    {
        var next = Playlist.After(CurrentTrack());
        if (next is null)
        {
            await StopAsync().ConfigureAwait(false);
            return;
        }

        await PlayAsync(next, TimeSpan.Zero).ConfigureAwait(false);
    }

    public async Task PreviousAsync()
    {
        var snapshot = GetSnapshot();
        if (snapshot.Track is { } current && snapshot.Position > TimeSpan.FromSeconds(3))
        {
            await PlayAsync(current, TimeSpan.Zero).ConfigureAwait(false);
            return;
        }

        var previous = Playlist.Before(snapshot.Track);
        if (previous is not null)
        {
            await PlayAsync(previous, TimeSpan.Zero).ConfigureAwait(false);
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var track = CurrentTrack();
        if (track is null)
        {
            return;
        }

        if (State == EngineState.Paused)
        {
            lock (_sync)
            {
                _resume = (track, (long)(position.TotalSeconds * Options.SampleRate));
            }

            return;
        }

        await PlayAsync(track, position).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ calibration

    /// <summary>Measures every running output with the microphone and re-aligns them.</summary>
    public async Task<CalibrationReport> CalibrateAsync(CancellationToken ct = default)
    {
        if (_microphone is null)
        {
            throw new InvalidOperationException("No microphone available for calibration.");
        }

        if (!SessionActive)
        {
            await StartSessionAsync(ct).ConfigureAwait(false);
            if (LastCalibration is { } fresh && DateTime.UtcNow - fresh.TimeUtc < TimeSpan.FromSeconds(5))
            {
                return fresh; // the session start already calibrated
            }
        }

        return await RunCalibrationAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Starts a calibration, or joins the one already running.</summary>
    private Task<CalibrationReport> RunCalibrationAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            if (_calibrationTask is { IsCompleted: false } running)
            {
                return running;
            }

            _calibrationTask = Task.Run(() => CalibrateCoreAsync(ct), CancellationToken.None);
            return _calibrationTask;
        }
    }

    private async Task AutoCalibrateIfUsefulAsync(CancellationToken ct)
    {
        if (!Options.AutoCalibrate || _microphone is null)
        {
            return;
        }

        var running = Sinks.Count(s => s.IsRunning && s.Status == SinkStatus.Active);
        if (running < 2)
        {
            return; // a single output needs no alignment
        }

        try
        {
            await RunCalibrationAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("engine", "automatic calibration failed", ex);
        }
    }

    private async Task<CalibrationReport> CalibrateCoreAsync(CancellationToken ct)
    {
        await _command.WaitAsync(ct).ConfigureAwait(false);
        Session? session;
        bool wasPlaying;
        EngineState previousState;
        try
        {
            session = _session ?? throw new InvalidOperationException("No session.");
            lock (_sync)
            {
                if (_calibrating)
                {
                    throw new InvalidOperationException("Calibration already running.");
                }

                _calibrating = true;
                previousState = _state;
            }

            wasPlaying = previousState == EngineState.Playing;
            if (wasPlaying)
            {
                PauseCore(session);
            }

            SetState(EngineState.Calibrating, "calibrating");
        }
        finally
        {
            _command.Release();
        }

        CalibrationReport report;
        try
        {
            var sinks = Sinks.Where(s => s.IsRunning).ToList();
            await WaitForOutputsAsync(sinks, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            sinks = sinks.Where(s => s.Status == SinkStatus.Active).ToList();
            if (sinks.Count == 0)
            {
                throw new InvalidOperationException("No active outputs to calibrate.");
            }

            var now = _clock.NowNs;
            var latency = ComputeTargetLatency(now);
            Volatile.Write(ref _targetLatency, latency);
            _freezeTarget = true;
            foreach (var s in sinks)
            {
                s.UnlockCursors();
            }

            // Bursts must not reach a TV while an instant pause still has it muted.
            var earliestDue = now;
            foreach (var dlna in sinks.OfType<DlnaSink>())
            {
                if (dlna.PendingUnmuteNs > 0)
                {
                    earliestDue = Math.Max(earliestDue, dlna.PendingUnmuteNs + Ns.FromSeconds(0.3 - latency - dlna.UserOffsetSeconds));
                }
            }

            report = await AcousticCalibration.RunAsync(session.Context, sinks, _microphone!, latency, earliestDue, ct).ConfigureAwait(false);
            LastCalibration = report;
        }
        finally
        {
            _freezeTarget = false;
            Volatile.Write(ref _targetLatency, ComputeTargetLatency(_clock.NowNs));
            lock (_sync)
            {
                _calibrating = false;
            }
        }

        try
        {
            CalibrationCompleted?.Invoke(report);
        }
        catch (Exception ex)
        {
            Log.Error("engine", "calibration listener failed", ex);
        }

        bool resume;
        lock (_sync)
        {
            resume = wasPlaying || _playAfterCalibration;
            _playAfterCalibration = false;
        }

        SetState(previousState == EngineState.Playing ? EngineState.Paused : previousState is EngineState.Calibrating or EngineState.Starting ? EngineState.Ready : previousState,
            report.Success ? null : report.Summary);
        if (resume)
        {
            await PlayAsync(ct: ct).ConfigureAwait(false);
        }

        return report;
    }

    // ------------------------------------------------------------------ snapshot

    public EngineSnapshot GetSnapshot()
    {
        var now = _clock.NowNs;
        Session? session;
        EngineState state;
        (TrackInfo Track, long Frame)? resume;
        TrackInfo? starting;
        string? message;
        lock (_sync)
        {
            session = _session;
            state = _state;
            resume = _resume;
            starting = _startingTrack;
            message = _message;
        }

        TrackInfo? track = null;
        long trackFrame = 0;
        var buffering = false;
        if (session is not null && state == EngineState.Playing)
        {
            (track, trackFrame) = session.Segments.Locate(AudibleFrame(session, now));
            if (track is null && starting is not null)
            {
                track = starting;
                buffering = true;
            }
        }
        else if (resume is { } r)
        {
            (track, trackFrame) = (r.Track, r.Frame);
        }

        TimeSpan? duration = null;
        if (track is not null)
        {
            lock (_durations)
            {
                duration = _durations.TryGetValue(track.Id, out var d) ? d : track.Duration;
            }
        }

        var sinks = Sinks.Select(s => new SinkSnapshot(
            s.Id,
            s.Name,
            s.Kind,
            s.Enabled,
            s.IsRunning,
            s.Status,
            s.StatusMessage,
            s.CurrentLatencyAt(now),
            s.IsCalibrated,
            s.CalibratedAtUtc,
            s.UserOffsetSeconds,
            s.Volume,
            s.SyncErrorSeconds * 1000,
            s.RateCorrectionPpm,
            s.DriftPpm,
            Detail(s))).ToList();

        return new EngineSnapshot(
            state,
            session is not null,
            track,
            Playlist.IndexOf(track),
            TimeSpan.FromSeconds(trackFrame / (double)Options.SampleRate),
            duration,
            buffering,
            TargetLatencySeconds,
            sinks,
            LastCalibration,
            message);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSessionAsync().ConfigureAwait(false);
        HttpClient.Dispose();
        _command.Dispose();
    }

    // ------------------------------------------------------------------ internals

    private static string? Detail(SinkBase s) => s switch
    {
        DlnaSink d => string.Join(" · ", new[]
        {
            d.Format?.ContentType,
            d.TransportState,
            d.PositionLatencyEstimate is { } e ? string.Create(CultureInfo.InvariantCulture, $"pos≈{e * 1000:0} ms") : null,
        }.Where(x => x is not null)),
        LocalPlaybackSink l => l.DeviceKind,
        _ => null,
    };

    private IAudioSource OpenTrack(TrackInfo track)
    {
        var source = _sourceFactory.Open(track);
        if (source.Duration is { } d)
        {
            lock (_durations)
            {
                _durations[track.Id] = d;
            }
        }

        return source;
    }

    private TrackInfo? CurrentTrack()
    {
        var snapshot = GetSnapshot();
        return snapshot.Track ?? _resume?.Track ?? Playlist.At(0);
    }

    private double AudibleFrameAt(Session s, long nowNs) =>
        s.Context.Timeline.FrameAt(nowNs - Ns.FromSeconds(TargetLatencySeconds));

    private long AudibleFrame(Session s, long nowNs) => (long)Math.Floor(AudibleFrameAt(s, nowNs));

    /// <summary>First frame that no output has read yet and that new content can start at.</summary>
    private long CommandFrame(Session s, long nowNs)
    {
        var frame = (long)Math.Ceiling(s.Context.Timeline.FrameAt(nowNs) + Options.CommandLeadSeconds * Options.SampleRate);
        foreach (var sink in Sinks.Where(x => x.IsRunning && x.Cursor is not null))
        {
            frame = Math.Max(frame, sink.NextUnplayedFrame(nowNs) + Options.SampleRate / 20);
        }

        return frame;
    }

    /// <summary>
    /// Silences what each output has not played yet. Returns the timeline frame the listener
    /// stopped hearing (the place to resume from).
    /// </summary>
    private long ApplyCut(Session s, long nowNs, bool open, long? endFrame = null)
    {
        var fade = Math.Max(16, Options.SampleRate / 200);
        var running = Sinks.Where(x => x.IsRunning && x.Cursor is not null).ToList();
        var latency = TargetLatencySeconds;
        long resumeFrame;

        if (Options.PauseMode == PauseMode.Synchronized)
        {
            var gate = running.Count == 0
                ? AudibleFrame(s, nowNs)
                : running.Max(x => x.NextUnplayedFrame(nowNs));
            if (open)
            {
                foreach (var sink in running)
                {
                    sink.Gates!.AddOpen(gate, fade);
                }
            }

            // A synchronized skip needs no gate: the new content simply starts at the command frame.
            return gate;
        }

        var locals = running.Where(x => x.Kind == SinkKind.Local).ToList();
        resumeFrame = locals.Count > 0
            ? locals.Max(x => x.NextUnplayedFrame(nowNs))
            : AudibleFrame(s, nowNs);

        foreach (var sink in running)
        {
            var start = sink.NextUnplayedFrame(nowNs);
            if (open)
            {
                sink.Gates!.AddOpen(start, fade);
            }
            else
            {
                sink.Gates!.Add(start, endFrame ?? long.MaxValue, fade);
            }

            sink.OnInstantCut(start, latency);
        }

        return resumeFrame;
    }

    private void PauseCore(Session s)
    {
        var now = _clock.NowNs;
        var stopFrame = ApplyCut(s, now, open: true);
        var (track, trackFrame) = s.Segments.Locate(stopFrame);
        lock (_sync)
        {
            if (track is not null)
            {
                _resume = (track, trackFrame);
            }
            else if (_startingTrack is not null)
            {
                _resume = (_startingTrack, 0); // paused before the first note was heard
            }
        }

        s.Producer.SetContent(CommandFrame(s, now), null, null, 0);
        SetState(EngineState.Paused, null);
    }

    private async Task StartContentAsync(TrackInfo track, long trackFrame)
    {
        if (_session is not { } s)
        {
            return;
        }

        IAudioSource source;
        try
        {
            source = await Task.Run(() =>
            {
                var src = OpenTrack(track);
                if (trackFrame > 0)
                {
                    src.Seek(TimeSpan.FromSeconds(trackFrame / (double)Options.SampleRate));
                }

                return src;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("engine", $"cannot open '{track.Title}'", ex);
            SetMessage($"cannot open {track.Title}: {ex.Message}");
            return;
        }

        var now = _clock.NowNs;
        var state = State;
        var frame = CommandFrame(s, now);
        if (state == EngineState.Playing)
        {
            ApplyCut(s, now, open: false, endFrame: frame);
        }
        else
        {
            foreach (var sink in Sinks.Where(x => x.IsRunning))
            {
                sink.Gates?.CloseOpen(frame);
            }
        }

        s.Producer.SetContent(frame, track, source, trackFrame);
        lock (_sync)
        {
            _contentEndFrame = null;
            _startingTrack = track;
            _resume = null;
        }

        SetState(EngineState.Playing, null);
        Log.Info("engine", string.Create(CultureInfo.InvariantCulture,
            $"play '{track.DisplayName}' from {trackFrame / (double)Options.SampleRate:0.0} s; audible in {Ns.ToSeconds(s.Context.Timeline.TimeOf(frame) - now) + TargetLatencySeconds:0.00} s"));
    }

    /// <summary>Pause + resume at the same musical position, e.g. when the target latency jumps.</summary>
    private async Task RebufferAsync(string reason)
    {
        await _command.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != EngineState.Playing || _session is not { } s)
            {
                return;
            }

            Log.Info("engine", $"re-buffering: {reason}");
            PauseCore(s);
            Volatile.Write(ref _targetLatency, ComputeTargetLatency(_clock.NowNs));
            if (_resume is { } r)
            {
                await StartContentAsync(r.Track, r.Frame).ConfigureAwait(false);
            }
        }
        finally
        {
            _command.Release();
        }
    }

    private double ComputeTargetLatency(long nowNs)
    {
        var active = Sinks.Where(s => s.IsRunning && s.Status != SinkStatus.Error).ToList();
        if (active.Count == 0)
        {
            return 0.3;
        }

        return Math.Max(0.05, active.Max(s => s.MinimumLatencyAt(nowNs) - s.UserOffsetSeconds));
    }

    private async Task SuperviseAsync(Session session)
    {
        var token = session.Cancel.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(50, token).ConfigureAwait(false);
                SuperviseOnce(session);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("engine", "supervisor", ex);
            }
        }
    }

    private void SuperviseOnce(Session s)
    {
        var now = _clock.NowNs;
        var rate = Options.SampleRate;

        if (!_freezeTarget)
        {
            var wanted = ComputeTargetLatency(now);
            var current = TargetLatencySeconds;
            if (Math.Abs(wanted - current) > 0.03 && State == EngineState.Playing)
            {
                // A large jump would make outputs skip or repeat music: re-buffer instead.
                Volatile.Write(ref _targetLatency, wanted);
                _ = RebufferAsync(string.Create(CultureInfo.InvariantCulture, $"target latency {current * 1000:0} → {wanted * 1000:0} ms"));
            }
            else
            {
                Volatile.Write(ref _targetLatency, wanted);
            }
        }

        // Housekeeping: drop gates/probes/segments nobody can read any more.
        var oldest = (long)(AudibleFrameAt(s, now) - 2.0 * rate);
        foreach (var sink in Sinks.Where(x => x.IsRunning))
        {
            sink.Gates?.Prune(oldest);
        }

        s.Segments.Prune(oldest);

        long? end;
        lock (_sync)
        {
            end = _contentEndFrame;
        }

        if (end is { } e && State == EngineState.Playing && AudibleFrame(s, now) >= e)
        {
            lock (_sync)
            {
                _contentEndFrame = null;
                _startingTrack = null;
            }

            SetState(EngineState.Ready, null);
            Log.Info("engine", "playlist finished");
        }

        // Outputs whose latency changed (TV reconnected, stalled, newly joined).
        var invalid = Sinks.Where(x => x.IsRunning && x.LatencyInvalidated && x.Status == SinkStatus.Active).ToList();
        var calibrationBusy = _calibrating || _calibrationTask is { IsCompleted: false };
        if (invalid.Count > 0 && !calibrationBusy && State != EngineState.Starting)
        {
            if (Options.AutoCalibrate && _microphone is not null && Sinks.Count(x => x.IsRunning && x.Status == SinkStatus.Active) >= 2)
            {
                foreach (var x in invalid)
                {
                    x.LatencyInvalidated = false;
                }

                Log.Info("engine", "re-calibrating: " + string.Join(", ", invalid.Select(x => x.Name)));
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RunCalibrationAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("engine", "re-calibration failed", ex);
                    }
                });
            }
            else
            {
                foreach (var x in invalid)
                {
                    x.LatencyInvalidated = false;
                    x.IsCalibrated = false;
                }
            }
        }

        // Without calibration, fall back (once per connection) on the renderer's own position reports.
        foreach (var d in Sinks.OfType<DlnaSink>().Where(x => x.IsRunning && !x.IsCalibrated && !x.PositionEstimateApplied))
        {
            if (d.PositionLatencyEstimate is { } estimate && estimate > 0.05 && estimate < Options.MaxNetworkLatencySeconds)
            {
                d.PositionEstimateApplied = true;
                if (Math.Abs(estimate - d.CurrentLatencyAt(now)) > 0.03)
                {
                    Log.Info("engine", string.Create(CultureInfo.InvariantCulture, $"{d.Name}: using position-based latency estimate {estimate * 1000:0} ms (no microphone calibration)"));
                    d.LatencySeconds = estimate;
                    d.LatencyReferenceNs = now;
                }
            }
        }
    }

    private void SetState(EngineState state, string? message)
    {
        lock (_sync)
        {
            _state = state;
            _message = message;
        }
    }

    private void SetMessage(string? message)
    {
        lock (_sync)
        {
            _message = message;
        }
    }

    private sealed class Session(EngineContext context, Producer producer, SegmentMap segments, MiniHttpServer http, CancellationTokenSource cancel)
    {
        public EngineContext Context { get; } = context;

        public Producer Producer { get; } = producer;

        public SegmentMap Segments { get; } = segments;

        public MiniHttpServer Http { get; } = http;

        public CancellationTokenSource Cancel { get; } = cancel;

        public Task Supervisor { get; set; } = Task.CompletedTask;
    }
}
