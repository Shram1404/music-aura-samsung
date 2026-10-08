using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MusicAura.Core.Diagnostics;

namespace MusicAura.Core.Http;

public sealed class HttpRequestHead
{
    public HttpRequestHead(string method, string target, string version, IReadOnlyDictionary<string, string> headers, IPEndPoint? remote)
    {
        Method = method;
        Version = version;
        Headers = headers;
        Remote = remote;
        var q = target.IndexOf('?');
        Path = Uri.UnescapeDataString(q >= 0 ? target[..q] : target);
        Query = q >= 0 ? target[(q + 1)..] : "";
    }

    public string Method { get; }

    public string Path { get; }

    public string Query { get; }

    public string Version { get; }

    public IReadOnlyDictionary<string, string> Headers { get; }

    public IPEndPoint? Remote { get; }

    public bool IsHead => Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    public static HttpRequestHead? Parse(string head, IPEndPoint? remote)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new HttpRequestHead(requestLine[0], requestLine[1], requestLine[2], headers, remote);
    }
}

/// <summary>Handles one request. The handler writes the complete raw response; the connection is closed afterwards.</summary>
public delegate Task HttpHandler(HttpRequestHead request, NetworkStream stream, CancellationToken cancellationToken);

/// <summary>
/// Tiny HTTP/1.1 server on a raw <see cref="TcpListener"/>. DLNA renderers are picky about
/// response headers and long-lived streaming bodies, so full byte-level control is easier
/// than fighting a general-purpose server.
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable
{
    private const int MaxHeadBytes = 32 * 1024;

    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, HttpHandler> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly AuraLog? _log;
    private Task? _acceptLoop;

    public MiniHttpServer(IPAddress bindAddress, int port = 0, AuraLog? log = null)
    {
        _listener = new TcpListener(bindAddress, port);
        _log = log;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static string ServerHeader => "Linux/1 UPnP/1.0 DLNADOC/1.50 MusicAura/0.1";

    public void Start()
    {
        _listener.Start(16);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public void Map(string path, HttpHandler handler) => _routes[path] = handler;

    public void Unmap(string path) => _routes.TryRemove(path, out _);

    public static async Task WriteHeadAsync(Stream stream, int status, string reason, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        foreach (var (name, value) in headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task WriteSimpleAsync(Stream stream, int status, string reason, string contentType, byte[] body, bool headOnly, CancellationToken ct)
    {
        await WriteHeadAsync(stream, status, reason,
        [
            KeyValuePair.Create("Content-Type", contentType),
            KeyValuePair.Create("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            KeyValuePair.Create("Connection", "close"),
            KeyValuePair.Create("Server", ServerHeader),
        ], ct).ConfigureAwait(false);
        if (!headOnly)
        {
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // shutting down
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                _log?.Warn("http", $"accept failed: {ex.Message}");
                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var remote = client.Client.RemoteEndPoint as IPEndPoint;
            try
            {
                var stream = client.GetStream();
                string? headText;
                using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    headCts.CancelAfter(TimeSpan.FromSeconds(15));
                    headText = await ReadHeadAsync(stream, headCts.Token).ConfigureAwait(false);
                }

                if (headText is null)
                {
                    return;
                }

                var request = HttpRequestHead.Parse(headText, remote);
                if (request is null)
                {
                    await WriteSimpleAsync(stream, 400, "Bad Request", "text/plain", "bad request"u8.ToArray(), false, _cts.Token).ConfigureAwait(false);
                    return;
                }

                _log?.Debug("http", $"{remote} {request.Method} {request.Path} range={request.Header("Range") ?? "-"}");
                if (!_routes.TryGetValue(request.Path, out var handler))
                {
                    await WriteSimpleAsync(stream, 404, "Not Found", "text/plain", "not found"u8.ToArray(), request.IsHead, _cts.Token).ConfigureAwait(false);
                    return;
                }

                await handler(request, stream, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Client went away; normal for media renderers that probe and reconnect.
            }
            catch (Exception ex)
            {
                _log?.Error("http", $"handler failed for {remote}", ex);
            }
        }
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length, 1), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }

            length += n;
            if (length >= 4 &&
                buffer[length - 4] == '\r' && buffer[length - 3] == '\n' &&
                buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length - 4);
            }
        }

        return null;
    }
}
