using MusicAura.Core.Dlna;

namespace MusicAura.Core.Engine;

public enum PauseMode
{
    /// <summary>Local outputs stop at once; network renderers are muted at once and unmuted when their buffer has drained.</summary>
    Instant,

    /// <summary>Every output stops on the same musical frame, i.e. after the network latency.</summary>
    Synchronized,
}

public enum HttpBodyMode
{
    /// <summary>No Content-Length; the body ends when the connection closes (HTTP/1.0 style).</summary>
    CloseDelimited,

    /// <summary>Chunked transfer encoding.</summary>
    Chunked,

    /// <summary>Advertise a very large Content-Length matching the WAV header.</summary>
    HugeContentLength,
}

public sealed class EngineOptions
{
    /// <summary>Session sample rate. Everything (outputs, TV stream) runs at this rate.</summary>
    public int SampleRate { get; set; } = 44100;

    /// <summary>How far ahead of real time the decoder fills the timeline.</summary>
    public double ProducerLeadSeconds { get; set; } = 1.0;

    /// <summary>Timeline history kept in memory; must exceed the largest output latency.</summary>
    public double TimelineSeconds { get; set; } = 30;

    /// <summary>Latency assumed for a network renderer before it has been measured.</summary>
    public double DefaultNetworkLatencySeconds { get; set; } = 2.0;

    /// <summary>Upper bound for the acoustic search window of a network renderer.</summary>
    public double MaxNetworkLatencySeconds { get; set; } = 8.0;

    /// <summary>Extra margin added to the minimum latency of local outputs.</summary>
    public double LocalSafetyMarginSeconds { get; set; } = 0.08;

    /// <summary>Gap between the "now" of a command and the first frame of new content.</summary>
    public double CommandLeadSeconds { get; set; } = 0.15;

    public PauseMode PauseMode { get; set; } = PauseMode.Instant;

    /// <summary>Calibrate with the microphone automatically whenever a session starts or a TV reconnects.</summary>
    public bool AutoCalibrate { get; set; } = true;

    /// <summary>Track TV clock drift via GetPositionInfo polling and follow it.</summary>
    public bool DriftCompensation { get; set; } = true;

    public TimeSpan PositionPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    public StreamFormatPreference StreamFormat { get; set; } = StreamFormatPreference.Auto;

    public HttpBodyMode HttpBodyMode { get; set; } = HttpBodyMode.CloseDelimited;

    /// <summary>Title shown on the TV while streaming.</summary>
    public string StreamTitle { get; set; } = "Music Aura";

    /// <summary>Peak level of calibration bursts (0..1).</summary>
    public double ProbeLevel { get; set; } = 0.35;

    public double ProbeSeconds { get; set; } = 0.4;

    public int ProbeRepetitions { get; set; } = 2;

    /// <summary>Lets the host raise the priority of audio threads (e.g. Android URGENT_AUDIO).</summary>
    public Action<string>? ConfigureAudioThread { get; set; }

    public int TimelineCapacityFrames => (int)(TimelineSeconds * SampleRate);
}
