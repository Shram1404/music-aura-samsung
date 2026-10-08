using System.Security;

namespace MusicAura.Core.Dlna;

/// <summary>Builds the DIDL-Lite metadata sent with SetAVTransportURI. Samsung TVs reject empty metadata.</summary>
public static class DidlLite
{
    public static string ForAudioStream(string title, string artist, Uri resource, StreamFormat format)
    {
        var e = (string s) => SecurityElement.Escape(s);
        return
            "<DIDL-Lite xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
            "xmlns:dlna=\"urn:schemas-dlna-org:metadata-1-0/\">" +
            "<item id=\"musicaura-live\" parentID=\"0\" restricted=\"1\">" +
            $"<dc:title>{e(title)}</dc:title>" +
            $"<dc:creator>{e(artist)}</dc:creator>" +
            $"<upnp:artist>{e(artist)}</upnp:artist>" +
            "<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
            $"<res protocolInfo=\"{e(format.ProtocolInfo)}\" " +
            $"sampleFrequency=\"{format.SampleRate}\" nrAudioChannels=\"{format.Channels}\" bitsPerSample=\"16\">" +
            $"{e(resource.ToString())}</res>" +
            "</item></DIDL-Lite>";
    }
}
