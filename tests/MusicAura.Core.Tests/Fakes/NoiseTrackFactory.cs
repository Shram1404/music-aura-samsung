using MusicAura.Core.Audio;
using MusicAura.Core.Dsp;

namespace MusicAura.Core.Tests.Fakes;

/// <summary>Deterministic "music": softly low-passed noise with a slow amplitude envelope.</summary>
internal sealed class NoiseTrackFactory : IAudioSourceFactory
{
    public NoiseTrackFactory(int sampleRate = 44100, double seconds = 60)
    {
        SampleRate = sampleRate;
        Seconds = seconds;
    }

    public int SampleRate { get; }

    public double Seconds { get; }

    public int Opened { get; private set; }

    public static TrackInfo Track(string id, double seconds = 60) =>
        new(id, $"Noise {id}", "Test", $"noise://{id}", TimeSpan.FromSeconds(seconds));

    public IAudioSource Open(TrackInfo track)
    {
        Opened++;
        return new NoiseSource(SampleRate, Seconds, track.Id.GetHashCode(StringComparison.Ordinal));
    }

    internal sealed class NoiseSource : IAudioSource
    {
        private readonly long _length;
        private readonly int _seed;
        private XorShiftRandom _rng;
        private long _position;
        private double _lp;

        public NoiseSource(int sampleRate, double seconds, int seed)
        {
            SampleRate = sampleRate;
            _length = (long)(seconds * sampleRate);
            _seed = seed;
            _rng = new XorShiftRandom(seed);
        }

        public int SampleRate { get; }

        public TimeSpan? Duration => TimeSpan.FromSeconds(_length / (double)SampleRate);

        public long Position => _position;

        public int Read(Span<float> destination)
        {
            var frames = (int)Math.Min(destination.Length / 2, _length - _position);
            for (var i = 0; i < frames; i++)
            {
                _lp += 0.5 * (_rng.NextGaussian() - _lp);
                var env = 0.6 + 0.4 * Math.Sin((_position + i) * 2 * Math.PI / SampleRate * 0.7);
                var v = (float)(_lp * 0.2 * env);
                destination[i * 2] = v;
                destination[i * 2 + 1] = v;
            }

            _position += frames;
            return frames;
        }

        public void Seek(TimeSpan position)
        {
            // Regenerate deterministically up to the target.
            _rng = new XorShiftRandom(_seed);
            _lp = 0;
            _position = 0;
            var target = (long)(position.TotalSeconds * SampleRate);
            var scratch = new float[4096];
            while (_position < target)
            {
                Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, (target - _position) * 2)));
            }
        }

        public void Dispose()
        {
        }
    }
}
