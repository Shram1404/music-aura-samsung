using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MusicAura.Core.Audio;
using MusicAura.Core.Dlna;
using MusicAura.Core.Http;
using MusicAura.Core.Sync;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Engine.Sinks;

/// <summary>
/// A DLNA MediaRenderer (e.g. the built-in renderer of a Samsung Smart TV) playing an endless
/// live PCM stream served by the phone. The renderer is told to play our URL once per session;
/// pause/skip/seek happen inside the stream, so the TV's buffering latency stays constant
/// and only has to be measured once.
/// </summary>
public sealed class DlnaSink : SinkBase
{
    private const double ActiveAfterSeconds = 1.5;

    private readonly object _connectionLock = new();
    private readonly object _muteLock = new();
    private readonly PositionTracker _tracker = new();
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;
    private readonly List<Connection> _connections = [];
    private string? _path;
    private int _connectionCount;
    private int _playingNumber;
    private bool _everActive;
    private bool? _userMuted;
    private bool _mutedByUs;
    private long _unmuteAtNs;
    private int _volumeVersion;

    public DlnaSink(DlnaRenderer renderer)
        : base(renderer.Id, renderer.FriendlyName, SinkKind.Network)
    {
        Renderer = renderer;
    }

    public DlnaRenderer Renderer { get; }

    public StreamFormat? Format { get; private set; }

    public Uri? StreamUri { get; private set; }

    public string? TransportState { get; private set; }

    public int ConnectionCount => _connectionCount;

    /// <summary>Latency estimated from the renderer's reported position (no microphone needed), if available.</summary>
    public double? PositionLatencyEstimate { get; private set; }

    /// <summary>Set once the engine has adopted <see cref="PositionLatencyEstimate"/> for the current connection.</summary>
    internal bool PositionEstimateApplied { get; set; }

    /// <summary>Time until which the TV is muted by an instant pause, or 0.</summary>
    internal long PendingUnmuteNs
    {
        get
        {
            lock (_muteLock)
            {
                return _mutedByUs ? _unmuteAtNs : 0;
            }
        }
    }

    /// <summary>True when some stream connection has been running long enough to be the one the TV plays.</summary>
    internal bool HasStableConnection(long nowNs)
    {
        lock (_connectionLock)
        {
            return _connections.Any(c => Ns.ToSeconds(nowNs - c.StartNs) >= ActiveAfterSeconds);
        }
    }

    public override double SyncErrorSeconds => Newest()?.Cursor.Controller.FilteredError ?? 0;

    public override double RateCorrectionPpm => Newest() is { } c ? (c.Cursor.LastStep - 1) * 1e6 : 0;

    internal override long NextUnplayedFrame(long nowNs)
    {
        lock (_connectionLock)
        {
            if (_connections.Count > 0)
            {
                return _connections.Max(c => (long)Math.Ceiling(c.Cursor.Position));
            }
        }

        // Nothing connected: whatever is due from now on has not been sent anywhere.
        var ctx = Context!;
        return (long)Math.Ceiling(ctx.Timeline.FrameAt(nowNs - Ns.FromSeconds(SendDelayAt(nowNs, ctx.TargetLatency()))));
    }

    internal override void UnlockCursors()
    {
        lock (_connectionLock)
        {
            foreach (var c in _connections)
            {
                c.Cursor.Unlock();
            }
        }
    }

    public override double CurrentLatencyAt(long nowNs)
    {
        var drift = Context?.Options.DriftCompensation == true ? DriftPpm * 1e-6 : 0;
        return LatencySeconds - drift * Ns.ToSeconds(nowNs - LatencyReferenceNs);
    }

    public override double MinimumLatencyAt(long nowNs) => CurrentLatencyAt(nowNs);

    /// <summary>How long we hold frames back before sending them to this renderer.</summary>
    internal double SendDelayAt(long nowNs, double targetLatency) =>
        Math.Max(0, targetLatency + UserOffsetSeconds - CurrentLatencyAt(nowNs));

    internal override (long EarliestNs, long LatestNs) ProbeArrivalWindow(long frame, double targetLatency)
    {
        var ctx = Context!;
        var due = ctx.Timeline.TimeOf(frame);
        var send = due + Ns.FromSeconds(SendDelayAt(due, targetLatency));
        return (send + Ns.FromSeconds(0.02), send + Ns.FromSeconds(ctx.Options.MaxNetworkLatencySeconds));
    }

    internal override void OnInstantCut(long gateStartFrame, double targetLatency)
    {
        if (Renderer.RenderingControl is null || _userMuted == true || !_everActive)
        {
            return;
        }

        // The TV keeps playing what it has buffered; mute it until the gated (silent) frames reach it.
        var unmuteAt = ExpectedAudibleNs(gateStartFrame, targetLatency) + Ns.FromSeconds(0.08);
        var startMute = false;
        lock (_muteLock)
        {
            _unmuteAtNs = Math.Max(_unmuteAtNs, unmuteAt);
            if (!_mutedByUs)
            {
                _mutedByUs = true;
                startMute = true;
            }
        }

        if (startMute)
        {
            _ = MuteCycleAsync();
        }
    }

    protected override void OnVolumeChanged(double volume)
    {
        if (!IsRunning)
        {
            return;
        }

        var version = Interlocked.Increment(ref _volumeVersion);
        _ = Task.Run(async () =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            if (version != Volatile.Read(ref _volumeVersion))
            {
                return; // a newer value is on its way
            }

            try
            {
                await Renderer.SetVolumeAsync((int)Math.Round(volume * 100)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Context?.Log.Warn("dlna", $"{Name}: SetVolume failed: {ex.Message}");
            }
        });
    }

    private protected override void OnStart(EngineContext context)
    {
        if (LatencySeconds <= 0)
        {
            LatencySeconds = context.Options.DefaultNetworkLatencySeconds;
        }

        LatencyReferenceNs = context.Clock.NowNs;
        _connectionCount = 0;
        _playingNumber = 0;
        _everActive = false;
        _tracker.Reset();
        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;
        _runTask = Task.Run(() => RunAsync(context, token));
    }

    private protected override async Task OnStopAsync()
    {
        var ctx = Context;
        _lifetime?.Cancel();
        lock (_connectionLock)
        {
            foreach (var c in _connections)
            {
                c.Cancel();
            }

            _connections.Clear();
        }

        if (_path is not null)
        {
            ctx?.HttpServer?.Unmap(_path);
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch
            {
                // already logged
            }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            bool unmute;
            lock (_muteLock)
            {
                unmute = _mutedByUs;
                _mutedByUs = false;
            }

            if (unmute)
            {
                await Renderer.SetMuteAsync(false, cts.Token).ConfigureAwait(false);
            }

            await Renderer.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx?.Log.Debug("dlna", $"{Name}: stop: {ex.Message}");
        }

        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async Task RunAsync(EngineContext ctx, CancellationToken token)
    {
        var log = ctx.Log;
        try
        {
            SetStatus(SinkStatus.Connecting, "connecting");
            IReadOnlyList<ProtocolInfoEntry> protocols = [];
            try
            {
                protocols = await Renderer.GetSinkProtocolInfoAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn("dlna", $"{Name}: GetProtocolInfo failed ({ex.Message}); assuming WAV support");
            }

            Format = StreamFormat.Choose(protocols, ctx.SampleRate, 2, ctx.Options.StreamFormat);
            log.Info("dlna", $"{Name}: streaming {Format.ContentType} ({protocols.Count} sink protocols advertised)");

            var server = ctx.HttpServer ?? throw new InvalidOperationException("HTTP server not running.");
            var rendererIp = Renderer.Address ?? (await Dns.GetHostAddressesAsync(Renderer.Location.Host, token).ConfigureAwait(false))
                .First(a => a.AddressFamily == AddressFamily.InterNetwork);
            var local = ctx.LocalAddressResolver?.Invoke(rendererIp) ?? RendererDiscovery.LocalAddressFor(rendererIp)
                ?? throw new InvalidOperationException("No network route to the renderer.");
            _path = $"/aura/{Guid.NewGuid():N}.{Format.FileExtension}";
            server.Map(_path, HandleRequestAsync);
            StreamUri = new Uri($"http://{local}:{server.Port.ToString(CultureInfo.InvariantCulture)}{_path}");

            await StartTransportAsync(ctx, token).ConfigureAwait(false);

            try
            {
                _userMuted = await Renderer.GetMuteAsync(token).ConfigureAwait(false);
                if (await Renderer.GetVolumeAsync(token).ConfigureAwait(false) is { } v)
                {
                    SetVolumeSilently(v / 100.0);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Debug("dlna", $"{Name}: RenderingControl unavailable: {ex.Message}");
            }

            await MonitorAsync(ctx, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.Error("dlna", $"{Name}: failed", ex);
            SetStatus(SinkStatus.Error, Describe(ex));
        }
    }

    private async Task StartTransportAsync(EngineContext ctx, CancellationToken token)
    {
        var metadata = DidlLite.ForAudioStream(ctx.Options.StreamTitle, "Music Aura", StreamUri!, Format!);
        try
        {
            await Renderer.StopAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Many renderers answer Stop with an error when idle.
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Renderer.SetAvTransportUriAsync(StreamUri!, metadata, token).ConfigureAwait(false);
                await Renderer.PlayAsync(token).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < 3)
            {
                ctx.Log.Warn("dlna", $"{Name}: start attempt {attempt} failed: {ex.Message}");
                SetStatus(SinkStatus.Connecting, Describe(ex));
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), token).ConfigureAwait(false);
            }
        }

        SetStatus(SinkStatus.Buffering, "waiting for the TV to fetch the stream");
        ctx.Log.Info("dlna", $"{Name}: playing {StreamUri}");
    }

    private async Task MonitorAsync(EngineContext ctx, CancellationToken token)
    {
        var poll = ctx.Options.PositionPollInterval;
        var positionSupported = true;
        var positionFailures = 0;
        var lastTransportCheck = 0L;
        var stoppedPolls = 0;
        var restarts = 0;

        while (!token.IsCancellationRequested)
        {
            var now = ctx.Clock.NowNs;

            if (now - lastTransportCheck > Ns.FromSeconds(2))
            {
                lastTransportCheck = now;
                try
                {
                    var info = await Renderer.GetTransportInfoAsync(token).ConfigureAwait(false);
                    TransportState = info.State;
                    var stopped = info.State is "STOPPED" or "NO_MEDIA_PRESENT";
                    stoppedPolls = stopped ? stoppedPolls + 1 : 0;
                    if (stoppedPolls >= 2 && _everActive && restarts < 5)
                    {
                        restarts++;
                        stoppedPolls = 0;
                        ctx.Log.Warn("dlna", $"{Name}: renderer stopped on its own ({info.State}); restarting stream");
                        LatencyInvalidated = true;
                        await StartTransportAsync(ctx, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ctx.Log.Debug("dlna", $"{Name}: GetTransportInfo: {ex.Message}");
                }
            }

            UpdateActiveStatus(ctx.Clock.NowNs);

            if (positionSupported && _everActive)
            {
                try
                {
                    var t0 = ctx.Clock.NowNs;
                    var pos = await Renderer.GetPositionInfoAsync(token).ConfigureAwait(false);
                    var t1 = ctx.Clock.NowNs;
                    positionFailures = 0;
                    if (pos.RelTime is { } rel)
                    {
                        FeedPosition((t0 + t1) / 2, rel.TotalSeconds, ctx);
                    }
                }
                catch (UpnpException ex) when (ex.ErrorCode is 401 or 602)
                {
                    positionSupported = false;
                    ctx.Log.Info("dlna", $"{Name}: GetPositionInfo not supported; no drift tracking");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (++positionFailures > 20)
                    {
                        positionSupported = false;
                        ctx.Log.Info("dlna", $"{Name}: GetPositionInfo keeps failing ({ex.Message}); no drift tracking");
                    }
                }
            }

            // Dithered interval: polls must not phase-lock to the renderer's one-second ticks,
            // otherwise the tick-time quantisation error becomes a systematic drift error.
            await Task.Delay(poll * (0.8 + 0.4 * Random.Shared.NextDouble()), token).ConfigureAwait(false);
        }
    }

    private void UpdateActiveStatus(long nowNs)
    {
        if (Status == SinkStatus.Error)
        {
            return;
        }

        if (HasStableConnection(nowNs) && TransportState is null or "PLAYING" or "TRANSITIONING")
        {
            if (Status != SinkStatus.Active)
            {
                SetStatus(SinkStatus.Active, null);
                _everActive = true;
            }
        }
        else if (Status == SinkStatus.Active && !HasStableConnection(nowNs))
        {
            // Reconnected: wait until the new connection has settled before using (or measuring) it.
            SetStatus(SinkStatus.Buffering, "renderer reconnecting");
        }
        else if (Status == SinkStatus.Active && TransportState is "PAUSED_PLAYBACK")
        {
            SetStatus(SinkStatus.Active, "paused on the TV");
        }
    }

    private void FeedPosition(long midNs, double relSeconds, EngineContext ctx)
    {
        // The renderer reports the position of what it plays: the newest settled connection.
        var connection = Newest();
        if (connection is null || connection.StartNs == long.MaxValue)
        {
            return;
        }

        var connectionStart = connection.StartNs;

        var t = Ns.ToSeconds(midNs - connectionStart);
        if (!_tracker.Add(t, relSeconds))
        {
            ctx.Log.Info("dlna", $"{Name}: renderer position jumped; drift tracking restarted");
        }

        if (_tracker.Fit() is not { } fit)
        {
            return;
        }

        // Content sent at connection start + x is played when the renderer position reaches x
        // (plus a fixed, unknown reporting offset), so latency(t) ≈ t − position(t).
        if (fit.ResidualSeconds < 0.25 && fit.SpanSeconds >= 15)
        {
            PositionLatencyEstimate = t - fit.PositionAt(t);
        }

        if (ctx.Options.DriftCompensation && fit.Points >= 60 && fit.SpanSeconds >= 120 &&
            fit.ResidualSeconds < 0.2 && Math.Abs(fit.DriftPpm) < 500)
        {
            var now = ctx.Clock.NowNs;
            // Re-anchor so the latency model stays continuous when the drift estimate changes.
            LatencySeconds = CurrentLatencyAt(now);
            LatencyReferenceNs = now;
            DriftPpm = fit.DriftPpm;
        }
    }

    private async Task MuteCycleAsync()
    {
        var ctx = Context;
        if (ctx is null)
        {
            return;
        }

        try
        {
            await Renderer.SetMuteAsync(true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Log.Warn("dlna", $"{Name}: mute failed: {ex.Message}");
        }

        while (true)
        {
            long wait;
            lock (_muteLock)
            {
                if (!_mutedByUs)
                {
                    return; // session stopped and unmuted us already
                }

                wait = _unmuteAtNs - ctx.Clock.NowNs;
                if (wait <= 0)
                {
                    _mutedByUs = false;
                    _unmuteAtNs = 0;
                    break;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(Ns.ToMilliseconds(wait) + 1, 500))).ConfigureAwait(false);
        }

        try
        {
            await Renderer.SetMuteAsync(false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ctx.Log.Warn("dlna", $"{Name}: unmute failed: {ex.Message}");
        }
    }

    private async Task HandleRequestAsync(HttpRequestHead request, NetworkStream stream, CancellationToken serverToken)
    {
        var ctx = Context;
        var format = Format;
        if (ctx is null || format is null)
        {
            await MiniHttpServer.WriteSimpleAsync(stream, 503, "Service Unavailable", "text/plain", [], request.IsHead, serverToken).ConfigureAwait(false);
            return;
        }

        var mode = ctx.Options.HttpBodyMode;
        if (mode == HttpBodyMode.Chunked && request.Version == "HTTP/1.0")
        {
            mode = HttpBodyMode.CloseDelimited;
        }

        var dataBytes = mode == HttpBodyMode.HugeContentLength ? 2_000_000_000u : uint.MaxValue;
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Content-Type", format.ContentType),
            new("Cache-Control", "no-cache, no-store"),
            new("Pragma", "no-cache"),
            new("Accept-Ranges", "none"),
            new("Connection", "close"),
            new("transferMode.dlna.org", "Streaming"),
            new("contentFeatures.dlna.org", format.ContentFeatures),
            new("realTimeInfo.dlna.org", "DLNA.ORG_TLAG=*"),
            new("Server", MiniHttpServer.ServerHeader),
        };
        if (mode == HttpBodyMode.HugeContentLength)
        {
            var length = (long)dataBytes + (format.Encoding == StreamEncoding.Wav ? WavFormat.HeaderSize : 0);
            headers.Add(new("Content-Length", length.ToString(CultureInfo.InvariantCulture)));
        }
        else if (mode == HttpBodyMode.Chunked)
        {
            headers.Add(new("Transfer-Encoding", "chunked"));
        }

        ctx.Log.Info("dlna", $"{Name}: {request.Method} from {request.Remote} (range: {request.Header("Range") ?? "none"}, UA: {request.Header("User-Agent") ?? "-"})");
        await MiniHttpServer.WriteHeadAsync(stream, 200, "OK", headers, serverToken).ConfigureAwait(false);
        if (request.IsHead)
        {
            return;
        }

        // Every request gets its own live stream (and its own read position). A TV may probe with a
        // short extra connection while it keeps playing the first one, so nothing is cancelled here.
        var cursor = new SinkCursor(ctx.Buffer, ctx.Kernel, ctx.SampleRate, new RateController(), Gates, Overlays);
        var connection = new Connection(CancellationTokenSource.CreateLinkedTokenSource(serverToken), cursor);
        lock (_connectionLock)
        {
            _connections.Add(connection);
        }

        connection.Number = Interlocked.Increment(ref _connectionCount);
        try
        {
            await Task.Factory.StartNew(
                () => SendLoop(ctx, format, mode, dataBytes, stream, connection),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            OnConnectionClosed(connection, ctx);
            connection.Dispose();
        }
    }

    private Connection? Newest()
    {
        lock (_connectionLock)
        {
            return _connections.Count == 0 ? null : _connections[^1];
        }
    }

    private void OnConnectionClosed(Connection connection, EngineContext ctx)
    {
        bool none;
        lock (_connectionLock)
        {
            _connections.Remove(connection);
            none = _connections.Count == 0;
        }

        ctx.Log.Debug("dlna", $"{Name}: connection #{connection.Number} closed");
        if (none && Status == SinkStatus.Active)
        {
            SetStatus(SinkStatus.Buffering, "renderer disconnected");
        }
    }

    /// <summary>
    /// A connection has streamed long enough that the TV is evidently playing it. If it replaces an
    /// earlier one, the TV re-buffered from scratch and its latency has to be measured again.
    /// </summary>
    private void OnConnectionPlaying(Connection connection, EngineContext ctx)
    {
        int previous;
        lock (_connectionLock)
        {
            previous = _playingNumber;
            if (connection.Number <= previous)
            {
                return;
            }

            _playingNumber = connection.Number;
        }

        if (previous > 0 && _everActive)
        {
            ctx.Log.Warn("dlna", $"{Name}: renderer now plays stream connection #{connection.Number}; latency must be re-measured");
            _tracker.Reset();
            PositionLatencyEstimate = null;
            PositionEstimateApplied = false;
            LatencyInvalidated = true;
        }
    }

    private void SendLoop(EngineContext ctx, StreamFormat format, HttpBodyMode mode, uint dataBytes, NetworkStream stream, Connection connection)
    {
        ctx.Options.ConfigureAudioThread?.Invoke("network");
        var rate = ctx.SampleRate;
        var maxChunk = rate / 20; // 50 ms
        var minChunk = rate / 50; // 20 ms: ~50 writes per second is plenty for a multi-second TV buffer
        var floats = new float[maxChunk * 2];
        var bytes = new byte[maxChunk * 4 + 16];
        var cursor = connection.Cursor;
        var token = connection.Token;
        stream.WriteTimeout = 10_000;
        // A superseded connection may be blocked in Write(); closing the socket releases it.
        using var registration = token.Register(stream.Dispose);

        if (format.Encoding == StreamEncoding.Wav)
        {
            WriteBody(stream, mode, WavFormat.CreateHeader(rate, 2, 16, dataBytes));
        }

        long bytesSent = 0;
        long sent = 0;
        cursor.Unlock();
        connection.StartNs = ctx.Clock.NowNs;
        var start = connection.StartNs;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = ctx.Clock.NowNs;
                var due = (long)((now - start) * (double)rate / Ns.PerSecond);
                var backlog = due - sent;
                if (backlog < minChunk)
                {
                    Thread.Sleep(5);
                    continue;
                }

                if (backlog > rate)
                {
                    // The renderer stopped reading for over a second: its buffer level (= latency) changed.
                    ctx.Log.Warn("dlna", $"{Name}: renderer stalled on connection #{connection.Number} for {backlog / (double)rate:0.0} s; skipping ahead");
                    sent = due - minChunk;
                    backlog = minChunk;
                    if (connection.Number == Volatile.Read(ref _playingNumber))
                    {
                        // The stream it plays from stalled, so its buffer level changed.
                        LatencyInvalidated = true;
                    }
                }

                var n = (int)Math.Min(backlog, maxChunk);
                var frameDueNs = start + (long)(sent * (double)Ns.PerSecond / rate);
                var delay = SendDelayAt(frameDueNs, ctx.TargetLatency());
                var desired = ctx.Timeline.FrameAt(frameDueNs - Ns.FromSeconds(delay));
                var span = floats.AsSpan(0, n * 2);
                cursor.Render(span, desired, n / (double)rate);
                PcmConvert.FloatToInt16Bytes(span, bytes, format.BigEndian);
                WriteBody(stream, mode, bytes.AsSpan(0, n * 4));
                sent += n;
                bytesSent += n * 4;
                connection.FramesSent = sent;
                if (!connection.Played && now - start > Ns.FromSeconds(ActiveAfterSeconds))
                {
                    connection.Played = true;
                    OnConnectionPlaying(connection, ctx);
                }
                if (mode == HttpBodyMode.HugeContentLength && bytesSent >= dataBytes - maxChunk * 4)
                {
                    ctx.Log.Info("dlna", $"{Name}: advertised stream length reached");
                    break;
                }
            }

            if (mode == HttpBodyMode.Chunked)
            {
                stream.Write("0\r\n\r\n"u8);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested)
            {
                ctx.Log.Info("dlna", $"{Name}: stream connection closed after {Ns.ToSeconds(ctx.Clock.NowNs - start):0.0} s ({ex.Message})");
            }
        }
    }

    private static void WriteBody(NetworkStream stream, HttpBodyMode mode, ReadOnlySpan<byte> data)
    {
        if (mode == HttpBodyMode.Chunked)
        {
            stream.Write(Encoding.ASCII.GetBytes(data.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"));
            stream.Write(data);
            stream.Write("\r\n"u8);
        }
        else
        {
            stream.Write(data);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UpnpException { HttpStatus: 401 or 403 } =>
            "TV refused access — accept the connection prompt on the TV (Settings › Connection › External Device Manager › Device Connection Manager)",
        UpnpException { ErrorCode: 714 or 716 } u => $"TV rejected the stream format ({u.ErrorCode}); try another format in settings",
        UpnpException u => $"TV error {u.ErrorCode}: {u.ErrorDescription}",
        TimeoutException => "TV does not respond (is it on and on the same Wi-Fi?)",
        HttpRequestException h => $"network error: {h.Message}",
        _ => ex.Message,
    };

    private sealed class Connection : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public Connection(CancellationTokenSource cts, SinkCursor cursor)
        {
            _cts = cts;
            Token = cts.Token;
            Cursor = cursor;
        }

        public CancellationToken Token { get; }

        public SinkCursor Cursor { get; }

        /// <summary>Sequence number of this request within the session (1 = first).</summary>
        public int Number { get; set; }

        /// <summary>Streamed long enough that the renderer is presumably playing it.</summary>
        public bool Played { get; set; }

        public long StartNs { get; set; } = long.MaxValue;

        public long FramesSent { get; set; }

        public void Cancel()
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _cts.Dispose();
    }
}
