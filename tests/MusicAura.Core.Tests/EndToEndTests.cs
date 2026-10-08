using System.Net;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Dlna;
using MusicAura.Core.Dsp;
using MusicAura.Core.Engine;
using MusicAura.Core.Engine.Sinks;
using MusicAura.Core.Tests.Fakes;
using MusicAura.Core.Timing;
using Xunit.Abstractions;

namespace MusicAura.Core.Tests;

/// <summary>
/// Full-stack tests in real time: a fake DLNA TV that pulls our HTTP stream and buffers it, a
/// simulated Bluetooth output with its own clock and hidden latency, and a simulated room
/// microphone. Nothing is told the true latencies — the engine has to measure them.
/// </summary>
[Trait("Category", "Integration")]
public class EndToEndTests
{
    private const int Rate = 44100;
    private readonly ITestOutputHelper _output;

    public EndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Calibration_aligns_tv_and_bluetooth_and_pause_resume_keeps_position()
    {
        var clock = MonotonicClock.Instance;
        var log = NewLog();
        await using var tv = new FakeTv(clock, prebufferSeconds: 1.2, extraLatencySeconds: 0.09);
        tv.Start();
        var bt = new SimulatedPcmOutput(clock, Rate, bufferSeconds: 0.06, skewPpm: 60, extraLatencySeconds: 0.21);
        var room = new SimulatedRoom(clock);
        room.AddSource(tv.AudibleAt, 0.7f);
        room.AddSource(bt.AudibleAt, 0.5f);

        var options = new EngineOptions
        {
            DefaultNetworkLatencySeconds = 0.8,
            MaxNetworkLatencySeconds = 4,
            DriftCompensation = false,
        };
        await using var engine = new MultiroomEngine(options, new NoiseTrackFactory(), room, log, clock, _ => IPAddress.Loopback);
        var renderer = await DlnaRenderer.FromLocationAsync(engine.HttpClient, tv.Location);
        Assert.NotNull(renderer);
        engine.AddSink(new DlnaSink(renderer));
        engine.AddSink(new LocalPlaybackSink("bt", "BT speaker", _ => bt, defaultExtraLatencySeconds: 0.1));
        engine.Playlist.Add([NoiseTrackFactory.Track("t1"), NoiseTrackFactory.Track("t2")]);

        await engine.StartSessionAsync();
        var report = engine.LastCalibration;
        Assert.NotNull(report);
        Assert.True(report.Success, report.Summary);
        Assert.Contains("SetAVTransportURI", tv.Actions);

        var tvSink = (DlnaSink)engine.FindSink(renderer.Id)!;
        var btSink = engine.FindSink("bt")!;
        _output.WriteLine($"TV latency {tvSink.LatencySeconds * 1000:0} ms, BT extra {btSink.LatencySeconds * 1000:0} ms, target {engine.TargetLatencySeconds * 1000:0} ms");
        // The fake TV buffers 1.2 s and adds 90 ms; the BT output hides 210 ms. The microphone's
        // start time is reported 13.7 ms late, which shifts both absolute numbers equally.
        Assert.InRange(tvSink.LatencySeconds, 1.25, 1.40);
        Assert.InRange(btSink.LatencySeconds, 0.18, 0.26);

        await engine.PlayAsync();
        Assert.Equal(EngineState.Playing, engine.State);
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 2.5));

        var offset = MeasureOffsetMs(tv.AudibleAt, bt.AudibleAt, clock);
        _output.WriteLine($"TV vs BT offset after calibration: {offset:0.00} ms");
        Assert.InRange(offset, -3, 3);

        // Instant pause: BT goes quiet at once and the TV is muted while its buffer drains.
        var before = engine.GetSnapshot().Position;
        await engine.PauseAsync();
        await Task.Delay(200);
        Assert.True(tv.Muted, "TV should be muted right after pause");
        // BT still has its device buffer (60 ms) and hidden 210 ms in flight.
        await Task.Delay(500);
        Assert.True(Rms(bt.AudibleAt, clock, 0.1) < 1e-4, "BT should be silent");
        var paused = engine.GetSnapshot();
        Assert.Equal(EngineState.Paused, paused.State);
        Assert.InRange((paused.Position - before).TotalSeconds, 0, 0.6);

        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 0.5));
        Assert.False(tv.Muted, "TV should be unmuted once it only has silence left");

        await engine.PlayAsync();
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 1.5));
        var resumed = engine.GetSnapshot();
        Assert.Equal(paused.Track, resumed.Track);
        Assert.InRange((resumed.Position - paused.Position).TotalSeconds, 1.0, 2.2);
        Assert.InRange(MeasureOffsetMs(tv.AudibleAt, bt.AudibleAt, clock), -3, 3);
        Assert.Equal(1, tv.StreamRequests); // the TV stream was never restarted
    }

    [Fact]
    public async Task Skipping_tracks_switches_all_outputs_together()
    {
        var clock = MonotonicClock.Instance;
        var log = NewLog();
        await using var tv = new FakeTv(clock, prebufferSeconds: 0.9, extraLatencySeconds: 0.05);
        tv.Start();
        var phone = new SimulatedPcmOutput(clock, Rate, bufferSeconds: 0.04, skewPpm: -30, extraLatencySeconds: 0.01);
        var room = new SimulatedRoom(clock);
        room.AddSource(tv.AudibleAt, 0.6f);
        room.AddSource(phone.AudibleAt, 0.6f);

        var options = new EngineOptions { DefaultNetworkLatencySeconds = 0.5, MaxNetworkLatencySeconds = 3, DriftCompensation = false };
        await using var engine = new MultiroomEngine(options, new NoiseTrackFactory(), room, log, clock, _ => IPAddress.Loopback);
        var renderer = await DlnaRenderer.FromLocationAsync(engine.HttpClient, tv.Location);
        engine.AddSink(new DlnaSink(renderer!));
        engine.AddSink(new LocalPlaybackSink("speaker", "Phone speaker", _ => phone));
        var t1 = NoiseTrackFactory.Track("one");
        var t2 = NoiseTrackFactory.Track("two");
        engine.Playlist.Add([t1, t2]);

        await engine.PlayAsync();
        Assert.True(engine.LastCalibration?.Success, engine.LastCalibration?.Summary);
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 1));
        Assert.Equal(t1, engine.GetSnapshot().Track);

        await engine.NextAsync();
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 1.5));
        var snap = engine.GetSnapshot();
        Assert.Equal(t2, snap.Track);
        Assert.InRange(snap.Position.TotalSeconds, 0.8, 2.2);
        Assert.InRange(MeasureOffsetMs(tv.AudibleAt, phone.AudibleAt, clock), -3, 3);

        await engine.SeekAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 1));
        Assert.InRange(engine.GetSnapshot().Position.TotalSeconds, 30.5, 31.8);
        Assert.InRange(MeasureOffsetMs(tv.AudibleAt, phone.AudibleAt, clock), -3, 3);

        // A short probing connection (some TVs do this) must neither disturb playback nor force a re-calibration.
        var calibration = engine.LastCalibration;
        await tv.ProbeStreamAsync(TimeSpan.FromSeconds(0.7));
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Same(calibration, engine.LastCalibration);
        Assert.Equal(EngineState.Playing, engine.State);
        Assert.InRange(MeasureOffsetMs(tv.AudibleAt, phone.AudibleAt, clock), -3, 3);
    }

    [Fact]
    public async Task Tv_reconnect_with_new_buffering_triggers_automatic_recalibration()
    {
        var clock = MonotonicClock.Instance;
        var log = NewLog();
        await using var tv = new FakeTv(clock, prebufferSeconds: 1.0, extraLatencySeconds: 0.05);
        tv.Start();
        var bt = new SimulatedPcmOutput(clock, Rate, bufferSeconds: 0.05, skewPpm: 25, extraLatencySeconds: 0.17);
        var room = new SimulatedRoom(clock);
        room.AddSource(tv.AudibleAt, 0.7f);
        room.AddSource(bt.AudibleAt, 0.5f);

        var options = new EngineOptions { DefaultNetworkLatencySeconds = 0.8, MaxNetworkLatencySeconds = 4, DriftCompensation = false };
        await using var engine = new MultiroomEngine(options, new NoiseTrackFactory(), room, log, clock, _ => IPAddress.Loopback);
        var renderer = await DlnaRenderer.FromLocationAsync(engine.HttpClient, tv.Location);
        engine.AddSink(new DlnaSink(renderer!));
        engine.AddSink(new LocalPlaybackSink("bt", "BT speaker", _ => bt));
        engine.Playlist.Add([NoiseTrackFactory.Track("r1", 120)]);

        await engine.PlayAsync();
        var first = engine.LastCalibration!;
        Assert.True(first.Success, first.Summary);
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 1.5));
        Assert.InRange(MeasureOffsetMs(tv.AudibleAt, bt.AudibleAt, clock), -3, 3);

        // The TV re-fetches the stream and now buffers 600 ms more.
        tv.PrebufferSeconds = 1.6;
        tv.ForceReconnect();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (ReferenceEquals(engine.LastCalibration, first) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        var second = engine.LastCalibration!;
        Assert.NotSame(first, second);
        Assert.True(second.Success, second.Summary);
        var tvLatency = second.Sinks.Single(r => r.SinkId == renderer!.Id).LatencySeconds!.Value;
        Assert.InRange(tvLatency, 1.6, 1.75);

        // Playback resumes by itself after the automatic calibration.
        var resumeDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (engine.State != EngineState.Playing && DateTime.UtcNow < resumeDeadline)
        {
            await Task.Delay(100);
        }

        Assert.Equal(EngineState.Playing, engine.State);
        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 1.5));
        Assert.InRange(MeasureOffsetMs(tv.AudibleAt, bt.AudibleAt, clock), -3, 3);
    }

    [Fact]
    public async Task Without_microphone_tv_latency_is_estimated_from_its_position_reports()
    {
        var clock = MonotonicClock.Instance;
        var log = NewLog();
        await using var tv = new FakeTv(clock, prebufferSeconds: 1.4, extraLatencySeconds: 0.0);
        tv.Start();
        var phone = new SimulatedPcmOutput(clock, Rate, bufferSeconds: 0.04, skewPpm: 0, extraLatencySeconds: 0.0);

        var options = new EngineOptions { DefaultNetworkLatencySeconds = 0.6, DriftCompensation = false, PositionPollInterval = TimeSpan.FromMilliseconds(150) };
        await using var engine = new MultiroomEngine(options, new NoiseTrackFactory(), microphone: null, log, clock, _ => IPAddress.Loopback);
        var renderer = await DlnaRenderer.FromLocationAsync(engine.HttpClient, tv.Location);
        var tvSink = new DlnaSink(renderer!);
        engine.AddSink(tvSink);
        engine.AddSink(new LocalPlaybackSink("speaker", "Phone speaker", _ => phone));
        engine.Playlist.Add([NoiseTrackFactory.Track("n1", 120)]);

        await engine.PlayAsync();
        Assert.Null(engine.LastCalibration);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (!tvSink.PositionEstimateApplied && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250);
        }

        Assert.True(tvSink.PositionEstimateApplied, "position-based estimate never became available");
        _output.WriteLine($"estimated TV latency {tvSink.LatencySeconds * 1000:0} ms (true 1400 ms)");
        Assert.InRange(tvSink.LatencySeconds, 1.3, 1.5);

        await Task.Delay(TimeSpan.FromSeconds(engine.TargetLatencySeconds + 2));
        var offset = MeasureOffsetMs(tv.AudibleAt, phone.AudibleAt, clock);
        _output.WriteLine($"offset without calibration: {offset:0.0} ms");
        Assert.InRange(offset, -100, 100);
    }

    private AuraLog NewLog()
    {
        var log = new AuraLog { MinimumLevel = LogLevel.Debug };
        log.Sink = e =>
        {
            try
            {
                _output.WriteLine(e.ToString());
            }
            catch (InvalidOperationException)
            {
                // test already finished
            }
        };
        return log;
    }

    /// <summary>How much later output B plays the same audio than output A, in ms.</summary>
    private static double MeasureOffsetMs(Func<long, float> a, Func<long, float> b, IClock clock)
    {
        var end = clock.NowNs - Ns.FromSeconds(0.1);
        var start = end - Ns.FromSeconds(3);
        var n = 3 * Rate;
        var sa = new float[n];
        var sb = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = start + (long)(i * (double)Ns.PerSecond / Rate);
            sa[i] = a(t);
            sb[i] = b(t);
        }

        // Find B's middle second inside A.
        var reference = sb.AsSpan(Rate, Rate).ToArray();
        var est = DelayEstimator.Estimate(sa, reference, Rate, 0, n - Rate, new DelayEstimatorOptions { LowHz = 100, HighHz = 10000 });
        Assert.True(est.Found && est.Confidence > 10, $"outputs not correlated (confidence {est.Confidence:0.0})");
        return (Rate - est.LagSamples) / Rate * 1000.0;
    }

    private static double Rms(Func<long, float> source, IClock clock, double seconds)
    {
        var end = clock.NowNs - Ns.FromSeconds(0.05);
        var n = (int)(seconds * Rate);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var v = source(end - Ns.FromSeconds(seconds) + (long)(i * (double)Ns.PerSecond / Rate));
            sum += v * v;
        }

        return Math.Sqrt(sum / n);
    }
}
