using Android.Content;
using Android.Media;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Sync;
using MusicAura.Core.Timing;
using MusicAura.Droid.Platform;
using Encoding = Android.Media.Encoding;

namespace MusicAura.Droid.Audio;

/// <summary>
/// Microphone for acoustic calibration. Prefers the UNPROCESSED source (no echo cancellation,
/// noise suppression or AGC that would distort or remove the calibration bursts).
/// </summary>
internal sealed class AndroidMicrophone : IMicrophone
{
    private readonly Context _context;
    private readonly AuraLog _log;

    public AndroidMicrophone(Context context, AuraLog log)
    {
        _context = context;
        _log = log;
    }

    public IMicrophoneCapture Start(int sampleRate)
    {
        if (_context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
        {
            throw new UnauthorizedAccessException("microphone permission not granted");
        }

        var audioManager = (AudioManager)_context.GetSystemService(Context.AudioService)!;
        var unprocessed = audioManager.GetProperty(AudioManager.PropertySupportAudioSourceUnprocessed) == "true";
        var source = unprocessed ? AudioSource.Unprocessed : AudioSource.VoiceRecognition;
        return new Capture(source, sampleRate, _log);
    }

    private sealed class Capture : IMicrophoneCapture
    {
        private readonly object _lock = new();
        private readonly AudioRecord _record;
        private readonly Thread _thread;
        private readonly AuraLog _log;
        private float[] _samples = new float[48000 * 20];
        private int _count;
        private long? _firstSampleNs;
        private long? _fallbackFirstSampleNs;
        private volatile bool _running = true;

        public Capture(AudioSource source, int sampleRate, AuraLog log)
        {
            _log = log;
            SampleRate = sampleRate;
            var format = new AudioFormat.Builder()
                .SetEncoding(Encoding.PcmFloat)!
                .SetSampleRate(sampleRate)!
                .SetChannelMask((ChannelOut)(int)ChannelIn.Mono)! // the binding types the mask as ChannelOut
                .Build()!;
            var minBytes = AudioRecord.GetMinBufferSize(sampleRate, ChannelIn.Mono, Encoding.PcmFloat);
            _record = new AudioRecord.Builder()
                .SetAudioSource(source)!
                .SetAudioFormat(format)!
                .SetBufferSizeInBytes(Math.Max(minBytes * 4, sampleRate * 4 / 2))!
                .Build()!;
            if (_record.State != State.Initialized)
            {
                _record.Release();
                throw new InvalidOperationException("AudioRecord could not be initialised");
            }

            _record.StartRecording();
            _thread = new Thread(Run) { IsBackground = true, Name = "aura-mic" };
            _thread.Start();
            log.Info("mic", $"recording from {source} at {sampleRate} Hz");
        }

        public int SampleRate { get; }

        public float[] Snapshot(out long firstSampleNs)
        {
            lock (_lock)
            {
                firstSampleNs = _firstSampleNs ?? _fallbackFirstSampleNs ?? MonotonicClock.Instance.NowNs;
                return _samples.AsSpan(0, _count).ToArray();
            }
        }

        public void Dispose()
        {
            _running = false;
            _thread.Join(1000);
            try
            {
                _record.Stop();
            }
            catch (Java.Lang.IllegalStateException)
            {
            }

            _record.Release();
            _record.Dispose();
        }

        private void Run()
        {
            Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio);
            var chunk = new float[SampleRate / 100];
            var timestamp = new AudioTimestamp();
            while (_running)
            {
                var n = _record.Read(chunk, 0, chunk.Length, 0 /* READ_BLOCKING */);
                var doneNs = MonotonicClock.Instance.NowNs;
                if (n <= 0)
                {
                    if (n < 0)
                    {
                        _log.Warn("mic", $"AudioRecord.read returned {n}");
                        Thread.Sleep(5);
                    }

                    continue;
                }

                lock (_lock)
                {
                    if (_count + n > _samples.Length)
                    {
                        Array.Resize(ref _samples, _samples.Length * 2);
                    }

                    Array.Copy(chunk, 0, _samples, _count, n);
                    _count += n;

                    // Fallback estimate: the newest sample was captured before the read returned, so
                    // every read bounds the start time from above; the tightest bound is the best guess.
                    var bound = doneNs - (long)(_count * (double)Ns.PerSecond / SampleRate);
                    _fallbackFirstSampleNs = _fallbackFirstSampleNs is { } f ? Math.Min(f, bound) : bound;

                    if (_firstSampleNs is null && _count > SampleRate / 10 &&
                        _record.GetTimestamp(timestamp, AudioTimebase.Monotonic) == 0 && timestamp.FramePosition > 0)
                    {
                        _firstSampleNs = JavaClock.ToEngineNs(timestamp.NanoTime) -
                                         (long)(timestamp.FramePosition * (double)Ns.PerSecond / SampleRate);
                    }
                }
            }
        }
    }
}
