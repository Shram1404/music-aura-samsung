using System.Net;
using System.Net.Sockets;
using System.Text;
using MusicAura.Core.Diagnostics;

namespace MusicAura.Core.Dlna;

public sealed record SsdpResponse(Uri Location, string? Usn, string? SearchTarget, string? Server, IPEndPoint From)
{
    /// <summary>Device UUID taken from the USN (<c>uuid:xxxx::urn:...</c>).</summary>
    public string? DeviceUuid
    {
        get
        {
            if (Usn is null)
            {
                return null;
            }

            var end = Usn.IndexOf("::", StringComparison.Ordinal);
            return end >= 0 ? Usn[..end] : Usn;
        }
    }
}

/// <summary>SSDP M-SEARCH client (UPnP Device Architecture 1.1, section 1.3).</summary>
public sealed class SsdpDiscovery
{
    public const string MediaRendererType = "urn:schemas-upnp-org:device:MediaRenderer:1";

    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    private readonly AuraLog? _log;

    public SsdpDiscovery(AuraLog? log = null)
    {
        _log = log;
    }

    public static string BuildSearchRequest(string searchTarget, int mxSeconds) =>
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        $"MX: {mxSeconds}\r\n" +
        $"ST: {searchTarget}\r\n" +
        "USER-AGENT: Android/1 UPnP/1.1 MusicAura/0.1\r\n" +
        "\r\n";

    /// <summary>Parses a unicast M-SEARCH reply or a NOTIFY message. Returns null if it has no LOCATION.</summary>
    public static SsdpResponse? Parse(string message, IPEndPoint from)
    {
        var lines = message.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0)
        {
            return null;
        }

        var first = lines[0];
        if (!first.StartsWith("HTTP/1.", StringComparison.OrdinalIgnoreCase) &&
            !first.StartsWith("NOTIFY", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (!headers.TryGetValue("LOCATION", out var location) ||
            !Uri.TryCreate(location, UriKind.Absolute, out var locationUri))
        {
            return null;
        }

        headers.TryGetValue("USN", out var usn);
        headers.TryGetValue("SERVER", out var server);
        if (!headers.TryGetValue("ST", out var st))
        {
            headers.TryGetValue("NT", out st);
        }

        return new SsdpResponse(locationUri, usn, st, server, from);
    }

    /// <summary>
    /// Sends M-SEARCH a few times and collects distinct responders for <paramref name="timeout"/>.
    /// </summary>
    /// <param name="localAddress">Interface to search on (the phone's Wi-Fi address); null = any.</param>
    public async Task<IReadOnlyList<SsdpResponse>> SearchAsync(
        string searchTarget,
        TimeSpan timeout,
        IPAddress? localAddress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, SsdpResponse>(StringComparer.OrdinalIgnoreCase);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(localAddress ?? IPAddress.Any, 0));
        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
        if (localAddress is not null)
        {
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, localAddress.GetAddressBytes());
        }

        var mx = Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds) - 1, 1, 5);
        var payload = Encoding.ASCII.GetBytes(BuildSearchRequest(searchTarget, mx));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var sender = Task.Run(async () =>
        {
            // UDP is lossy: repeat the search a few times.
            for (var i = 0; i < 3 && !cts.IsCancellationRequested; i++)
            {
                try
                {
                    await udp.SendAsync(payload, payload.Length, MulticastEndpoint).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    _log?.Warn("ssdp", $"M-SEARCH send failed: {ex.Message}");
                }

                try
                {
                    await Task.Delay(400, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, CancellationToken.None);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    _log?.Warn("ssdp", $"receive failed: {ex.Message}");
                    break;
                }

                var text = Encoding.UTF8.GetString(received.Buffer);
                var response = Parse(text, received.RemoteEndPoint);
                if (response is null)
                {
                    continue;
                }

                var key = response.DeviceUuid ?? response.Location.ToString();
                if (results.TryAdd(key, response))
                {
                    _log?.Debug("ssdp", $"found {response.Location} ({response.Server})");
                }
            }
        }
        finally
        {
            await sender.ConfigureAwait(false);
        }

        return results.Values.ToList();
    }
}
