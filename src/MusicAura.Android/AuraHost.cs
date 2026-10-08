using Android.Content;
using Android.OS;
using MusicAura.Core.Audio;
using MusicAura.Core.Diagnostics;
using MusicAura.Core.Dlna;
using MusicAura.Core.Engine;
using MusicAura.Core.Engine.Sinks;
using MusicAura.Core.Settings;
using MusicAura.Droid.Audio;
using MusicAura.Droid.Platform;

namespace MusicAura.Droid;

/// <summary>
/// Process-wide owner of the engine and its platform glue (outputs, discovery, settings).
/// Lives as long as the app process so playback survives the activity being recreated.
/// </summary>
internal sealed class AuraHost
{
    private const string PrefsName = "musicaura";
    private const string SettingsKey = "settings";

    private static readonly object InstanceLock = new();
    private static AuraHost? _instance;

    private readonly object _saveLock = new();
    private readonly ISharedPreferences _prefs;
    private int _discovering;

    private AuraHost(Context context)
    {
        Context = context.ApplicationContext!;
        Log = new AuraLog(800) { Sink = e => Android.Util.Log.Info("MusicAura", e.ToString()) };
        _prefs = Context.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;
        Settings = AuraSettings.FromJson(_prefs.GetString(SettingsKey, null));
        Network = new NetworkHelper(Context);

        var options = new EngineOptions
        {
            ConfigureAudioThread = _ => Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio),
        };
        Settings.ApplyTo(options);
        Engine = new MultiroomEngine(options, new AndroidSourceFactory(Context), new AndroidMicrophone(Context, Log), Log);
        Engine.Playlist.RepeatAll = Settings.RepeatAll;
        Engine.Playlist.Add(Settings.Playlist.Select(p => new TrackInfo(
            p.Id, p.Title, p.Artist, p.Uri, p.DurationMs is { } ms ? TimeSpan.FromMilliseconds(ms) : null)));
        Engine.CalibrationCompleted += _ => Save();
        Engine.Playlist.Changed += Save;

        Outputs = new OutputCatalog(Context);
        Outputs.Changed += SyncLocalOutputs;
        Outputs.StartWatching(new Handler(Looper.MainLooper!));
        SyncLocalOutputs();

        _ = RestoreRenderersAsync();
    }

    public Context Context { get; }

    public AuraLog Log { get; }

    public AuraSettings Settings { get; }

    public MultiroomEngine Engine { get; }

    public OutputCatalog Outputs { get; }

    public NetworkHelper Network { get; }

    public bool Discovering => Volatile.Read(ref _discovering) != 0;

    public static AuraHost Get(Context context)
    {
        lock (InstanceLock)
        {
            return _instance ??= new AuraHost(context);
        }
    }

    /// <summary>Finds DLNA renderers (Samsung TVs) on the Wi-Fi and adds them as outputs.</summary>
    public async Task DiscoverRenderersAsync()
    {
        if (Interlocked.Exchange(ref _discovering, 1) == 1)
        {
            return;
        }

        try
        {
            Network.AcquireMulticast();
            var local = Network.LocalIPv4();
            Log.Info("host", $"searching for TVs on {local?.ToString() ?? "any interface"}");
            var renderers = await RendererDiscovery.FindAsync(Engine.HttpClient, TimeSpan.FromSeconds(3.5), local, Log).ConfigureAwait(false);
            AddRenderers(renderers);
            Log.Info("host", renderers.Count == 0 ? "no DLNA renderers found" : $"found {renderers.Count} renderer(s)");
        }
        catch (Exception ex)
        {
            Log.Error("host", "discovery failed", ex);
        }
        finally
        {
            Network.ReleaseMulticast();
            Volatile.Write(ref _discovering, 0);
        }
    }

    public void Save()
    {
        lock (_saveLock)
        {
            foreach (var sink in Engine.Sinks)
            {
                var s = Settings.For(sink.Id);
                s.Name = sink.Name;
                s.Enabled = sink.Enabled;
                s.OffsetMs = Math.Round(sink.UserOffsetSeconds * 1000);
                if (sink.Kind == SinkKind.Local)
                {
                    s.Volume = sink.Volume;
                }

                if (sink.IsCalibrated)
                {
                    s.LatencyMs = Math.Round(sink.CurrentLatencyAt(Core.Timing.MonotonicClock.Instance.NowNs) * 1000, 1);
                    s.CalibratedUtc = sink.CalibratedAtUtc;
                }
            }

            Settings.RepeatAll = Engine.Playlist.RepeatAll;
            Settings.Playlist = Engine.Playlist.Items.Select(t => new PlaylistEntry
            {
                Id = t.Id,
                Uri = t.Uri,
                Title = t.Title,
                Artist = t.Artist,
                DurationMs = t.Duration is { } d ? (long)d.TotalMilliseconds : null,
            }).ToList();

            var editor = _prefs.Edit()!;
            editor.PutString(SettingsKey, Settings.ToJson());
            editor.Apply();
        }
    }

    /// <summary>Pushes changed settings into the engine options (used from the next connection on).</summary>
    public void ApplySettings()
    {
        Settings.ApplyTo(Engine.Options);
        Engine.Playlist.RepeatAll = Settings.RepeatAll;
        Save();
    }

    private async Task RestoreRenderersAsync()
    {
        try
        {
            var known = Settings.RendererLocations
                .Select(l => Uri.TryCreate(l, UriKind.Absolute, out var u) ? u : null)
                .Where(u => u is not null)
                .Cast<Uri>()
                .ToList();
            if (known.Count > 0)
            {
                AddRenderers(await RendererDiscovery.LoadAsync(Engine.HttpClient, known, Log).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            Log.Debug("host", $"restoring known TVs failed: {ex.Message}");
        }

        await DiscoverRenderersAsync().ConfigureAwait(false);
    }

    private void AddRenderers(IEnumerable<DlnaRenderer> renderers)
    {
        var changed = false;
        foreach (var renderer in renderers)
        {
            var location = renderer.Location.ToString();
            if (!Settings.RendererLocations.Contains(location))
            {
                // A TV that changed its IP/port: forget the stale location.
                Settings.RendererLocations.RemoveAll(l => Settings.Outputs.ContainsKey(renderer.Id) && l.Contains(renderer.Location.Host, StringComparison.Ordinal));
                Settings.RendererLocations.Add(location);
                changed = true;
            }

            if (Engine.FindSink(renderer.Id) is not null)
            {
                continue;
            }

            var sink = new DlnaSink(renderer);
            ApplySaved(sink, defaultEnabled: true);
            Engine.AddSink(sink);
            changed = true;
        }

        if (changed)
        {
            Save();
        }
    }

    private void SyncLocalOutputs()
    {
        var current = Outputs.List();
        foreach (var output in current)
        {
            if (Engine.FindSink(output.Id) is not null)
            {
                continue;
            }

            var sink = Outputs.CreateSink(output);
            ApplySaved(sink, output.DefaultEnabled);
            Engine.AddSink(sink);
            Log.Info("host", $"output available: {output.Name}");
        }

        foreach (var sink in Engine.Sinks.Where(s => s.Kind == SinkKind.Local))
        {
            if (current.All(o => o.Id != sink.Id))
            {
                Log.Info("host", $"output gone: {sink.Name}");
                _ = Engine.RemoveSinkAsync(sink.Id);
            }
        }
    }

    private void ApplySaved(SinkBase sink, bool defaultEnabled)
    {
        var saved = Settings.Outputs.TryGetValue(sink.Id, out var s) ? s : null;
        sink.Enabled = saved?.Enabled ?? defaultEnabled;
        sink.UserOffsetSeconds = (saved?.OffsetMs ?? 0) / 1000.0;
        if (sink.Kind == SinkKind.Local && saved is not null)
        {
            sink.Volume = saved.Volume;
        }

        if (saved?.LatencyMs is { } latency && saved.CalibratedUtc is { } when && DateTime.UtcNow - when < TimeSpan.FromDays(30))
        {
            // Remembered measurement: good enough to start with; auto-calibration refreshes it.
            sink.LatencySeconds = latency / 1000.0;
            sink.IsCalibrated = true;
            sink.CalibratedAtUtc = when;
        }
    }
}
