using System.Net;
using Android.Content;
using Android.Net;
using Android.Net.Wifi;

namespace MusicAura.Droid.Platform;

/// <summary>Wi-Fi address lookup and the locks that keep Wi-Fi responsive while streaming.</summary>
internal sealed class NetworkHelper
{
    private readonly Context _context;
    private WifiManager.MulticastLock? _multicastLock;
    private WifiManager.WifiLock? _wifiLock;

    public NetworkHelper(Context context)
    {
        _context = context.ApplicationContext!;
    }

    /// <summary>IPv4 address of the phone on Wi-Fi (or Ethernet / hotspot), if any.</summary>
    public IPAddress? LocalIPv4()
    {
        var cm = (ConnectivityManager)_context.GetSystemService(Context.ConnectivityService)!;
#pragma warning disable CA1422 // still the simplest way to see every network's addresses
        var networks = cm.GetAllNetworks();
#pragma warning restore CA1422
        IPAddress? fallback = null;
        foreach (var network in networks)
        {
            var caps = cm.GetNetworkCapabilities(network);
            var properties = cm.GetLinkProperties(network);
            if (caps is null || properties is null)
            {
                continue;
            }

            foreach (var link in properties.LinkAddresses)
            {
                if (link.Address is Java.Net.Inet4Address v4 && !v4.IsLoopbackAddress &&
                    IPAddress.TryParse(v4.HostAddress, out var ip))
                {
                    if (caps.HasTransport(TransportType.Wifi) || caps.HasTransport(TransportType.Ethernet))
                    {
                        return ip;
                    }

                    fallback ??= ip;
                }
            }
        }

        return fallback;
    }

    /// <summary>Lets the phone receive SSDP multicast replies (Android filters them otherwise).</summary>
    public void AcquireMulticast()
    {
        var wifi = (WifiManager)_context.GetSystemService(Context.WifiService)!;
        _multicastLock ??= wifi.CreateMulticastLock("musicaura-ssdp")!;
        _multicastLock.SetReferenceCounted(false);
        _multicastLock.Acquire();
    }

    public void ReleaseMulticast()
    {
        if (_multicastLock?.IsHeld == true)
        {
            _multicastLock.Release();
        }
    }

    /// <summary>Keeps Wi-Fi out of power-save while the TV streams (avoids dropouts).</summary>
    public void AcquireWifiLock()
    {
        var wifi = (WifiManager)_context.GetSystemService(Context.WifiService)!;
        if (_wifiLock is null)
        {
            var mode = OperatingSystem.IsAndroidVersionAtLeast(29) ? WifiMode.FullLowLatency : WifiMode.FullHighPerf;
            _wifiLock = wifi.CreateWifiLock(mode, "musicaura-stream");
            _wifiLock!.SetReferenceCounted(false);
        }

        _wifiLock.Acquire();
    }

    public void ReleaseWifiLock()
    {
        if (_wifiLock?.IsHeld == true)
        {
            _wifiLock.Release();
        }
    }
}
