using MusicAura.Core.Audio;
using MusicAura.Core.Engine.Sinks;

namespace MusicAura.Core.Engine;

public enum EngineState
{
    /// <summary>No session: nothing is connected.</summary>
    Idle,

    /// <summary>Connecting outputs (TV fetching the stream, Bluetooth starting).</summary>
    Starting,

    /// <summary>Measuring output latencies with the microphone.</summary>
    Calibrating,

    /// <summary>Session running, nothing playing.</summary>
    Ready,

    Playing,

    Paused,
}

public sealed record SinkSnapshot(
    string Id,
    string Name,
    SinkKind Kind,
    bool Enabled,
    bool Running,
    SinkStatus Status,
    string? Message,
    double LatencySeconds,
    bool Calibrated,
    DateTime? CalibratedAtUtc,
    double OffsetSeconds,
    double Volume,
    double SyncErrorMs,
    double RateCorrectionPpm,
    double DriftPpm,
    string? Detail);

public sealed record EngineSnapshot(
    EngineState State,
    bool SessionActive,
    TrackInfo? Track,
    int TrackIndex,
    TimeSpan Position,
    TimeSpan? Duration,
    bool Buffering,
    double TargetLatencySeconds,
    IReadOnlyList<SinkSnapshot> Sinks,
    CalibrationReport? LastCalibration,
    string? Message);
