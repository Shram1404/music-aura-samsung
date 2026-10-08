using System.Net;
using System.Net.Sockets;
using MusicAura.Core.Diagnostics;

namespace MusicAura.Core.Dlna;

public static class RendererDiscovery
{
    /// <summary>Finds DLNA media renderers on the local network.</summary>
    public static async Task<IReadOnlyList<DlnaRenderer>> FindAsync(
        HttpClient http,
        TimeSpan timeout,
        IPAddress? localAddress = null,
        AuraLog? log = null,
        CancellationToken cancellationToken = default)
    {
        var ssdp = new SsdpDiscovery(log);
        var responses = await ssdp.SearchAsync(SsdpDiscovery.MediaRendererType, timeout, localAddress, cancellationToken).ConfigureAwait(false);
        var locations = responses.Select(r => r.Location).Distinct().ToList();
        return await LoadAsync(http, locations, log, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads renderers from known description URLs (e.g. remembered from an earlier session).</summary>
    public static async Task<IReadOnlyList<DlnaRenderer>> LoadAsync(
        HttpClient http,
        IEnumerable<Uri> locations,
        AuraLog? log = null,
        CancellationToken cancellationToken = default)
    {
        var tasks = locations.Distinct().Select(async location =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                return await DlnaRenderer.FromLocationAsync(http, location, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                log?.Debug("dlna", $"skipping {location}: {ex.Message}");
                return null;
            }
        });

        var renderers = await Task.WhenAll(tasks).ConfigureAwait(false);
        return renderers
            .Where(r => r is not null)
            .Cast<DlnaRenderer>()
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .OrderBy(r => r.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the local IPv4 address the OS would use to reach <paramref name="remote"/>.
    /// Connecting a UDP socket sends nothing; it only resolves the route.
    /// </summary>
    public static IPAddress? LocalAddressFor(IPAddress remote)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(remote, 1900));
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
