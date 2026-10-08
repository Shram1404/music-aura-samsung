namespace MusicAura.Core.Audio;

/// <summary>A playable item of the playlist.</summary>
public sealed record TrackInfo(string Id, string Title, string? Artist, string Uri, TimeSpan? Duration = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
}

/// <summary>
/// Decoded audio. Implementations always deliver interleaved <b>stereo</b> float samples
/// in [-1, 1] at <see cref="SampleRate"/>; the engine converts to the session rate.
/// </summary>
public interface IAudioSource : IDisposable
{
    int SampleRate { get; }

    TimeSpan? Duration { get; }

    /// <summary>
    /// Reads up to <c>destination.Length / 2</c> frames. Returns the number of frames read;
    /// 0 means end of stream.
    /// </summary>
    int Read(Span<float> destination);

    /// <summary>Repositions the decoder so the next <see cref="Read"/> starts at <paramref name="position"/>.</summary>
    void Seek(TimeSpan position);
}

public interface IAudioSourceFactory
{
    IAudioSource Open(TrackInfo track);
}

/// <summary>Endless silence, handy as a placeholder and in tests.</summary>
public sealed class SilenceSource : IAudioSource
{
    public SilenceSource(int sampleRate) => SampleRate = sampleRate;

    public int SampleRate { get; }

    public TimeSpan? Duration => null;

    public int Read(Span<float> destination)
    {
        destination.Clear();
        return destination.Length / 2;
    }

    public void Seek(TimeSpan position)
    {
    }

    public void Dispose()
    {
    }
}
