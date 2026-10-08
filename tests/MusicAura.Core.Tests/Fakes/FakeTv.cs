using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;
using MusicAura.Core.Dlna;
using MusicAura.Core.Http;
using MusicAura.Core.Timing;

namespace MusicAura.Core.Tests.Fakes;

/// <summary>
/// A DLNA MediaRenderer emulator modelled on a TV: it fetches the stream URL it is given,
/// pre-buffers, then plays at its own (optionally skewed) clock. It records what it plays so
/// tests can "listen" to it, and answers the UPnP actions the engine uses.
/// </summary>
internal sealed class FakeTv : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly IClock _clock;
    private readonly MiniHttpServer _server;
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false });
    private readonly List<float> _received = [];
    private CancellationTokenSource? _playerCts;
    private Task? _player;
    private long _playStartNs = long.MaxValue;
    private int _sampleRate = 44100;
    private string _state = "NO_MEDIA_PRESENT";
    private string? _uri;

    public FakeTv(IClock clock, double prebufferSeconds, double extraLatencySeconds, double skewPpm = 0)
    {
        _clock = clock;
        PrebufferSeconds = prebufferSeconds;
        ExtraLatencySeconds = extraLatencySeconds;
        RateFactor = 1 + skewPpm * 1e-6;
        _server = new MiniHttpServer(IPAddress.Loopback, 0);
        _server.Map("/desc.xml", DescriptionAsync);
        _server.Map("/ctl/avt", ControlAsync);
        _server.Map("/ctl/rc", ControlAsync);
        _server.Map("/ctl/cm", ControlAsync);
    }

    /// <summary>Audio the TV collects before it starts playing (= its latency). Settable to simulate a re-buffer.</summary>
    public double PrebufferSeconds { get; set; }

    public double ExtraLatencySeconds { get; }

    public double RateFactor { get; }

    public bool Muted { get; private set; }

    public int Volume { get; private set; } = 25;

    public int StreamRequests { get; private set; }

    public List<string> Actions { get; } = [];

    public string SinkProtocolInfo { get; set; } =
        "http-get:*:audio/mpeg:DLNA.ORG_PN=MP3,http-get:*:audio/L16;rate=44100;channels=2:DLNA.ORG_PN=LPCM,http-get:*:audio/wav:*";

    public Uri Location => new($"http://127.0.0.1:{_server.Port}/desc.xml");

    public void Start() => _server.Start();

    /// <summary>The TV's acoustic output at time <paramref name="tNs"/>.</summary>
    public float AudibleAt(long tNs)
    {
        lock (_lock)
        {
            if (Muted || _playStartNs == long.MaxValue)
            {
                return 0;
            }

            var pos = (tNs - Ns.FromSeconds(ExtraLatencySeconds) - _playStartNs) / (double)Ns.PerSecond * _sampleRate * RateFactor;
            var i = (long)Math.Floor(pos);
            if (i < 0 || i + 1 >= _received.Count)
            {
                return 0;
            }

            var f = (float)(pos - i);
            return _received[(int)i] * (1 - f) + _received[(int)i + 1] * f;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _playerCts?.Cancel();
        if (_player is not null)
        {
            try
            {
                await _player;
            }
            catch
            {
            }
        }

        await _server.DisposeAsync();
        _http.Dispose();
    }

    private Task DescriptionAsync(HttpRequestHead request, NetworkStream stream, CancellationToken ct)
    {
        var xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0" xmlns:dlna="urn:schemas-dlna-org:device-1-0">
              <specVersion><major>1</major><minor>0</minor></specVersion>
              <device>
                <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                <friendlyName>[TV] Fake Samsung</friendlyName>
                <manufacturer>Samsung Electronics</manufacturer>
                <modelName>UE55FAKE</modelName>
                <UDN>uuid:fake-tv-0001</UDN>
                <serviceList>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId>
                    <controlURL>/ctl/rc</controlURL><eventSubURL>/evt/rc</eventSubURL><SCPDURL>/rc.xml</SCPDURL>
                  </service>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                    <controlURL>/ctl/cm</controlURL><eventSubURL>/evt/cm</eventSubURL><SCPDURL>/cm.xml</SCPDURL>
                  </service>
                  <service>
                    <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                    <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                    <controlURL>/ctl/avt</controlURL><eventSubURL>/evt/avt</eventSubURL><SCPDURL>/avt.xml</SCPDURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;
        return MiniHttpServer.WriteSimpleAsync(stream, 200, "OK", "text/xml", Encoding.UTF8.GetBytes(xml), request.IsHead, ct);
    }

    private async Task ControlAsync(HttpRequestHead request, NetworkStream stream, CancellationToken ct)
    {
        var length = int.Parse(request.Header("Content-Length") ?? "0", CultureInfo.InvariantCulture);
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        var soapAction = request.Header("SOAPAction")?.Trim('"') ?? "";
        var hash = soapAction.IndexOf('#');
        var serviceType = soapAction[..hash];
        var action = soapAction[(hash + 1)..];
        var args = XDocument.Parse(Encoding.UTF8.GetString(body)).Descendants()
            .Where(e => e.Parent?.Name.LocalName == action)
            .ToDictionary(e => e.Name.LocalName, e => e.Value);
        lock (_lock)
        {
            Actions.Add(action);
        }

        var outArgs = new List<(string, string)>();
        switch (action)
        {
            case "GetProtocolInfo":
                outArgs.Add(("Source", ""));
                outArgs.Add(("Sink", SinkProtocolInfo));
                break;
            case "SetAVTransportURI":
                _uri = args["CurrentURI"];
                if (!args["CurrentURIMetaData"].Contains("DIDL-Lite", StringComparison.Ordinal))
                {
                    await Fault(stream, 714, "Illegal MIME-type", ct);
                    return;
                }

                lock (_lock)
                {
                    _state = "STOPPED";
                }

                break;
            case "Play":
                StartPlayer();
                break;
            case "Stop":
                StopPlayer();
                break;
            case "Pause":
                break;
            case "GetTransportInfo":
                outArgs.Add(("CurrentTransportState", _state));
                outArgs.Add(("CurrentTransportStatus", "OK"));
                outArgs.Add(("CurrentSpeed", "1"));
                break;
            case "GetPositionInfo":
                long playStart;
                lock (_lock)
                {
                    playStart = _playStartNs;
                }

                var rel = playStart == long.MaxValue ? 0 : Math.Max(0, (_clock.NowNs - playStart) / (double)Ns.PerSecond * RateFactor);
                outArgs.Add(("Track", "1"));
                outArgs.Add(("TrackDuration", "0:00:00"));
                outArgs.Add(("RelTime", UpnpTime.Format(TimeSpan.FromSeconds(Math.Floor(rel)))));
                outArgs.Add(("AbsTime", "NOT_IMPLEMENTED"));
                break;
            case "GetVolume":
                outArgs.Add(("CurrentVolume", Volume.ToString(CultureInfo.InvariantCulture)));
                break;
            case "SetVolume":
                Volume = int.Parse(args["DesiredVolume"], CultureInfo.InvariantCulture);
                break;
            case "GetMute":
                outArgs.Add(("CurrentMute", Muted ? "1" : "0"));
                break;
            case "SetMute":
                Muted = args["DesiredMute"] == "1";
                break;
            default:
                await Fault(stream, 401, "Invalid Action", ct);
                return;
        }

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>");
        sb.Append($"<u:{action}Response xmlns:u=\"{serviceType}\">");
        foreach (var (k, v) in outArgs)
        {
            sb.Append($"<{k}>{SecurityElement.Escape(v)}</{k}>");
        }

        sb.Append($"</u:{action}Response></s:Body></s:Envelope>");
        await MiniHttpServer.WriteSimpleAsync(stream, 200, "OK", "text/xml; charset=\"utf-8\"", Encoding.UTF8.GetBytes(sb.ToString()), false, ct);
    }

    private static Task Fault(NetworkStream stream, int code, string description, CancellationToken ct)
    {
        var xml = $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>{code}</errorCode><errorDescription>{description}</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
        return MiniHttpServer.WriteSimpleAsync(stream, 500, "Internal Server Error", "text/xml", Encoding.UTF8.GetBytes(xml), false, ct);
    }

    /// <summary>Opens a short extra connection to the stream (like a media scanner probing the URL) and closes it.</summary>
    public async Task ProbeStreamAsync(TimeSpan duration)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await using var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until && await body.ReadAsync(buffer) > 0)
        {
        }
    }

    /// <summary>Simulates the TV dropping the stream and fetching it again (as some firmwares do).</summary>
    public void ForceReconnect() => StartPlayer();

    private void StartPlayer()
    {
        StopPlayer();
        var uri = _uri ?? throw new InvalidOperationException("No URI.");
        var cts = new CancellationTokenSource();
        _playerCts = cts;
        lock (_lock)
        {
            _state = "TRANSITIONING";
        }

        _player = Task.Run(() => PlayerAsync(new Uri(uri), cts.Token));
    }

    private void StopPlayer()
    {
        _playerCts?.Cancel();
        lock (_lock)
        {
            _playStartNs = long.MaxValue;
            _received.Clear();
            if (_state != "NO_MEDIA_PRESENT")
            {
                _state = "STOPPED";
            }
        }
    }

    private async Task PlayerAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("getcontentFeatures.dlna.org", "1");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            StreamRequests++;
            response.EnsureSuccessStatusCode();
            var bigEndian = response.Content.Headers.ContentType?.MediaType?.Equals("audio/L16", StringComparison.OrdinalIgnoreCase) == true;
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[8192];
            var pending = new List<byte>();
            var headerSkipped = bigEndian;
            while (!ct.IsCancellationRequested)
            {
                var n = await body.ReadAsync(buffer, ct);
                if (n == 0)
                {
                    break;
                }

                pending.AddRange(buffer.AsSpan(0, n).ToArray());
                if (!headerSkipped)
                {
                    if (pending.Count < 44)
                    {
                        continue;
                    }

                    _sampleRate = BinaryPrimitives.ReadInt32LittleEndian(pending.GetRange(24, 4).ToArray());
                    pending.RemoveRange(0, 44);
                    headerSkipped = true;
                }

                var frames = pending.Count / 4;
                lock (_lock)
                {
                    for (var i = 0; i < frames; i++)
                    {
                        var b = pending.GetRange(i * 4, 4).ToArray();
                        var l = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b);
                        var r = bigEndian ? BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(2)) : BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(2));
                        _received.Add((l + r) / 65536f);
                    }

                    if (_playStartNs == long.MaxValue && _received.Count >= PrebufferSeconds * _sampleRate)
                    {
                        // Start playing once the pre-buffer is full; the buffered audio sets the latency.
                        _playStartNs = _clock.NowNs;
                        _state = "PLAYING";
                    }
                }

                pending.RemoveRange(0, frames * 4);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
        }
    }
}
