using System.Net;
using System.Xml.Linq;
using MusicAura.Core.Dlna;

namespace MusicAura.Core.Tests;

public class DlnaTests
{
    private const string SamsungDescription = """
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0" xmlns:sec="http://www.sec.co.kr/dlna" xmlns:dlna="urn:schemas-dlna-org:device-1-0">
          <specVersion><major>1</major><minor>0</minor></specVersion>
          <device>
            <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
            <pnpx:X_compatibleId xmlns:pnpx="http://schemas.microsoft.com/windows/pnpx/2005/11">MS_DigitalMediaDeviceClass_DMR_V001</pnpx:X_compatibleId>
            <dlna:X_DLNADOC>DMR-1.50</dlna:X_DLNADOC>
            <friendlyName>[TV] Samsung Q60 Series (55)</friendlyName>
            <manufacturer>Samsung Electronics</manufacturer>
            <modelName>QE55Q60TAU</modelName>
            <modelNumber>AllShare1.0</modelNumber>
            <UDN>uuid:0aa1b2c3-00d4-1000-a5b6-cc2b3c4d5e6f</UDN>
            <sec:deviceID>ABC</sec:deviceID>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:RenderingControl:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:RenderingControl</serviceId>
                <controlURL>/upnp/control/RenderingControl1</controlURL>
                <eventSubURL>/upnp/event/RenderingControl1</eventSubURL>
                <SCPDURL>RenderingControl_1.xml</SCPDURL>
              </service>
              <service>
                <serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId>
                <controlURL>/upnp/control/ConnectionManager1</controlURL>
                <eventSubURL>/upnp/event/ConnectionManager1</eventSubURL>
                <SCPDURL>ConnectionManager_1.xml</SCPDURL>
              </service>
              <service>
                <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <serviceId>urn:upnp-org:serviceId:AVTransport</serviceId>
                <controlURL>/upnp/control/AVTransport1</controlURL>
                <eventSubURL>/upnp/event/AVTransport1</eventSubURL>
                <SCPDURL>AVTransport_1.xml</SCPDURL>
              </service>
            </serviceList>
          </device>
        </root>
        """;

    [Fact]
    public void Parses_samsung_style_description()
    {
        var device = UpnpDeviceDescription.Parse(SamsungDescription, new Uri("http://192.168.1.50:9197/dmr"));
        Assert.Equal("[TV] Samsung Q60 Series (55)", device.FriendlyName);
        Assert.Equal("uuid:0aa1b2c3-00d4-1000-a5b6-cc2b3c4d5e6f", device.Udn);
        var avt = device.FindService(UpnpDeviceDescription.AvTransportType);
        Assert.NotNull(avt);
        Assert.Equal("http://192.168.1.50:9197/upnp/control/AVTransport1", avt.ControlUrl.ToString());
        Assert.Equal("http://192.168.1.50:9197/RenderingControl_1.xml", device.FindService(UpnpDeviceDescription.RenderingControlType)!.ScpdUrl!.ToString());
        Assert.Same(device, UpnpDeviceDescription.FindRenderer(device));
    }

    [Fact]
    public void Finds_renderer_embedded_in_another_device_and_honours_url_base()
    {
        var xml = """
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <URLBase>http://10.0.0.7:8080/</URLBase>
              <device>
                <deviceType>urn:schemas-upnp-org:device:Basic:1</deviceType>
                <friendlyName>Box</friendlyName><UDN>uuid:root</UDN>
                <deviceList>
                  <device>
                    <deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
                    <friendlyName>Box Renderer</friendlyName><UDN>uuid:child</UDN>
                    <serviceList><service>
                      <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                      <serviceId>avt</serviceId><controlURL>avt/ctl</controlURL>
                    </service></serviceList>
                  </device>
                </deviceList>
              </device>
            </root>
            """;
        var root = UpnpDeviceDescription.Parse(xml, new Uri("http://10.0.0.7:1900/desc.xml"));
        var renderer = UpnpDeviceDescription.FindRenderer(root);
        Assert.NotNull(renderer);
        Assert.Equal("uuid:child", renderer.Udn);
        Assert.Equal("http://10.0.0.7:8080/avt/ctl", renderer.Services[0].ControlUrl.ToString());
    }

    [Fact]
    public void Parses_ssdp_response()
    {
        var text = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nDATE: Thu, 01 Jan 1970 00:00:00 GMT\r\nEXT:\r\n" +
                   "LOCATION: http://192.168.1.50:9197/dmr\r\nSERVER: SHP, UPnP/1.0, Samsung UPnP SDK/1.0\r\n" +
                   "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n" +
                   "USN: uuid:0aa1b2c3::urn:schemas-upnp-org:device:MediaRenderer:1\r\nContent-Length: 0\r\n\r\n";
        var r = SsdpDiscovery.Parse(text, new IPEndPoint(IPAddress.Parse("192.168.1.50"), 1900));
        Assert.NotNull(r);
        Assert.Equal("http://192.168.1.50:9197/dmr", r.Location.ToString());
        Assert.Equal("uuid:0aa1b2c3", r.DeviceUuid);
        Assert.Null(SsdpDiscovery.Parse("M-SEARCH * HTTP/1.1\r\nHOST: x\r\n\r\n", new IPEndPoint(IPAddress.Loopback, 1)));
    }

    [Fact]
    public void Builds_search_request()
    {
        var req = SsdpDiscovery.BuildSearchRequest(SsdpDiscovery.MediaRendererType, 2);
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", req);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", req);
        Assert.EndsWith("\r\n\r\n", req);
    }

    [Fact]
    public void Soap_envelope_escapes_arguments_and_parses_responses()
    {
        var didl = "<DIDL-Lite><item>&</item></DIDL-Lite>";
        var envelope = SoapClient.BuildEnvelope("urn:schemas-upnp-org:service:AVTransport:1", "SetAVTransportURI",
            [KeyValuePair.Create("InstanceID", "0"), KeyValuePair.Create("CurrentURIMetaData", didl)]);
        var doc = XDocument.Parse(envelope);
        var meta = doc.Descendants().Single(e => e.Name.LocalName == "CurrentURIMetaData");
        Assert.Equal(didl, meta.Value);

        var response = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>
            <u:GetPositionInfoResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
            <Track>1</Track><RelTime>0:01:02</RelTime></u:GetPositionInfoResponse></s:Body></s:Envelope>
            """;
        var args = SoapClient.ParseResponse(response, "GetPositionInfo");
        Assert.Equal("0:01:02", args["RelTime"]);

        var fault = """
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault>
            <detail><UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>716</errorCode>
            <errorDescription>Resource not found</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>
            """;
        Assert.Equal((716, "Resource not found"), SoapClient.ParseFault(fault));
    }

    [Theory]
    [InlineData("0:01:02", 62.0)]
    [InlineData("00:00:05.500", 5.5)]
    [InlineData("1:00:00.1/4", 3600.25)]
    public void Parses_upnp_time(string text, double seconds)
    {
        Assert.True(UpnpTime.TryParse(text, out var t));
        Assert.Equal(seconds, t.TotalSeconds, 6);
    }

    [Fact]
    public void Rejects_unimplemented_time()
    {
        Assert.False(UpnpTime.TryParse("NOT_IMPLEMENTED", out _));
        Assert.Equal("1:02:03", UpnpTime.Format(TimeSpan.FromSeconds(3723)));
    }

    [Fact]
    public void Chooses_stream_format_from_sink_protocols()
    {
        var wav = ProtocolInfoEntry.ParseList("http-get:*:audio/mpeg:DLNA.ORG_PN=MP3,http-get:*:audio/x-wav:*,http-get:*:audio/L16;rate=44100;channels=2:DLNA.ORG_PN=LPCM");
        var f = StreamFormat.Choose(wav, 44100, 2, StreamFormatPreference.Auto);
        Assert.Equal(StreamEncoding.Wav, f.Encoding);
        Assert.Equal("audio/x-wav", f.MimeType);

        var l16Only = ProtocolInfoEntry.ParseList("http-get:*:audio/mpeg:*,http-get:*:audio/L16;rate=44100;channels=2:DLNA.ORG_PN=LPCM");
        var g = StreamFormat.Choose(l16Only, 44100, 2, StreamFormatPreference.Auto);
        Assert.Equal(StreamEncoding.L16, g.Encoding);
        Assert.Equal("audio/L16;rate=44100;channels=2", g.ContentType);
        Assert.StartsWith("DLNA.ORG_PN=LPCM;", g.ContentFeatures);
        Assert.True(g.BigEndian);

        var wrongRate = ProtocolInfoEntry.ParseList("http-get:*:audio/L16;rate=48000;channels=2:*");
        Assert.Equal(StreamEncoding.Wav, StreamFormat.Choose(wrongRate, 44100, 2, StreamFormatPreference.Auto).Encoding);
        Assert.Equal(StreamEncoding.Wav, StreamFormat.Choose([], 44100, 2, StreamFormatPreference.Auto).Encoding);
        Assert.Equal(StreamEncoding.L16, StreamFormat.Choose([], 44100, 2, StreamFormatPreference.L16).Encoding);
    }

    [Fact]
    public void Didl_lite_is_well_formed_and_carries_protocol_info()
    {
        var format = StreamFormat.Wav(44100);
        var didl = DidlLite.ForAudioStream("A & B", "Music Aura", new Uri("http://192.168.1.5:5000/aura/x.wav?a=1&b=2"), format);
        var doc = XDocument.Parse(didl);
        var res = doc.Descendants().Single(e => e.Name.LocalName == "res");
        Assert.Equal(format.ProtocolInfo, res.Attribute("protocolInfo")!.Value);
        Assert.Equal("http://192.168.1.5:5000/aura/x.wav?a=1&b=2", res.Value);
        Assert.Equal("A & B", doc.Descendants().Single(e => e.Name.LocalName == "title").Value);
    }
}
