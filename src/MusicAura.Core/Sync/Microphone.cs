namespace MusicAura.Core.Sync;

/// <summary>Platform microphone used for acoustic calibration.</summary>
public interface IMicrophone
{
    /// <summary>Starts capturing mono audio, preferably at <paramref name="sampleRate"/>.</summary>
    IMicrophoneCapture Start(int sampleRate);
}

public interface IMicrophoneCapture : IDisposable
{
    int SampleRate { get; }

    /// <summary>
    /// Copy of everything captured so far, and the monotonic time (ns) at which sample 0 was
    /// captured. A constant error in that time cancels out of the calibration (it shifts every
    /// device equally); only the sample clock has to be steady.
    /// </summary>
    float[] Snapshot(out long firstSampleNs);
}
