using System.Buffers.Binary;

namespace MusicAura.Core.Audio;

public static class PcmConvert
{
    public static short FloatToInt16(float sample)
    {
        var scaled = sample * 32767f;
        if (scaled >= 32767f)
        {
            return short.MaxValue;
        }

        if (scaled <= -32768f)
        {
            return short.MinValue;
        }

        return (short)MathF.Round(scaled);
    }

    /// <summary>Writes samples as 16-bit PCM, little-endian (WAV) or big-endian (audio/L16).</summary>
    public static void FloatToInt16Bytes(ReadOnlySpan<float> samples, Span<byte> destination, bool bigEndian)
    {
        if (destination.Length < samples.Length * 2)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        for (var i = 0; i < samples.Length; i++)
        {
            var s = FloatToInt16(samples[i]);
            var slot = destination.Slice(i * 2, 2);
            if (bigEndian)
            {
                BinaryPrimitives.WriteInt16BigEndian(slot, s);
            }
            else
            {
                BinaryPrimitives.WriteInt16LittleEndian(slot, s);
            }
        }
    }

    public static void Int16ToFloat(ReadOnlySpan<short> samples, Span<float> destination)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            destination[i] = samples[i] / 32768f;
        }
    }

    public static void Int16BytesToFloat(ReadOnlySpan<byte> bytes, Span<float> destination, bool bigEndian = false)
    {
        var n = bytes.Length / 2;
        for (var i = 0; i < n; i++)
        {
            var slot = bytes.Slice(i * 2, 2);
            var s = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(slot) : BinaryPrimitives.ReadInt16LittleEndian(slot);
            destination[i] = s / 32768f;
        }
    }

    /// <summary>
    /// Converts interleaved audio with any channel count to interleaved stereo.
    /// Mono is duplicated, 5.1/7.1 get a standard ITU-style downmix, other layouts keep the first two channels.
    /// </summary>
    public static void ToStereo(ReadOnlySpan<float> source, int channels, Span<float> destination)
    {
        var frames = source.Length / channels;
        if (destination.Length < frames * 2)
        {
            throw new ArgumentException("Destination too small.", nameof(destination));
        }

        switch (channels)
        {
            case 1:
                for (var i = 0; i < frames; i++)
                {
                    destination[i * 2] = source[i];
                    destination[i * 2 + 1] = source[i];
                }

                break;
            case 2:
                source[..(frames * 2)].CopyTo(destination);
                break;
            case 6:
            case 8:
                // FL FR FC LFE SL SR [BL BR]
                const float c = 0.7071f;
                const float norm = 1f / (1f + c + c);
                for (var i = 0; i < frames; i++)
                {
                    var f = source.Slice(i * channels, channels);
                    var sl = f[4] + (channels == 8 ? f[6] : 0f);
                    var sr = f[5] + (channels == 8 ? f[7] : 0f);
                    destination[i * 2] = (f[0] + c * f[2] + c * sl) * norm;
                    destination[i * 2 + 1] = (f[1] + c * f[2] + c * sr) * norm;
                }

                break;
            default:
                for (var i = 0; i < frames; i++)
                {
                    destination[i * 2] = source[i * channels];
                    destination[i * 2 + 1] = source[i * channels + 1];
                }

                break;
        }
    }
}
