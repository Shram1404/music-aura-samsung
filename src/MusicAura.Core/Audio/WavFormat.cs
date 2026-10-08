using System.Buffers.Binary;
using System.Text;

namespace MusicAura.Core.Audio;

public static class WavFormat
{
    public const int HeaderSize = 44;

    /// <summary>
    /// Builds a canonical 44-byte PCM WAV header. For live streams pass
    /// <see cref="uint.MaxValue"/> as <paramref name="dataBytes"/>; renderers treat
    /// that as "unknown / endless".
    /// </summary>
    public static byte[] CreateHeader(int sampleRate, int channels, int bitsPerSample, uint dataBytes)
    {
        var header = new byte[HeaderSize];
        var span = header.AsSpan();
        var blockAlign = (ushort)(channels * bitsPerSample / 8);
        var byteRate = (uint)(sampleRate * blockAlign);
        var riffSize = dataBytes >= uint.MaxValue - 36 ? uint.MaxValue : dataBytes + 36;

        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], riffSize);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(span[8..]);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], byteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], blockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], (ushort)bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], dataBytes);
        return header;
    }
}

/// <summary>
/// Minimal RIFF/WAVE reader (PCM 8/16/24/32-bit and IEEE float) used by the desktop CLI
/// and by tests. On Android the platform decoders are used instead.
/// </summary>
public sealed class WavFileSource : IAudioSource
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly int _channels;
    private readonly int _bitsPerSample;
    private readonly bool _isFloat;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private readonly int _blockAlign;
    private long _dataPosition;
    private byte[] _byteBuffer = [];
    private float[] _sampleBuffer = [];

    public WavFileSource(Stream stream, bool ownsStream = true)
    {
        _stream = stream;
        _ownsStream = ownsStream;

        Span<byte> four = stackalloc byte[4];
        ReadExactly(four);
        if (Encoding.ASCII.GetString(four) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        ReadExactly(four); // riff size
        ReadExactly(four);
        if (Encoding.ASCII.GetString(four) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        var haveFormat = false;
        while (true)
        {
            ReadExactly(four);
            var id = Encoding.ASCII.GetString(four);
            ReadExactly(four);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(four);
            if (id == "fmt ")
            {
                var fmt = new byte[size];
                ReadExactly(fmt);
                var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                _channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                SampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                _blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(12));
                _bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (formatTag == 0xFFFE && size >= 26)
                {
                    // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID starts with the real tag.
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                }

                _isFloat = formatTag == 3;
                if (formatTag != 1 && formatTag != 3)
                {
                    throw new InvalidDataException($"Unsupported WAV encoding {formatTag}.");
                }

                if (_channels <= 0 || SampleRate <= 0 || _blockAlign <= 0)
                {
                    throw new InvalidDataException("Corrupt WAV format chunk.");
                }

                haveFormat = true;
                if ((size & 1) == 1)
                {
                    _stream.ReadByte();
                }
            }
            else if (id == "data")
            {
                if (!haveFormat)
                {
                    throw new InvalidDataException("WAV data chunk before fmt chunk.");
                }

                _dataStart = _stream.CanSeek ? _stream.Position : 0;
                _dataLength = size == uint.MaxValue || (_stream.CanSeek && _dataStart + size > _stream.Length)
                    ? (_stream.CanSeek ? _stream.Length - _dataStart : long.MaxValue)
                    : size;
                break;
            }
            else
            {
                Skip(size + (size & 1));
            }
        }
    }

    public int SampleRate { get; }

    public int Channels => _channels;

    public TimeSpan? Duration => _dataLength == long.MaxValue
        ? null
        : TimeSpan.FromSeconds(_dataLength / (double)_blockAlign / SampleRate);

    public static WavFileSource Open(string path) => new(File.OpenRead(path));

    public int Read(Span<float> destination)
    {
        var frames = destination.Length / 2;
        var remainingFrames = (_dataLength - _dataPosition) / _blockAlign;
        frames = (int)Math.Min(frames, remainingFrames);
        if (frames <= 0)
        {
            return 0;
        }

        var bytes = frames * _blockAlign;
        if (_byteBuffer.Length < bytes)
        {
            _byteBuffer = new byte[bytes];
        }

        var read = 0;
        while (read < bytes)
        {
            var n = _stream.Read(_byteBuffer, read, bytes - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        frames = read / _blockAlign;
        _dataPosition += frames * _blockAlign;
        var samples = frames * _channels;
        if (_sampleBuffer.Length < samples)
        {
            _sampleBuffer = new float[samples];
        }

        Decode(_byteBuffer.AsSpan(0, frames * _blockAlign), _sampleBuffer.AsSpan(0, samples));
        PcmConvert.ToStereo(_sampleBuffer.AsSpan(0, samples), _channels, destination);
        return frames;
    }

    public void Seek(TimeSpan position)
    {
        if (!_stream.CanSeek)
        {
            throw new NotSupportedException("Stream is not seekable.");
        }

        var frame = (long)(position.TotalSeconds * SampleRate);
        var offset = Math.Clamp(frame * _blockAlign, 0, _dataLength);
        _stream.Position = _dataStart + offset;
        _dataPosition = offset;
    }

    public void Dispose()
    {
        if (_ownsStream)
        {
            _stream.Dispose();
        }
    }

    private void Decode(ReadOnlySpan<byte> bytes, Span<float> samples)
    {
        var bytesPerSample = _bitsPerSample / 8;
        for (var i = 0; i < samples.Length; i++)
        {
            var b = bytes.Slice(i * bytesPerSample, bytesPerSample);
            samples[i] = (_isFloat, _bitsPerSample) switch
            {
                (true, 32) => BinaryPrimitives.ReadSingleLittleEndian(b),
                (true, 64) => (float)BinaryPrimitives.ReadDoubleLittleEndian(b),
                (false, 8) => (b[0] - 128) / 128f,
                (false, 16) => BinaryPrimitives.ReadInt16LittleEndian(b) / 32768f,
                (false, 24) => ((b[0] | (b[1] << 8) | (b[2] << 16)) << 8 >> 8) / 8388608f,
                (false, 32) => BinaryPrimitives.ReadInt32LittleEndian(b) / 2147483648f,
                _ => throw new InvalidDataException($"Unsupported sample size {_bitsPerSample}."),
            };
        }
    }

    private void ReadExactly(Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = _stream.Read(buffer[read..]);
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            read += n;
        }
    }

    private void Skip(long count)
    {
        if (_stream.CanSeek)
        {
            _stream.Seek(count, SeekOrigin.Current);
            return;
        }

        var tmp = new byte[4096];
        while (count > 0)
        {
            var n = _stream.Read(tmp, 0, (int)Math.Min(tmp.Length, count));
            if (n == 0)
            {
                throw new EndOfStreamException();
            }

            count -= n;
        }
    }
}
