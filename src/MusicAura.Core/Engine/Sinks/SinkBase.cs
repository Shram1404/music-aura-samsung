using System.Net.Http;
using MusicAura.Core.Audio;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Http;
using MusicAura.Core.Sync;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Engine.Sinks;

public enum SinkKind
{
    /// <summary>An output of the phone itself (speaker, Bluetooth, wired, USB).</summary>
    Local,

    /// <summary>A network renderer fed over HTTP (DLNA TV).</summary>
    Network,
}

public enum SinkStatus
{
    Idle,
    Connecting,
    Buffering,
    Active,
    Error,
}

/// <summary>Everything a sink needs from the running engine.</summary>
internal sealed class EngineContext
{
    public required IClock Clock { get; init; }

    public required TimelineClock Timeline { get; init; }

    public required TimelineBuffer Buffer { get; init; }

    public required SincKernel Kernel { get; init; }

    public required EngineOptions Options { get; init; }

    public required AuraLog Log { get; init; }

    public required Func<double> TargetLatency { get; init; }

    public MiniHttpServer? HttpServer { get; init; }

    public HttpClient? HttpClient { get; init; }

    public Func<System.Net.IPAddress, System.Net.IPAddress?>? LocalAddressResolver { get; init; }

    public int SampleRate => Timeline.SampleRate;
}

/// <summary>
/// An audio output taking part in the session. The engine decides <i>when</i> each timeline
/// frame must be audible (target latency + the user's per-device offset); the sink is
/// responsible for making that happen given its own, measured latency.
/// </summary>
public abstract class SinkBase
{
    private double _volume = 1.0;

    protected SinkBase(string id, string name, SinkKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    public string Id { get; }

    public string Name { get; protected set; }

    public SinkKind Kind { get; }

    public bool Enabled { get; set; } = true;

    /// <summary>How much later (+) or earlier (−) this device should play relative to the others.</summary>
    public double UserOffsetSeconds { get; set; }

    public double Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 1);
            if (Math.Abs(v - _volume) < 1e-6)
            {
                return;
            }

            _volume = v;
            OnVolumeChanged(v);
        }
    }

    public SinkStatus Status { get; protected set; } = SinkStatus.Idle;

    public string? StatusMessage { get; protected set; }

    /// <summary>
    /// Local sinks: acoustic latency beyond what the platform's output timestamps account for
    /// (Bluetooth transport, speaker DSP, sound travel). Network sinks: latency from sending a
    /// frame to hearing it, valid at <see cref="LatencyReferenceNs"/>.
    /// </summary>
    public double LatencySeconds { get; set; }

    public bool IsCalibrated { get; set; }

    public DateTime? CalibratedAtUtc { get; set; }

    public double? CalibrationConfidence { get; set; }

    /// <summary>Clock drift of a network renderer relative to the phone, in parts per million (positive = renderer runs fast).</summary>
    public double DriftPpm { get; protected set; }

    /// <summary>Set when the latency is known to have changed (e.g. the TV reconnected).</summary>
    public bool LatencyInvalidated { get; internal set; }

    internal long LatencyReferenceNs { get; set; }

    internal SinkCursor? Cursor { get; private protected set; }

    internal EngineContext? Context { get; private set; }

    public bool IsRunning => Context is not null;

    /// <summary>Last measured difference between where the output is and where it should be.</summary>
    public virtual double SyncErrorSeconds => Cursor?.Controller.FilteredError ?? 0;

    /// <summary>Current playback-rate correction in ppm.</summary>
    public virtual double RateCorrectionPpm => Cursor is null ? 0 : (Cursor.LastStep - 1) * 1e6;

    /// <summary>Mute ranges applied to this output.</summary>
    internal GateList? Gates => Cursor?.Gates;

    /// <summary>Calibration probes scheduled for this output.</summary>
    internal OverlayList? Overlays => Cursor?.Overlays;

    /// <summary>First timeline frame this output has not rendered yet (anything from here on can still be changed).</summary>
    internal virtual long NextUnplayedFrame(long nowNs) =>
        Cursor is { } c ? (long)Math.Ceiling(c.Position) : (long)Math.Ceiling(Context!.Timeline.FrameAt(nowNs));

    /// <summary>Makes the output jump straight to its target position on the next render.</summary>
    internal virtual void UnlockCursors() => Cursor?.Unlock();

    /// <summary>Smallest end-to-end latency this sink can achieve at <paramref name="nowNs"/>.</summary>
    public abstract double MinimumLatencyAt(long nowNs);

    /// <summary>Time window in which the start of a probe placed at <paramref name="frame"/> can be heard.</summary>
    internal abstract (long EarliestNs, long LatestNs) ProbeArrivalWindow(long frame, double targetLatency);

    /// <summary>Folds a measured timing error (heard − expected) into the latency model.</summary>
    internal virtual void ApplyCalibration(double errorSeconds, double confidence, long nowNs)
    {
        LatencySeconds = CurrentLatencyAt(nowNs) + errorSeconds;
        LatencyReferenceNs = nowNs;
        IsCalibrated = true;
        LatencyInvalidated = false;
        CalibratedAtUtc = DateTime.UtcNow;
        CalibrationConfidence = confidence;
    }

    /// <summary>The latency model value at <paramref name="nowNs"/> (drift-adjusted for network sinks).</summary>
    public virtual double CurrentLatencyAt(long nowNs) => LatencySeconds;

    /// <summary>When frame <paramref name="frame"/> should be heard from this sink.</summary>
    internal long ExpectedAudibleNs(long frame, double targetLatency) =>
        Context!.Timeline.TimeOf(frame) + Ns.FromSeconds(targetLatency + UserOffsetSeconds);

    internal void Attach(EngineContext context)
    {
        Context = context;
        Cursor = new SinkCursor(context.Buffer, context.Kernel, context.SampleRate, new RateController());
        Status = SinkStatus.Connecting;
        StatusMessage = null;
        OnStart(context);
    }

    internal async Task DetachAsync()
    {
        try
        {
            await OnStopAsync().ConfigureAwait(false);
        }
        finally
        {
            Context = null;
            Cursor = null;
            Status = SinkStatus.Idle;
        }
    }

    /// <summary>Starts output threads/connections. Must not block.</summary>
    private protected abstract void OnStart(EngineContext context);

    private protected abstract Task OnStopAsync();

    /// <summary>Called by the engine on pause/stop/skip when the output should go quiet as soon as possible.</summary>
    internal virtual void OnInstantCut(long gateStartFrame, double targetLatency)
    {
    }

    protected virtual void OnVolumeChanged(double volume)
    {
    }

    /// <summary>Updates <see cref="Volume"/> from the device without echoing it back.</summary>
    protected void SetVolumeSilently(double volume) => _volume = Math.Clamp(volume, 0, 1);

    protected void SetStatus(SinkStatus status, string? message = null)
    {
        Status = status;
        StatusMessage = message;
    }

    public override string ToString() => $"{Name} [{Kind}]";
}
