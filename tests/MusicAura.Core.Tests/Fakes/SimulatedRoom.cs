using MusicAura.Core.Dsp;
using MusicAura.Core.Sync;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Tests.Fakes;

/// <summary>
/// A room with a microphone: mixes what the simulated outputs play (plus noise) in real time.
/// The capture deliberately reports its start time with a constant error, like a real phone
/// whose input latency is unknown — calibration must be immune to that.
/// </summary>
internal sealed class SimulatedRoom : IMicrophone
{
    private readonly IClock _clock;
    private readonly List<(Func<long, float> Source, float Gain)> _sources = [];

    public SimulatedRoom(IClock clock, double noiseLevel = 0.003, double reportedStartErrorSeconds = 0.0137)
    {
        _clock = clock;
        NoiseLevel = noiseLevel;
        ReportedStartErrorSeconds = reportedStartErrorSeconds;
    }

    public double NoiseLevel { get; }

    public double ReportedStartErrorSeconds { get; }

    public int Captures { get; private set; }

    public void AddSource(Func<long, float> audibleAt, float gain) => _sources.Add((audibleAt, gain));

    public IMicrophoneCapture Start(int sampleRate)
    {
        Captures++;
        return new Capture(this, sampleRate);
    }

    private sealed class Capture : IMicrophoneCapture
    {
        private readonly SimulatedRoom _room;
        private readonly object _lock = new();
        private readonly List<float> _samples = [];
        private readonly long _startNs;
        private readonly Thread _thread;
        private readonly XorShiftRandom _rng = new(1234);
        private volatile bool _running = true;

        public Capture(SimulatedRoom room, int sampleRate)
        {
            _room = room;
            SampleRate = sampleRate;
            _startNs = room._clock.NowNs;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
        }

        public int SampleRate { get; }

        public float[] Snapshot(out long firstSampleNs)
        {
            firstSampleNs = _startNs + Ns.FromSeconds(_room.ReportedStartErrorSeconds);
            lock (_lock)
            {
                return _samples.ToArray();
            }
        }

        public void Dispose()
        {
            _running = false;
            _thread.Join();
        }

        private void Run()
        {
            long index = 0;
            while (_running)
            {
                // Generate everything up to 30 ms ago (the outputs' histories are complete there).
                var until = _room._clock.NowNs - Ns.FromSeconds(0.03);
                var batch = new List<float>();
                while (true)
                {
                    var t = _startNs + (long)(index * (double)Ns.PerSecond / SampleRate);
                    if (t > until)
                    {
                        break;
                    }

                    double v = _rng.NextGaussian() * _room.NoiseLevel;
                    foreach (var (source, gain) in _room._sources)
                    {
                        v += gain * source(t);
                    }

                    batch.Add((float)v);
                    index++;
                }

                lock (_lock)
                {
                    _samples.AddRange(batch);
                }

                Thread.Sleep(10);
            }
        }
    }
}
