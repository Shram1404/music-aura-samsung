using MusicAura.Core.Timing;

namespace MusicAura.Core.Engine.Sinks;

/// <summary>
/// Platform audio output (Android AudioTrack, a desktop device, or a simulation in tests).
/// Always stereo float at the session sample rate.
/// </summary>
public interface IPcmOutput : IDisposable
{
    int SampleRate { get; }

    /// <summary>Size of the device-side buffer in frames.</summary>
    int BufferFrames { get; }

    /// <summary>Total frames handed to <see cref="Write"/> so far.</summary>
    long FramesWritten { get; }

    void Start();

    /// <summary>Blocking write of interleaved stereo frames.</summary>
    void Write(ReadOnlySpan<float> interleaved);

    /// <summary>
    /// Latest presentation timestamp: frame <paramref name="framePosition"/> (counted like
    /// <see cref="FramesWritten"/>) left the DAC at <paramref name="timeNs"/> (<see cref="IClock"/> time base).
    /// </summary>
    bool TryGetTimestamp(out long framePosition, out long timeNs);

    /// <summary>Non-null if the platform routed the stream somewhere other than requested.</summary>
    string? RoutingProblem { get; }

    void Stop();
}

/// <summary>
/// A phone output. Uses the platform's presentation timestamps to know exactly when each
/// written frame reaches the DAC and continuously steers its read position so that timeline
/// frame F is heard at <c>due(F) + targetLatency + offset</c>. The remaining acoustic delay
/// (e.g. Bluetooth) is measured by calibration and stored in <see cref="SinkBase.LatencySeconds"/>.
/// </summary>
public sealed class LocalPlaybackSink : SinkBase
{
    private readonly Func<int, IPcmOutput> _outputFactory;
    private Thread? _thread;
    private volatile bool _running;
    private IPcmOutput? _output;
    private double _bufferLatency = 0.1;
    private float _gain = 1f;

    public LocalPlaybackSink(string id, string name, Func<int, IPcmOutput> outputFactory, double defaultExtraLatencySeconds = 0)
        : base(id, name, SinkKind.Local)
    {
        _outputFactory = outputFactory;
        LatencySeconds = defaultExtraLatencySeconds;
    }

    /// <summary>Optional platform device description (e.g. "Bluetooth A2DP").</summary>
    public string? DeviceKind { get; init; }

    public override double MinimumLatencyAt(long nowNs)
    {
        var margin = Context?.Options.LocalSafetyMarginSeconds ?? 0.08;
        return _bufferLatency + Math.Max(0, LatencySeconds) + margin;
    }

    internal override (long EarliestNs, long LatestNs) ProbeArrivalWindow(long frame, double targetLatency)
    {
        var expected = ExpectedAudibleNs(frame, targetLatency);
        return (expected - Ns.FromSeconds(0.35), expected + Ns.FromSeconds(0.9));
    }

    private protected override void OnStart(EngineContext context)
    {
        _running = true;
        _thread = new Thread(() => Run(context)) { IsBackground = true, Name = $"aura-out-{Name}", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    private protected override Task OnStopAsync()
    {
        _running = false;
        return Task.Run(() =>
        {
            _thread?.Join(2000);
            _thread = null;
        });
    }

    private void Run(EngineContext ctx)
    {
        ctx.Options.ConfigureAudioThread?.Invoke("output");
        var rate = ctx.SampleRate;
        IPcmOutput output;
        try
        {
            output = _outputFactory(rate);
            output.Start();
        }
        catch (Exception ex)
        {
            ctx.Log.Error("local", $"{Name}: cannot open output", ex);
            SetStatus(SinkStatus.Error, ex.Message);
            return;
        }

        _output = output;
        _bufferLatency = output.BufferFrames / (double)rate;
        ctx.Log.Info("local", $"{Name}: output open, buffer {_bufferLatency * 1000:0} ms");

        var chunk = Math.Max(64, rate / 200); // 5 ms
        var buffer = new float[chunk * 2];
        var dt = chunk / (double)rate;
        var cursor = Cursor!;
        var hadTimestamp = false;

        try
        {
            while (_running)
            {
                var now = ctx.Clock.NowNs;
                long presentNs;
                if (output.TryGetTimestamp(out var pos, out var tsNs) && pos > 0 && output.FramesWritten > pos)
                {
                    // When will the next frame we write reach the DAC?
                    presentNs = tsNs + (long)((output.FramesWritten - pos) * (double)Ns.PerSecond / rate);
                    if (!hadTimestamp)
                    {
                        hadTimestamp = true;
                        SetStatus(SinkStatus.Active, output.RoutingProblem);
                    }
                }
                else
                {
                    // No timestamp yet (output just started): assume the buffer is full.
                    presentNs = now + Ns.FromSeconds(_bufferLatency);
                    if (!hadTimestamp)
                    {
                        SetStatus(SinkStatus.Buffering);
                    }
                }

                var shift = LatencySeconds - ctx.TargetLatency() - UserOffsetSeconds;
                var desired = ctx.Timeline.FrameAt(presentNs + Ns.FromSeconds(shift));
                cursor.Render(buffer, desired, dt);
                ApplyGain(buffer);
                output.Write(buffer);

                if (output.RoutingProblem is { } problem && StatusMessage != problem)
                {
                    SetStatus(Status, problem);
                }
            }
        }
        catch (Exception ex)
        {
            ctx.Log.Error("local", $"{Name}: output failed", ex);
            SetStatus(SinkStatus.Error, ex.Message);
        }
        finally
        {
            try
            {
                output.Stop();
            }
            catch
            {
                // ignore
            }

            output.Dispose();
            _output = null;
        }
    }

    private void ApplyGain(Span<float> buffer)
    {
        var target = (float)(Volume * Volume); // perceptual-ish curve
        var frames = buffer.Length / 2;
        var start = _gain;
        if (Math.Abs(target - start) < 1e-5f)
        {
            if (Math.Abs(target - 1f) > 1e-6f)
            {
                for (var i = 0; i < buffer.Length; i++)
                {
                    buffer[i] *= target;
                }
            }

            return;
        }

        for (var i = 0; i < frames; i++)
        {
            var g = start + (target - start) * (i + 1) / frames;
            buffer[i * 2] *= g;
            buffer[i * 2 + 1] *= g;
        }

        _gain = target;
    }
}
