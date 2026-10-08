namespace MusicAura.Core.Dlna;

public enum StreamEncoding
{
    /// <summary>RIFF/WAVE header followed by 16-bit little-endian PCM.</summary>
    Wav,

    /// <summary>Raw 16-bit big-endian PCM (DLNA LPCM profile, MIME audio/L16).</summary>
    L16,
}

public enum StreamFormatPreference
{
    Auto,
    Wav,
    L16,
}

/// <summary>One entry of a ConnectionManager protocol info list (<c>protocol:network:contentFormat:additionalInfo</c>).</summary>
public sealed record ProtocolInfoEntry(string Protocol, string Network, string ContentFormat, string AdditionalInfo)
{
    public string MimeType
    {
        get
        {
            var semicolon = ContentFormat.IndexOf(';');
            return (semicolon >= 0 ? ContentFormat[..semicolon] : ContentFormat).Trim();
        }
    }

    public static ProtocolInfoEntry? Parse(string text)
    {
        var parts = text.Trim().Split(':', 4);
        return parts.Length == 4 ? new ProtocolInfoEntry(parts[0], parts[1], parts[2], parts[3]) : null;
    }

    public static IReadOnlyList<ProtocolInfoEntry> ParseList(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return [];
        }

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .Where(e => e is not null)
            .Cast<ProtocolInfoEntry>()
            .ToList();
    }

    public override string ToString() => $"{Protocol}:{Network}:{ContentFormat}:{AdditionalInfo}";
}

/// <summary>The live PCM stream format offered to a renderer.</summary>
public sealed record StreamFormat(StreamEncoding Encoding, string MimeType, int SampleRate, int Channels)
{
    /// <summary>
    /// DLNA flags for a live, non-seekable stream: streaming transfer mode, background
    /// transfer, connection stalling allowed, DLNA 1.5.
    /// </summary>
    public const string LiveStreamFlags = "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=01700000000000000000000000000000";

    public string ContentType => Encoding == StreamEncoding.L16
        ? $"{MimeType};rate={SampleRate};channels={Channels}"
        : MimeType;

    public string ContentFeatures => Encoding == StreamEncoding.L16
        ? "DLNA.ORG_PN=LPCM;" + LiveStreamFlags
        : LiveStreamFlags;

    public string ProtocolInfo => $"http-get:*:{ContentType}:{ContentFeatures}";

    public bool BigEndian => Encoding == StreamEncoding.L16;

    public string FileExtension => Encoding == StreamEncoding.L16 ? "pcm" : "wav";

    public int BytesPerSecond => SampleRate * Channels * 2;

    public static StreamFormat Wav(int sampleRate, int channels = 2, string mime = "audio/wav") =>
        new(StreamEncoding.Wav, mime, sampleRate, channels);

    public static StreamFormat L16(int sampleRate, int channels = 2) =>
        new(StreamEncoding.L16, "audio/L16", sampleRate, channels);

    /// <summary>
    /// Chooses a format the renderer advertises in its SinkProtocolInfo. WAV is preferred because
    /// its header removes any ambiguity about rate, channels and byte order; L16 is the
    /// DLNA-mandated fallback. If the list is empty or unhelpful, WAV is used.
    /// </summary>
    public static StreamFormat Choose(IReadOnlyList<ProtocolInfoEntry> sinkProtocols, int sampleRate, int channels, StreamFormatPreference preference)
    {
        var httpEntries = sinkProtocols
            .Where(e => e.Protocol.Equals("http-get", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? wavMime = null;
        foreach (var candidate in new[] { "audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave" })
        {
            if (httpEntries.Any(e => e.MimeType.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
            {
                wavMime = candidate;
                break;
            }
        }

        var supportsL16 = httpEntries.Any(e => e.MimeType.Equals("audio/L16", StringComparison.OrdinalIgnoreCase) && L16Matches(e, sampleRate, channels));

        return preference switch
        {
            StreamFormatPreference.Wav => Wav(sampleRate, channels, wavMime ?? "audio/wav"),
            StreamFormatPreference.L16 => L16(sampleRate, channels),
            _ when wavMime is not null => Wav(sampleRate, channels, wavMime),
            _ when supportsL16 => L16(sampleRate, channels),
            _ => Wav(sampleRate, channels),
        };
    }

    private static bool L16Matches(ProtocolInfoEntry entry, int sampleRate, int channels)
    {
        var parameters = entry.ContentFormat.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Skip(1);
        foreach (var p in parameters)
        {
            var eq = p.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = p[..eq].Trim();
            var value = p[(eq + 1)..].Trim();
            if (key.Equals("rate", StringComparison.OrdinalIgnoreCase) && value != sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return false;
            }

            if (key.Equals("channels", StringComparison.OrdinalIgnoreCase) && value != channels.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return false;
            }
        }

        return true;
    }
}
