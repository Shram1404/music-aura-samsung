using System.Net;
using System.Text;
using MusicAura.Core.Audio;
using MusicAura.Core.Dsp;
using MusicAura.Core.Http;
using MusicAura.Core.Sync;

namespace MusicAura.Core.Tests;

public class HttpAndTrackerTests
{
    [Fact]
    public async Task Mini_http_server_routes_and_handles_head()
    {
        await using var server = new MiniHttpServer(IPAddress.Loopback);
        server.Map("/hello.txt", (req, stream, ct) =>
            MiniHttpServer.WriteSimpleAsync(stream, 200, "OK", "text/plain", "hi there"u8.ToArray(), req.IsHead, ct));
        server.Start();

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        var baseUri = $"http://127.0.0.1:{server.Port}";
        Assert.Equal("hi there", await http.GetStringAsync(baseUri + "/hello.txt"));

        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, baseUri + "/hello.txt"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(8, head.Content.Headers.ContentLength);

        using var missing = await http.GetAsync(baseUri + "/nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public void Request_head_parsing()
    {
        var head = HttpRequestHead.Parse("GET /aura/a%20b.wav?x=1 HTTP/1.1\r\nHost: h\r\ngetcontentFeatures.dlna.org: 1\r\nRange: bytes=0-", null)!;
        Assert.Equal("GET", head.Method);
        Assert.Equal("/aura/a b.wav", head.Path);
        Assert.Equal("x=1", head.Query);
        Assert.Equal("bytes=0-", head.Header("range"));
    }

    [Fact]
    public void Wav_header_round_trips_through_reader()
    {
        var header = WavFormat.CreateHeader(44100, 2, 16, 8);
        var data = new byte[8];
        BitConverter.GetBytes((short)16384).CopyTo(data, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(data, 2);
        using var ms = new MemoryStream([.. header, .. data]);
        using var wav = new WavFileSource(ms, ownsStream: false);
        Assert.Equal(44100, wav.SampleRate);
        var buffer = new float[8];
        Assert.Equal(2, wav.Read(buffer));
        Assert.Equal(0.5f, buffer[0], 4);
        Assert.Equal(-0.5f, buffer[1], 4);
        Assert.Equal(Encoding.ASCII.GetBytes("RIFF"), header[..4]);
    }

    [Fact]
    public void Endless_wav_header_uses_max_sizes()
    {
        var header = WavFormat.CreateHeader(48000, 2, 16, uint.MaxValue);
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(header, 4));
        Assert.Equal(uint.MaxValue, BitConverter.ToUInt32(header, 40));
        Assert.Equal(48000u * 4, BitConverter.ToUInt32(header, 28));
    }

    [Fact]
    public void Position_tracker_estimates_drift_and_latency_from_whole_seconds()
    {
        // Renderer starts playing 2.3 s after the connection and runs 80 ppm fast; we poll every
        // 250 ms with up to 60 ms of random request delay.
        var tracker = new PositionTracker();
        var rng = new XorShiftRandom(21);
        const double latency = 2.3;
        const double factor = 1 + 80e-6;
        for (var t = 3.0; t < 900; t += 0.2 + rng.NextDouble() * 0.1)
        {
            // The renderer samples its position somewhere inside the request round trip.
            var sampledAt = t + rng.NextDouble() * 0.06;
            var reported = Math.Floor((sampledAt - latency) * factor);
            tracker.Add(t + 0.03, reported);
        }

        var fit = tracker.Fit();
        Assert.NotNull(fit);
        Assert.InRange(fit.Value.DriftPpm, 60, 100);
        var expected = 600 - (600 - latency) * factor;
        var estimate = 600 - fit.Value.PositionAt(600);
        Assert.InRange(estimate, expected - 0.03, expected + 0.03);
    }

    [Fact]
    public void Position_tracker_resets_when_renderer_restarts()
    {
        var tracker = new PositionTracker();
        for (var i = 0; i < 40; i++)
        {
            tracker.Add(i * 0.5, Math.Floor(i * 0.5));
        }

        Assert.True(tracker.Count > 10);
        Assert.False(tracker.Add(20.5, 0));
        Assert.Equal(0, tracker.Count);
    }
}
