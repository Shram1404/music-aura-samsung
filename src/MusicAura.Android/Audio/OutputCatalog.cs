using Android.Content;
using Android.Media;
using MusicAura.Core.Engine.Sinks;

namespace MusicAura.Droid.Audio;

/// <summary>A phone audio output that can take part in a session.</summary>
internal sealed record LocalOutput(string Id, string Name, AudioDeviceType Type, AudioDeviceInfo Device, bool DefaultEnabled, double DefaultExtraLatency);

/// <summary>Lists the phone's audio outputs and reports when Bluetooth/wired devices come and go.</summary>
internal sealed class OutputCatalog : AudioDeviceCallback
{
    private readonly Context _context;
    private readonly AudioManager _audioManager;

    public OutputCatalog(Context context)
    {
        _context = context;
        _audioManager = (AudioManager)context.GetSystemService(Context.AudioService)!;
    }

    public event Action? Changed;

    public void StartWatching(Android.OS.Handler handler) => _audioManager.RegisterAudioDeviceCallback(this, handler);

    public void StopWatching() => _audioManager.UnregisterAudioDeviceCallback(this);

    public IReadOnlyList<LocalOutput> List()
    {
        var result = new List<LocalOutput>();
        var devices = _audioManager.GetDevices(GetDevicesTargets.Outputs) ?? [];
        foreach (var d in devices)
        {
            if (!d.IsSink || Describe(d.Type) is not { } info)
            {
                continue;
            }

            var product = d.ProductName?.ToString();
            var name = d.Type == AudioDeviceType.BuiltinSpeaker || string.IsNullOrWhiteSpace(product) || product == Android.OS.Build.Model
                ? info.Label
                : $"{product} ({info.Label})";
            var id = $"local:{d.Type}:{product}";
            if (result.Any(r => r.Id == id))
            {
                continue; // same device exposed twice (e.g. several speaker endpoints)
            }

            result.Add(new LocalOutput(id, name, d.Type, d, info.DefaultEnabled, info.ExtraLatency));
        }

        return result;
    }

    public LocalPlaybackSink CreateSink(LocalOutput output) =>
        new(output.Id, output.Name, rate => new AudioTrackOutput(output.Device, rate), output.DefaultExtraLatency)
        {
            DeviceKind = Describe(output.Type)?.Label,
        };

    public override void OnAudioDevicesAdded(AudioDeviceInfo[]? addedDevices) => Changed?.Invoke();

    public override void OnAudioDevicesRemoved(AudioDeviceInfo[]? removedDevices) => Changed?.Invoke();

#pragma warning disable CA1416 // comparing against newer device types is harmless on older Android
    private static (string Label, bool DefaultEnabled, double ExtraLatency)? Describe(AudioDeviceType type) => type switch
    {
        AudioDeviceType.BuiltinSpeaker => ("Phone speaker", false, 0.01),
        AudioDeviceType.BluetoothA2dp => ("Bluetooth", true, 0.10),
        AudioDeviceType.BleHeadset => ("Bluetooth LE", true, 0.08),
        AudioDeviceType.BleSpeaker => ("Bluetooth LE", true, 0.08),
        AudioDeviceType.WiredHeadphones => ("Headphones", true, 0.0),
        AudioDeviceType.WiredHeadset => ("Headset", true, 0.0),
        AudioDeviceType.UsbHeadset => ("USB headset", true, 0.01),
        AudioDeviceType.UsbDevice => ("USB audio", true, 0.01),
        AudioDeviceType.LineAnalog => ("Line out", true, 0.0),
        AudioDeviceType.HearingAid => ("Hearing aid", true, 0.05),
        _ => null,
    };
#pragma warning restore CA1416
}
