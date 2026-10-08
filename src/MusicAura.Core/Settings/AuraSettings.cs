using System.Text.Json;
using System.Text.Json.Serialization;
using MusicAura.Core.Dlna;
using MusicAura.Core.Engine;

namespace MusicAura.Core.Settings;

/// <summary>Everything the app remembers between launches.</summary>
public sealed class AuraSettings
{
    public int SampleRate { get; set; } = 44100;

    public bool AutoCalibrate { get; set; } = true;

    public bool DriftCompensation { get; set; } = true;

    public PauseMode PauseMode { get; set; } = PauseMode.Instant;

    public StreamFormatPreference StreamFormat { get; set; } = StreamFormatPreference.Auto;

    public HttpBodyMode HttpBodyMode { get; set; } = HttpBodyMode.CloseDelimited;

    public bool RepeatAll { get; set; }

    public Dictionary<string, OutputSettings> Outputs { get; set; } = [];

    /// <summary>Description URLs of renderers seen before, for a quick reconnect at start-up.</summary>
    public List<string> RendererLocations { get; set; } = [];

    public List<PlaylistEntry> Playlist { get; set; } = [];

    public void ApplyTo(EngineOptions options)
    {
        options.SampleRate = SampleRate;
        options.AutoCalibrate = AutoCalibrate;
        options.DriftCompensation = DriftCompensation;
        options.PauseMode = PauseMode;
        options.StreamFormat = StreamFormat;
        options.HttpBodyMode = HttpBodyMode;
    }

    public OutputSettings For(string id)
    {
        if (!Outputs.TryGetValue(id, out var s))
        {
            s = new OutputSettings();
            Outputs[id] = s;
        }

        return s;
    }

    public string ToJson() => JsonSerializer.Serialize(this, AuraJsonContext.Default.AuraSettings);

    public static AuraSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AuraSettings();
        }

        try
        {
            return JsonSerializer.Deserialize(json, AuraJsonContext.Default.AuraSettings) ?? new AuraSettings();
        }
        catch (JsonException)
        {
            return new AuraSettings();
        }
    }
}

public sealed class OutputSettings
{
    public bool? Enabled { get; set; }

    public double OffsetMs { get; set; }

    public double Volume { get; set; } = 1.0;

    /// <summary>Last measured latency (local: extra acoustic latency; network: total), in ms.</summary>
    public double? LatencyMs { get; set; }

    public DateTime? CalibratedUtc { get; set; }

    public string? Name { get; set; }
}

public sealed class PlaylistEntry
{
    public string Id { get; set; } = "";

    public string Uri { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Artist { get; set; }

    public long? DurationMs { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = false, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuraSettings))]
internal sealed partial class AuraJsonContext : JsonSerializerContext
{
}
