using Android.Media;
using MusicAura.Core.Engine.Sinks;
using MusicAura.Droid.Platform;
using Encoding = Android.Media.Encoding;

namespace MusicAura.Droid.Audio;

/// <summary>
/// <see cref="IPcmOutput"/> on an Android AudioTrack pinned to one output device (phone
/// speaker, Bluetooth A2DP, wired...). Presentation timestamps come from
/// AudioTrack.getTimestamp(), which is what lets the engine place every frame precisely.
/// </summary>
internal sealed class AudioTrackOutput : IPcmOutput
{
    private readonly AudioTrack _track;
    private readonly AudioDeviceInfo? _device;
    private readonly AudioTimestamp _timestamp = new();
    private float[] _scratch = new float[2048];
    private long _lastTimestampPollNs;
    private long _cachedPosition;
    private long _cachedTimeNs;
    private bool _cachedValid;

    public AudioTrackOutput(AudioDeviceInfo? device, int sampleRate)
    {
        _device = device;
        SampleRate = sampleRate;

        var attributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!
            .Build()!;
        var format = new AudioFormat.Builder()
            .SetEncoding(Encoding.PcmFloat)!
            .SetSampleRate(sampleRate)!
            .SetChannelMask(ChannelOut.Stereo)!
            .Build()!;
        var minBytes = AudioTrack.GetMinBufferSize(sampleRate, ChannelOut.Stereo, Encoding.PcmFloat);
        var bytes = Math.Max(minBytes * 2, sampleRate * 8 * 60 / 1000); // at least 60 ms

        _track = new AudioTrack.Builder()
            .SetAudioAttributes(attributes)!
            .SetAudioFormat(format)!
            .SetBufferSizeInBytes(bytes)!
            .SetTransferMode(AudioTrackMode.Stream)!
            .Build()!;
        if (device is not null && !_track.SetPreferredDevice(device))
        {
            RoutingProblem = "Android refused to route to this device";
        }

        BufferFrames = _track.BufferSizeInFrames;
    }

    public int SampleRate { get; }

    public int BufferFrames { get; }

    public long FramesWritten { get; private set; }

    public string? RoutingProblem { get; private set; }

    public void Start()
    {
        _track.Play();
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        if (_scratch.Length < interleaved.Length)
        {
            _scratch = new float[interleaved.Length];
        }

        interleaved.CopyTo(_scratch);
        var offset = 0;
        while (offset < interleaved.Length)
        {
            var written = _track.Write(_scratch, offset, interleaved.Length - offset, WriteMode.Blocking);
            if (written < 0)
            {
                throw new IOException($"AudioTrack.write failed with {written}");
            }

            if (written == 0)
            {
                Thread.Sleep(2);
            }

            offset += written;
        }

        FramesWritten += interleaved.Length / 2;
        CheckRouting();
    }

    public bool TryGetTimestamp(out long framePosition, out long timeNs)
    {
        // getTimestamp() is cheap but there is no point calling it 200 times a second.
        var now = Java.Lang.JavaSystem.NanoTime();
        if (now - _lastTimestampPollNs > 20_000_000)
        {
            _lastTimestampPollNs = now;
            if (_track.GetTimestamp(_timestamp))
            {
                _cachedPosition = _timestamp.FramePosition;
                _cachedTimeNs = JavaClock.ToEngineNs(_timestamp.NanoTime);
                _cachedValid = _cachedPosition > 0;
            }
        }

        framePosition = _cachedPosition;
        timeNs = _cachedTimeNs;
        return _cachedValid;
    }

    public void Stop()
    {
        try
        {
            _track.Pause();
            _track.Flush();
            _track.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }
    }

    public void Dispose()
    {
        _track.Release();
        _track.Dispose();
    }

    private void CheckRouting()
    {
        if (_device is null || FramesWritten % (SampleRate / 2) > 512)
        {
            return;
        }

        var routed = _track.RoutedDevice;
        if (routed is not null && routed.Id != _device.Id)
        {
            RoutingProblem = $"Android plays this through {routed.ProductName} instead";
        }
        else if (routed is not null && RoutingProblem is not null && RoutingProblem.StartsWith("Android plays", StringComparison.Ordinal))
        {
            RoutingProblem = null;
        }
    }
}
