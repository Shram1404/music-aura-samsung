using System.Globalization;
using System.Net;

namespace MusicAura.Core.Dlna;

public sealed record TransportInfo(string State, string? Status, string? Speed);

public sealed record PositionInfo(TimeSpan? RelTime, TimeSpan? TrackDuration, string? TrackUri, string RawRelTime);

/// <summary>
/// A UPnP AV MediaRenderer (Samsung TVs expose one as part of their built-in DLNA support),
/// controlled through its AVTransport, RenderingControl and ConnectionManager services.
/// </summary>
public sealed class DlnaRenderer
{
    private const string Instance = "0";

    private readonly SoapClient _soap;

    public DlnaRenderer(UpnpDevice device, Uri location, SoapClient soap)
    {
        Device = device;
        Location = location;
        _soap = soap;
        AvTransport = device.FindService(UpnpDeviceDescription.AvTransportType)
            ?? throw new ArgumentException("Device has no AVTransport service.", nameof(device));
        RenderingControl = device.FindService(UpnpDeviceDescription.RenderingControlType);
        ConnectionManager = device.FindService(UpnpDeviceDescription.ConnectionManagerType);
    }

    public UpnpDevice Device { get; }

    public Uri Location { get; }

    public UpnpService AvTransport { get; }

    public UpnpService? RenderingControl { get; }

    public UpnpService? ConnectionManager { get; }

    public string Id => Device.Udn;

    public string FriendlyName => Device.FriendlyName;

    public IPAddress? Address => IPAddress.TryParse(Location.Host, out var ip) ? ip : null;

    public bool LooksLikeSamsung =>
        (Device.Manufacturer?.Contains("Samsung", StringComparison.OrdinalIgnoreCase) ?? false) ||
        Device.FriendlyName.Contains("Samsung", StringComparison.OrdinalIgnoreCase);

    public static async Task<DlnaRenderer?> FromLocationAsync(HttpClient http, Uri location, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, location);
        request.Headers.TryAddWithoutValidation("User-Agent", SoapClient.UserAgent);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var root = UpnpDeviceDescription.Parse(xml, location);
        var renderer = UpnpDeviceDescription.FindRenderer(root);
        return renderer is null ? null : new DlnaRenderer(renderer, location, new SoapClient(http));
    }

    public async Task<IReadOnlyList<ProtocolInfoEntry>> GetSinkProtocolInfoAsync(CancellationToken ct = default)
    {
        if (ConnectionManager is null)
        {
            return [];
        }

        var result = await _soap.InvokeAsync(ConnectionManager.ControlUrl, ConnectionManager.ServiceType, "GetProtocolInfo", [], ct).ConfigureAwait(false);
        return ProtocolInfoEntry.ParseList(result.GetValueOrDefault("Sink"));
    }

    public Task SetAvTransportUriAsync(Uri uri, string metadata, CancellationToken ct = default) =>
        InvokeAvt("SetAVTransportURI", ct,
            ("InstanceID", Instance), ("CurrentURI", uri.ToString()), ("CurrentURIMetaData", metadata));

    public Task PlayAsync(CancellationToken ct = default) =>
        InvokeAvt("Play", ct, ("InstanceID", Instance), ("Speed", "1"));

    public Task PauseAsync(CancellationToken ct = default) =>
        InvokeAvt("Pause", ct, ("InstanceID", Instance));

    public Task StopAsync(CancellationToken ct = default) =>
        InvokeAvt("Stop", ct, ("InstanceID", Instance));

    public async Task<TransportInfo> GetTransportInfoAsync(CancellationToken ct = default)
    {
        var r = await InvokeAvt("GetTransportInfo", ct, ("InstanceID", Instance)).ConfigureAwait(false);
        return new TransportInfo(
            r.GetValueOrDefault("CurrentTransportState") ?? "UNKNOWN",
            r.GetValueOrDefault("CurrentTransportStatus"),
            r.GetValueOrDefault("CurrentSpeed"));
    }

    public async Task<PositionInfo> GetPositionInfoAsync(CancellationToken ct = default)
    {
        var r = await InvokeAvt("GetPositionInfo", ct, ("InstanceID", Instance)).ConfigureAwait(false);
        var raw = r.GetValueOrDefault("RelTime") ?? "";
        TimeSpan? rel = UpnpTime.TryParse(raw, out var t) ? t : null;
        TimeSpan? duration = UpnpTime.TryParse(r.GetValueOrDefault("TrackDuration"), out var d) ? d : null;
        return new PositionInfo(rel, duration, r.GetValueOrDefault("TrackURI"), raw);
    }

    public async Task<int?> GetVolumeAsync(CancellationToken ct = default)
    {
        if (RenderingControl is null)
        {
            return null;
        }

        var r = await InvokeRc("GetVolume", ct, ("InstanceID", Instance), ("Channel", "Master")).ConfigureAwait(false);
        return int.TryParse(r.GetValueOrDefault("CurrentVolume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public async Task SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        if (RenderingControl is null)
        {
            return;
        }

        await InvokeRc("SetVolume", ct, ("InstanceID", Instance), ("Channel", "Master"),
            ("DesiredVolume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
    }

    public async Task<bool?> GetMuteAsync(CancellationToken ct = default)
    {
        if (RenderingControl is null)
        {
            return null;
        }

        var r = await InvokeRc("GetMute", ct, ("InstanceID", Instance), ("Channel", "Master")).ConfigureAwait(false);
        var v = r.GetValueOrDefault("CurrentMute");
        return v is null ? null : v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetMuteAsync(bool mute, CancellationToken ct = default)
    {
        if (RenderingControl is null)
        {
            return;
        }

        await InvokeRc("SetMute", ct, ("InstanceID", Instance), ("Channel", "Master"), ("DesiredMute", mute ? "1" : "0")).ConfigureAwait(false);
    }

    public override string ToString() => $"{FriendlyName} ({Location.Host})";

    private Task<IReadOnlyDictionary<string, string>> InvokeAvt(string action, CancellationToken ct, params (string, string)[] args) =>
        _soap.InvokeAsync(AvTransport.ControlUrl, AvTransport.ServiceType, action, args.Select(a => KeyValuePair.Create(a.Item1, a.Item2)), ct);

    private Task<IReadOnlyDictionary<string, string>> InvokeRc(string action, CancellationToken ct, params (string, string)[] args) =>
        _soap.InvokeAsync(RenderingControl!.ControlUrl, RenderingControl.ServiceType, action, args.Select(a => KeyValuePair.Create(a.Item1, a.Item2)), ct);
}
