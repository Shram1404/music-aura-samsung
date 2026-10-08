using System.Globalization;
using System.Net;
using MusicAura.Core.Audio;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Dlna;
using MusicAura.Core.Engine;
using MusicAura.Core.Engine.Sinks;

// Desktop test tool for the TV side of Music Aura: find DLNA renderers and stream to them with
// exactly the code the Android app uses. Handy to check that a TV accepts the live stream
// before installing the app on the phone.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var log = new AuraLog { Sink = e => Console.Error.WriteLine(e.ToString()) };

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintUsage();
    return 0;
}

var options = ParseOptions(args.Skip(1).ToArray());
if (options.ContainsKey("verbose"))
{
    log.MinimumLevel = LogLevel.Debug;
}

using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(4) });
IPAddress? localIp = options.TryGetValue("local-ip", out var ipText) ? IPAddress.Parse(ipText) : null;

switch (args[0])
{
    case "discover":
    {
        var renderers = await Discover();
        if (renderers.Count == 0)
        {
            Console.WriteLine("No DLNA renderers found. Is the TV on and on the same network?");
            return 1;
        }

        for (var i = 0; i < renderers.Count; i++)
        {
            var r = renderers[i];
            Console.WriteLine($"[{i}] {r.FriendlyName}  ({r.Location.Host})  {r.Device.Manufacturer} {r.Device.ModelName}");
            try
            {
                var protocols = await r.GetSinkProtocolInfoAsync();
                var audio = protocols.Where(p => p.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.ContentFormat).Distinct().ToList();
                Console.WriteLine($"     audio formats: {string.Join(", ", audio)}");
                var chosen = StreamFormat.Choose(protocols, 44100, 2, StreamFormatPreference.Auto);
                Console.WriteLine($"     Music Aura would stream: {chosen.ContentType}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"     (GetProtocolInfo failed: {ex.Message})");
            }
        }

        return 0;
    }

    case "play":
    {
        var selector = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;
        var renderers = await Discover();
        var renderer = Select(renderers, selector);
        if (renderer is null)
        {
            Console.Error.WriteLine(renderers.Count == 0 ? "No renderers found." : $"No renderer matches '{selector}'. Run 'discover'.");
            return 1;
        }

        var engineOptions = new EngineOptions
        {
            StreamFormat = Enum.Parse<StreamFormatPreference>(options.GetValueOrDefault("format", "auto"), ignoreCase: true),
            HttpBodyMode = options.GetValueOrDefault("http", "close") switch
            {
                "chunked" => HttpBodyMode.Chunked,
                "length" => HttpBodyMode.HugeContentLength,
                _ => HttpBodyMode.CloseDelimited,
            },
            AutoCalibrate = false,
        };

        TrackInfo track;
        IAudioSourceFactory factory;
        if (options.TryGetValue("file", out var file))
        {
            using (var probe = WavFileSource.Open(file))
            {
                engineOptions.SampleRate = probe.SampleRate is 44100 or 48000 ? probe.SampleRate : 44100;
            }

            track = new TrackInfo(file, Path.GetFileNameWithoutExtension(file), null, file);
            factory = new WavFileFactory();
        }
        else
        {
            track = new TrackInfo("beeps", "Beep every second", null, "tone:");
            factory = new BeepFactory();
        }

        await using var engine = new MultiroomEngine(engineOptions, factory, null, log, null, localIp is null ? null : _ => localIp);
        engine.AddSink(new DlnaSink(renderer));
        engine.Playlist.Add([track]);
        Console.WriteLine($"Streaming '{track.Title}' to {renderer}. Press Ctrl+C to stop.");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (options.TryGetValue("seconds", out var secondsText))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(double.Parse(secondsText, CultureInfo.InvariantCulture)));
        }

        try
        {
            await engine.PlayAsync(ct: cts.Token);
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000, cts.Token);
                var s = engine.GetSnapshot();
                var tv = s.Sinks[0];
                Console.WriteLine(
                    $"{s.State,-8} {s.Position:mm\\:ss}  TV: {tv.Status} {tv.Message} | {tv.Detail} | latency model {tv.LatencySeconds * 1000:0} ms, drift {tv.DriftPpm:0} ppm");
            }
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("Stopping...");
        await engine.StopSessionAsync();
        return 0;
    }

    default:
        PrintUsage();
        return 1;
}

async Task<IReadOnlyList<DlnaRenderer>> Discover()
{
    if (options.TryGetValue("location", out var location))
    {
        // Skip SSDP (e.g. when multicast is blocked) and load the description directly.
        return await RendererDiscovery.LoadAsync(http, [new Uri(location)], log);
    }

    var timeout = TimeSpan.FromSeconds(double.Parse(options.GetValueOrDefault("timeout", "3"), CultureInfo.InvariantCulture));
    Console.Error.WriteLine($"Searching for DLNA renderers ({timeout.TotalSeconds:0} s)...");
    return await RendererDiscovery.FindAsync(http, timeout, localIp, log);
}

static DlnaRenderer? Select(IReadOnlyList<DlnaRenderer> renderers, string? selector)
{
    if (renderers.Count == 0)
    {
        return null;
    }

    if (selector is null)
    {
        return renderers.FirstOrDefault(r => r.LooksLikeSamsung) ?? renderers[0];
    }

    if (int.TryParse(selector, out var index) && index >= 0 && index < renderers.Count)
    {
        return renderers[index];
    }

    return renderers.FirstOrDefault(r =>
        r.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
        r.Location.Host == selector);
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var key = args[i][2..];
        var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        result[key] = value;
    }

    return result;
}

static void PrintUsage()
{
    Console.WriteLine("""
        musicaura — DLNA streaming test tool (the TV side of Music Aura)

          musicaura discover [--timeout 3] [--local-ip 192.168.1.20] [--location http://tv:port/desc.xml]
              List DLNA renderers (Samsung TVs show up as "[TV] Samsung ...") and their audio formats.

          musicaura play [index|name|ip] [--file song.wav] [--format auto|wav|l16]
                         [--http close|chunked|length] [--seconds N] [--verbose]
              Stream a WAV file (or a beep every second) to the renderer as a live PCM stream,
              exactly like the phone app does. Watch the TV for an "Allow access?" prompt.
        """);
}

internal sealed class WavFileFactory : IAudioSourceFactory
{
    public IAudioSource Open(TrackInfo track) => WavFileSource.Open(track.Uri);
}

/// <summary>A 1 kHz beep of 100 ms at the start of every second, for listening tests.</summary>
internal sealed class BeepFactory : IAudioSourceFactory
{
    public IAudioSource Open(TrackInfo track) => new BeepSource();

    private sealed class BeepSource : IAudioSource
    {
        private long _position;

        public int SampleRate => 44100;

        public TimeSpan? Duration => null;

        public int Read(Span<float> destination)
        {
            var frames = destination.Length / 2;
            for (var i = 0; i < frames; i++)
            {
                var n = _position + i;
                var inSecond = n % SampleRate;
                var v = inSecond < SampleRate / 10 ? (float)(0.4 * Math.Sin(2 * Math.PI * 1000 * n / SampleRate)) : 0f;
                destination[i * 2] = v;
                destination[i * 2 + 1] = v;
            }

            _position += frames;
            return frames;
        }

        public void Seek(TimeSpan position) => _position = (long)(position.TotalSeconds * SampleRate);

        public void Dispose()
        {
        }
    }
}
