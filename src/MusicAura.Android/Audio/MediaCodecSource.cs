using Android.Content;
using Android.Media;
using Java.Nio;
using MusicAura.Core.Audio;

namespace MusicAura.Droid.Audio;

/// <summary>
/// Decodes any audio file Android supports (MP3, AAC/M4A, FLAC, OGG/Opus, WAV...) with the
/// platform MediaExtractor + MediaCodec and delivers stereo float PCM. Seeking is
/// sample-accurate: the decoder starts at the previous sync point and drops the excess.
/// </summary>
internal sealed class MediaCodecSource : IAudioSource
{
    private const long TimeoutUs = 10_000;

    private readonly MediaExtractor _extractor = new();
    private readonly MediaCodec _codec;
    private readonly MediaCodec.BufferInfo _info = new();
    private byte[] _bytes = new byte[16384];
    private float[] _decoded = new float[16384];
    private float[] _pending = new float[16384];
    private int _pendingOffset;
    private int _pendingCount; // floats (stereo interleaved)
    private int _channels;
    private bool _float;
    private bool _inputDone;
    private bool _outputDone;
    private long _skipUntilUs = -1;

    public MediaCodecSource(Context context, Android.Net.Uri uri)
    {
        _extractor.SetDataSource(context, uri, null);
        var trackIndex = -1;
        MediaFormat? format = null;
        for (var i = 0; i < _extractor.TrackCount; i++)
        {
            var f = _extractor.GetTrackFormat(i);
            if (f.GetString(MediaFormat.KeyMime)?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
            {
                trackIndex = i;
                format = f;
                break;
            }
        }

        if (format is null)
        {
            _extractor.Release();
            throw new InvalidDataException("No audio track in this file.");
        }

        _extractor.SelectTrack(trackIndex);
        var mime = format.GetString(MediaFormat.KeyMime)!;
        if (format.ContainsKey(MediaFormat.KeyDuration))
        {
            Duration = TimeSpan.FromMilliseconds(format.GetLong(MediaFormat.KeyDuration) / 1000.0);
        }

        SampleRate = format.GetInteger(MediaFormat.KeySampleRate);
        _channels = format.GetInteger(MediaFormat.KeyChannelCount);
        _codec = MediaCodec.CreateDecoderByType(mime);
        _codec.Configure(format, null, null, MediaCodecConfigFlags.None);
        _codec.Start();

        // Decode until the real output format is known (e.g. HE-AAC doubles the rate).
        for (var i = 0; i < 200 && _pendingCount == 0 && !_outputDone; i++)
        {
            DecodeStep();
        }
    }

    public int SampleRate { get; private set; }

    public TimeSpan? Duration { get; }

    public int Read(Span<float> destination)
    {
        var wanted = destination.Length / 2 * 2;
        var written = 0;
        while (written < wanted)
        {
            if (_pendingCount > 0)
            {
                var n = Math.Min(_pendingCount, wanted - written);
                _pending.AsSpan(_pendingOffset, n).CopyTo(destination[written..]);
                _pendingOffset += n;
                _pendingCount -= n;
                written += n;
                continue;
            }

            if (_outputDone)
            {
                break;
            }

            DecodeStep();
        }

        return written / 2;
    }

    public void Seek(TimeSpan position)
    {
        var us = (long)(position.TotalMilliseconds * 1000);
        _extractor.SeekTo(us, MediaExtractorSeekTo.PreviousSync);
        _codec.Flush();
        _inputDone = false;
        _outputDone = false;
        _pendingCount = 0;
        _pendingOffset = 0;
        _skipUntilUs = us;
    }

    public void Dispose()
    {
        try
        {
            _codec.Stop();
        }
        catch (Java.Lang.IllegalStateException)
        {
        }

        _codec.Release();
        _extractor.Release();
        _codec.Dispose();
        _extractor.Dispose();
    }

    private void DecodeStep()
    {
        if (!_inputDone)
        {
            var inIndex = _codec.DequeueInputBuffer(TimeoutUs);
            if (inIndex >= 0)
            {
                var buffer = _codec.GetInputBuffer(inIndex)!;
                var size = _extractor.ReadSampleData(buffer, 0);
                if (size < 0)
                {
                    _codec.QueueInputBuffer(inIndex, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                    _inputDone = true;
                }
                else
                {
                    _codec.QueueInputBuffer(inIndex, 0, size, _extractor.SampleTime, MediaCodecBufferFlags.None);
                    _extractor.Advance();
                }
            }
        }

        var outIndex = _codec.DequeueOutputBuffer(_info, TimeoutUs);
        if (outIndex == (int)MediaCodecInfoState.OutputFormatChanged)
        {
            var format = _codec.OutputFormat;
            SampleRate = format.GetInteger(MediaFormat.KeySampleRate);
            _channels = format.GetInteger(MediaFormat.KeyChannelCount);
            _float = format.ContainsKey(MediaFormat.KeyPcmEncoding) &&
                     format.GetInteger(MediaFormat.KeyPcmEncoding) == (int)Android.Media.Encoding.PcmFloat;
            return;
        }

        if (outIndex < 0)
        {
            return;
        }

        try
        {
            if (_info.Size > 0)
            {
                var output = _codec.GetOutputBuffer(outIndex)!;
                Consume(output, _info.Offset, _info.Size, _info.PresentationTimeUs);
            }
        }
        finally
        {
            _codec.ReleaseOutputBuffer(outIndex, false);
        }

        if ((_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0)
        {
            _outputDone = true;
        }
    }

    private void Consume(ByteBuffer buffer, int offset, int size, long ptsUs)
    {
        if (_bytes.Length < size)
        {
            _bytes = new byte[size];
        }

        buffer.Position(offset);
        buffer.Limit(offset + size);
        buffer.Get(_bytes, 0, size);

        var bytesPerSample = _float ? 4 : 2;
        var samples = size / bytesPerSample;
        if (_decoded.Length < samples)
        {
            _decoded = new float[samples];
        }

        var src = _bytes.AsSpan(0, samples * bytesPerSample);
        if (_float)
        {
            for (var i = 0; i < samples; i++)
            {
                _decoded[i] = BitConverter.ToSingle(src.Slice(i * 4, 4));
            }
        }
        else
        {
            PcmConvert.Int16BytesToFloat(src, _decoded);
        }

        var channels = Math.Max(1, _channels);
        var frames = samples / channels;
        var skipFrames = 0;
        if (_skipUntilUs >= 0)
        {
            var endUs = ptsUs + frames * 1_000_000L / SampleRate;
            if (endUs <= _skipUntilUs)
            {
                return; // entirely before the seek target
            }

            skipFrames = (int)Math.Clamp((_skipUntilUs - ptsUs) * SampleRate / 1_000_000L, 0, frames);
            _skipUntilUs = -1;
        }

        var keep = frames - skipFrames;
        if (keep <= 0)
        {
            return;
        }

        // Compact pending data, then append the new frames as stereo.
        if (_pendingOffset > 0)
        {
            Array.Copy(_pending, _pendingOffset, _pending, 0, _pendingCount);
            _pendingOffset = 0;
        }

        if (_pending.Length < _pendingCount + keep * 2)
        {
            Array.Resize(ref _pending, (_pendingCount + keep * 2) * 2);
        }

        PcmConvert.ToStereo(
            _decoded.AsSpan(skipFrames * channels, keep * channels),
            channels,
            _pending.AsSpan(_pendingCount, keep * 2));
        _pendingCount += keep * 2;
    }
}

internal sealed class AndroidSourceFactory : IAudioSourceFactory
{
    private readonly Context _context;

    public AndroidSourceFactory(Context context)
    {
        _context = context;
    }

    public IAudioSource Open(TrackInfo track) => new MediaCodecSource(_context, Android.Net.Uri.Parse(track.Uri)!);
}
