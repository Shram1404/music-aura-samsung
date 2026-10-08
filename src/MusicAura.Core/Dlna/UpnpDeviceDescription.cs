using System.Xml.Linq;

namespace MusicAura.Core.Dlna;

public sealed record UpnpService(string ServiceType, string ServiceId, Uri ControlUrl, Uri? EventSubUrl, Uri? ScpdUrl);

public sealed record UpnpDevice(
    string DeviceType,
    string FriendlyName,
    string? Manufacturer,
    string? ModelName,
    string? ModelNumber,
    string Udn,
    IReadOnlyList<UpnpService> Services,
    IReadOnlyList<UpnpDevice> EmbeddedDevices)
{
    public UpnpService? FindService(string typePrefix) =>
        Services.FirstOrDefault(s => s.ServiceType.StartsWith(typePrefix, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<UpnpDevice> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in EmbeddedDevices)
        {
            foreach (var d in child.SelfAndDescendants())
            {
                yield return d;
            }
        }
    }
}

/// <summary>Parser for UPnP device description documents (the XML behind the SSDP LOCATION).</summary>
public static class UpnpDeviceDescription
{
    public const string AvTransportType = "urn:schemas-upnp-org:service:AVTransport:";
    public const string RenderingControlType = "urn:schemas-upnp-org:service:RenderingControl:";
    public const string ConnectionManagerType = "urn:schemas-upnp-org:service:ConnectionManager:";

    public static UpnpDevice Parse(string xml, Uri location)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("Empty device description.");

        var baseUri = location;
        var urlBase = Child(root, "URLBase")?.Value?.Trim();
        if (!string.IsNullOrEmpty(urlBase) && Uri.TryCreate(urlBase, UriKind.Absolute, out var parsedBase))
        {
            baseUri = parsedBase;
        }

        var device = Child(root, "device") ?? throw new FormatException("No <device> element in description.");
        return ParseDevice(device, baseUri);
    }

    /// <summary>Picks the (possibly embedded) device that offers an AVTransport service.</summary>
    public static UpnpDevice? FindRenderer(UpnpDevice root) =>
        root.SelfAndDescendants().FirstOrDefault(d => d.FindService(AvTransportType) is not null);

    private static UpnpDevice ParseDevice(XElement device, Uri baseUri)
    {
        var services = new List<UpnpService>();
        var serviceList = Child(device, "serviceList");
        if (serviceList is not null)
        {
            foreach (var s in serviceList.Elements().Where(e => e.Name.LocalName == "service"))
            {
                var type = Text(s, "serviceType");
                var control = Text(s, "controlURL");
                if (type is null || control is null)
                {
                    continue;
                }

                services.Add(new UpnpService(
                    type,
                    Text(s, "serviceId") ?? type,
                    Resolve(baseUri, control)!,
                    Resolve(baseUri, Text(s, "eventSubURL")),
                    Resolve(baseUri, Text(s, "SCPDURL"))));
            }
        }

        var embedded = new List<UpnpDevice>();
        var deviceList = Child(device, "deviceList");
        if (deviceList is not null)
        {
            foreach (var child in deviceList.Elements().Where(e => e.Name.LocalName == "device"))
            {
                embedded.Add(ParseDevice(child, baseUri));
            }
        }

        return new UpnpDevice(
            Text(device, "deviceType") ?? "",
            Text(device, "friendlyName") ?? "UPnP device",
            Text(device, "manufacturer"),
            Text(device, "modelName"),
            Text(device, "modelNumber"),
            Text(device, "UDN") ?? Guid.NewGuid().ToString(),
            services,
            embedded);
    }

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Text(XElement parent, string localName)
    {
        var value = Child(parent, localName)?.Value?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static Uri? Resolve(Uri baseUri, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        if (Uri.TryCreate(relative, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute;
        }

        // Some devices omit the leading slash; UPnP says such URLs are relative to the base.
        return new Uri(baseUri, relative);
    }
}
