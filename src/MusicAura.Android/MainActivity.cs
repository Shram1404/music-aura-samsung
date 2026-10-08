using System.Globalization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Database;
using Android.Media;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using MusicAura.Core.Audio;
using MusicAura.Core.Dlna;
using MusicAura.Core.Engine;
using MusicAura.Core.Engine.Sinks;

namespace MusicAura.Droid;

[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : Activity
{
    private const int RequestPickFiles = 10;
    private const int RequestStartupPermissionsCode = 20;
    private const int RequestLibraryPermission = 21;

    private readonly Dictionary<string, OutputRow> _rows = [];
    private AuraHost _host = null!;
    private Handler _handler = null!;
    private Action? _refresh;
    private string? _playlistSignature;
    private bool _userSeeking;
    private bool _logVisible;

    private TextView _status = null!;
    private TextView _nowTitle = null!;
    private TextView _nowTime = null!;
    private TextView _calibration = null!;
    private TextView _noOutputs = null!;
    private TextView _log = null!;
    private SeekBar _seek = null!;
    private Button _play = null!;
    private Button _findTvs = null!;
    private Button _format = null!;
    private Button _http = null!;
    private LinearLayout _outputs = null!;
    private LinearLayout _playlist = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);
        _host = AuraHost.Get(this);
        _handler = new Handler(Looper.MainLooper!);

        var root = FindViewById(Resource.Id.root)!;
        root.SetOnApplyWindowInsetsListener(new InsetsListener());

        _status = FindViewById<TextView>(Resource.Id.status_text)!;
        _nowTitle = FindViewById<TextView>(Resource.Id.now_title)!;
        _nowTime = FindViewById<TextView>(Resource.Id.now_time)!;
        _calibration = FindViewById<TextView>(Resource.Id.calibration_text)!;
        _noOutputs = FindViewById<TextView>(Resource.Id.no_outputs)!;
        _log = FindViewById<TextView>(Resource.Id.log_text)!;
        _seek = FindViewById<SeekBar>(Resource.Id.seek_bar)!;
        _play = FindViewById<Button>(Resource.Id.btn_play)!;
        _findTvs = FindViewById<Button>(Resource.Id.btn_find_tvs)!;
        _format = FindViewById<Button>(Resource.Id.btn_format)!;
        _http = FindViewById<Button>(Resource.Id.btn_http)!;
        _outputs = FindViewById<LinearLayout>(Resource.Id.outputs_container)!;
        _playlist = FindViewById<LinearLayout>(Resource.Id.playlist_container)!;

        var engine = _host.Engine;
        _play.Click += (_, _) => Run(async () =>
        {
            EnsureService();
            await engine.TogglePlayPauseAsync();
        });
        FindViewById<Button>(Resource.Id.btn_prev)!.Click += (_, _) => Run(engine.PreviousAsync);
        FindViewById<Button>(Resource.Id.btn_next)!.Click += (_, _) => Run(engine.NextAsync);
        FindViewById<Button>(Resource.Id.btn_stop)!.Click += (_, _) => Run(engine.StopAsync);
        _findTvs.Click += (_, _) => Run(_host.DiscoverRenderersAsync);
        FindViewById<Button>(Resource.Id.btn_calibrate)!.Click += (_, _) => Run(CalibrateAsync);
        FindViewById<Button>(Resource.Id.btn_disconnect)!.Click += (_, _) => Run(engine.StopSessionAsync);
        FindViewById<Button>(Resource.Id.btn_add_files)!.Click += (_, _) => PickFiles();
        FindViewById<Button>(Resource.Id.btn_add_library)!.Click += (_, _) => AddLibrary();
        FindViewById<Button>(Resource.Id.btn_clear)!.Click += (_, _) => engine.Playlist.Clear();
        FindViewById<Button>(Resource.Id.btn_log)!.Click += (_, _) =>
        {
            _logVisible = !_logVisible;
            _log.Visibility = _logVisible ? ViewStates.Visible : ViewStates.Gone;
        };

        _seek.ProgressChanged += (_, e) =>
        {
            if (e.FromUser && engine.GetSnapshot().Duration is { } d)
            {
                _nowTime.Text = $"{Format(d * (e.Progress / 1000.0))} / {Format(d)}";
            }
        };
        _seek.StartTrackingTouch += (_, _) => _userSeeking = true;
        _seek.StopTrackingTouch += (_, _) =>
        {
            _userSeeking = false;
            if (engine.GetSnapshot().Duration is { } d)
            {
                Run(() => engine.SeekAsync(d * (_seek.Progress / 1000.0)));
            }
        };

        BindSwitch(Resource.Id.sw_auto_calibrate, _host.Settings.AutoCalibrate, v => _host.Settings.AutoCalibrate = v);
        BindSwitch(Resource.Id.sw_drift, _host.Settings.DriftCompensation, v => _host.Settings.DriftCompensation = v);
        BindSwitch(Resource.Id.sw_instant_pause, _host.Settings.PauseMode == PauseMode.Instant,
            v => _host.Settings.PauseMode = v ? PauseMode.Instant : PauseMode.Synchronized);
        BindSwitch(Resource.Id.sw_repeat, _host.Settings.RepeatAll, v => _host.Settings.RepeatAll = v);
        _format.Click += (_, _) =>
        {
            _host.Settings.StreamFormat = (StreamFormatPreference)(((int)_host.Settings.StreamFormat + 1) % 3);
            _host.ApplySettings();
            UpdateSettingButtons();
        };
        _http.Click += (_, _) =>
        {
            _host.Settings.HttpBodyMode = (HttpBodyMode)(((int)_host.Settings.HttpBodyMode + 1) % 3);
            _host.ApplySettings();
            UpdateSettingButtons();
        };
        UpdateSettingButtons();

        RequestStartupPermissions();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _refresh = () =>
        {
            Refresh();
            _handler.PostDelayed(_refresh!, 300);
        };
        _handler.Post(_refresh);
    }

    protected override void OnPause()
    {
        if (_refresh is not null)
        {
            _handler.RemoveCallbacks(_refresh);
        }

        _host.Save();
        base.OnPause();
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != RequestPickFiles || resultCode != Result.Ok || data is null)
        {
            return;
        }

        var uris = new List<Android.Net.Uri>();
        if (data.ClipData is { } clip)
        {
            for (var i = 0; i < clip.ItemCount; i++)
            {
                if (clip.GetItemAt(i)?.Uri is { } u)
                {
                    uris.Add(u);
                }
            }
        }
        else if (data.Data is { } single)
        {
            uris.Add(single);
        }

        foreach (var uri in uris)
        {
            try
            {
                ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
            }
            catch (Java.Lang.SecurityException)
            {
                // Not persistable (some providers); playable during this run at least.
            }
        }

        Run(() => Task.Run(() => _host.Engine.Playlist.Add(uris.Select(DescribeTrack).ToList())));
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == RequestLibraryPermission && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            AddLibrary();
        }
        else if (requestCode == RequestStartupPermissionsCode)
        {
            var mic = Array.IndexOf(permissions, Android.Manifest.Permission.RecordAudio);
            if (mic >= 0 && grantResults[mic] != Permission.Granted)
            {
                Toast.MakeText(this, Resource.String.mic_needed, ToastLength.Long)!.Show();
            }
        }
    }

    // ------------------------------------------------------------------ UI refresh

    private void Refresh()
    {
        var engine = _host.Engine;
        var s = engine.GetSnapshot();
        var playingOn = s.Sinks.Where(x => x.Running && x.Status == SinkStatus.Active).Select(x => x.Name).ToList();
        _status.Text = s.State switch
        {
            EngineState.Starting => GetString(Resource.String.status_starting) + (s.Message is null ? "" : $"\n{s.Message}"),
            EngineState.Calibrating => GetString(Resource.String.status_calibrating),
            EngineState.Ready => GetString(Resource.String.status_ready),
            EngineState.Playing => Str(Resource.String.status_playing, string.Join(", ", playingOn)),
            EngineState.Paused => GetString(Resource.String.status_paused),
            _ => GetString(Resource.String.status_idle),
        } + (s.State is not (EngineState.Starting or EngineState.Idle) && s.Message is not null ? $"\n{s.Message}" : "");

        _nowTitle.Text = s.Track?.DisplayName ?? GetString(Resource.String.nothing_playing);
        if (s.Track is not null)
        {
            var time = s.Duration is { } d ? $"{Format(s.Position)} / {Format(d)}" : Format(s.Position);
            if (!_userSeeking)
            {
                _nowTime.Text = s.Buffering ? GetString(Resource.String.starting) : time;
                _seek.Progress = s.Duration is { TotalMilliseconds: > 0 } dd ? (int)(s.Position / dd * 1000) : 0;
            }
        }
        else
        {
            _nowTime.Text = "";
            _seek.Progress = 0;
        }

        _play.Text = GetString(s.State == EngineState.Playing ? Resource.String.pause : Resource.String.play);
        _findTvs.Text = GetString(_host.Discovering ? Resource.String.searching : Resource.String.find_tvs);
        _findTvs.Enabled = !_host.Discovering;

        UpdateOutputs(s);
        UpdatePlaylist(s);

        if (s.LastCalibration is { } report)
        {
            _calibration.Visibility = ViewStates.Visible;
            _calibration.Text = report.Success
                ? Str(Resource.String.calibration_ok, string.Join(", ", report.Sinks.Select(r => $"{r.SinkName} {Ms(r.LatencySeconds ?? 0)} ms")))
                : Str(Resource.String.calibration_failed, report.Summary);
        }

        if (_logVisible)
        {
            _log.Text = string.Join("\n", _host.Log.Snapshot(150).Select(e => e.ToString()));
        }
    }

    private void UpdateOutputs(EngineSnapshot s)
    {
        foreach (var gone in _rows.Keys.Where(id => s.Sinks.All(x => x.Id != id)).ToList())
        {
            _outputs.RemoveView(_rows[gone].Root);
            _rows.Remove(gone);
        }

        foreach (var sink in s.Sinks.OrderBy(x => x.Kind).ThenBy(x => x.Name))
        {
            if (!_rows.TryGetValue(sink.Id, out var row))
            {
                row = new OutputRow(this, sink.Id, _host);
                _rows[sink.Id] = row;
                _outputs.AddView(row.Root);
            }

            row.Update(sink);
        }

        _noOutputs.Visibility = s.Sinks.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
    }

    private void UpdatePlaylist(EngineSnapshot s)
    {
        var items = _host.Engine.Playlist.Items;
        var signature = string.Join("|", items.Select(i => i.Id)) + "#" + s.Track?.Id;
        if (signature == _playlistSignature)
        {
            return;
        }

        _playlistSignature = signature;
        _playlist.RemoveAllViews();
        if (items.Count == 0)
        {
            _playlist.AddView(new TextView(this) { Text = GetString(Resource.String.playlist_empty), Alpha = 0.7f });
            return;
        }

        var pad = Dp(10);
        foreach (var track in items)
        {
            var current = track.Id == s.Track?.Id;
            var view = new TextView(this)
            {
                Text = (current ? "▶  " : "") + track.DisplayName + (track.Duration is { } d ? $"   ·  {Format(d)}" : ""),
                TextSize = 15,
            };
            view.SetPadding(Dp(4), pad, Dp(4), pad);
            view.SetTypeface(null, current ? Android.Graphics.TypefaceStyle.Bold : Android.Graphics.TypefaceStyle.Normal);
            view.Click += (_, _) => Run(async () =>
            {
                EnsureService();
                await _host.Engine.PlayAsync(track, TimeSpan.Zero);
            });
            view.LongClick += (_, _) =>
            {
                _host.Engine.Playlist.Remove(track);
                Toast.MakeText(this, Resource.String.removed, ToastLength.Short)!.Show();
            };
            _playlist.AddView(view);
        }
    }

    private void UpdateSettingButtons()
    {
        _format.Text = Str(Resource.String.format_button, _host.Settings.StreamFormat switch
        {
            StreamFormatPreference.Wav => "WAV",
            StreamFormatPreference.L16 => "L16",
            _ => "Auto",
        });
        _http.Text = Str(Resource.String.http_button, _host.Settings.HttpBodyMode switch
        {
            HttpBodyMode.Chunked => "chunked",
            HttpBodyMode.HugeContentLength => "length",
            _ => "close",
        });
    }

    // ------------------------------------------------------------------ actions

    private async Task CalibrateAsync()
    {
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            RequestPermissions([Android.Manifest.Permission.RecordAudio], RequestStartupPermissionsCode);
            return;
        }

        EnsureService();
        await _host.Engine.CalibrateAsync();
    }

    private void EnsureService()
    {
        PlaybackService.Start(this);
    }

    private void PickFiles()
    {
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("audio/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, RequestPickFiles);
    }

    private void AddLibrary()
    {
        var permission = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? Android.Manifest.Permission.ReadMediaAudio
            : Android.Manifest.Permission.ReadExternalStorage;
        if (CheckSelfPermission(permission) != Permission.Granted)
        {
            RequestPermissions([permission], RequestLibraryPermission);
            return;
        }

        Run(() => Task.Run(() =>
        {
            var tracks = new List<TrackInfo>();
            var collection = MediaStore.Audio.Media.ExternalContentUri!;
            string[] projection =
            [
                MediaStore.Audio.Media.InterfaceConsts.Id,
                MediaStore.Audio.Media.InterfaceConsts.Title,
                MediaStore.Audio.Media.InterfaceConsts.Artist,
                MediaStore.Audio.Media.InterfaceConsts.Duration,
            ];
            using ICursor? cursor = ContentResolver!.Query(collection, projection,
                MediaStore.Audio.Media.InterfaceConsts.IsMusic + " != 0", null,
                MediaStore.Audio.Media.InterfaceConsts.Artist + ", " + MediaStore.Audio.Media.InterfaceConsts.Title);
            while (cursor?.MoveToNext() == true)
            {
                var id = cursor.GetLong(0);
                var uri = ContentUris.WithAppendedId(collection, id)!;
                var artist = cursor.GetString(2);
                tracks.Add(new TrackInfo(
                    uri.ToString()!,
                    cursor.GetString(1) ?? $"#{id}",
                    artist == "<unknown>" ? null : artist,
                    uri.ToString()!,
                    TimeSpan.FromMilliseconds(cursor.GetLong(3))));
            }

            _host.Engine.Playlist.Add(tracks);
        }));
    }

    private TrackInfo DescribeTrack(Android.Net.Uri uri)
    {
        string? title = null, artist = null;
        TimeSpan? duration = null;
        try
        {
            using var retriever = new MediaMetadataRetriever();
            retriever.SetDataSource(this, uri);
            title = retriever.ExtractMetadata(MetadataKey.Title);
            artist = retriever.ExtractMetadata(MetadataKey.Artist);
            if (long.TryParse(retriever.ExtractMetadata(MetadataKey.Duration), out var ms))
            {
                duration = TimeSpan.FromMilliseconds(ms);
            }
        }
        catch (Exception)
        {
            // Unreadable metadata is fine; fall back to the file name.
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            using var c = ContentResolver!.Query(uri, [IOpenableColumns.DisplayName], null, null, null);
            title = c?.MoveToFirst() == true ? Path.GetFileNameWithoutExtension(c.GetString(0)) : uri.LastPathSegment;
        }

        return new TrackInfo(uri.ToString()!, title ?? "?", artist, uri.ToString()!, duration);
    }

    private void RequestStartupPermissions()
    {
        var wanted = new List<string>();
        if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            wanted.Add(Android.Manifest.Permission.RecordAudio);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
            CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
        {
            wanted.Add(Android.Manifest.Permission.PostNotifications);
        }

        if (wanted.Count > 0)
        {
            RequestPermissions(wanted.ToArray(), RequestStartupPermissionsCode);
        }
    }

    private void BindSwitch(int id, bool value, Action<bool> set)
    {
        var sw = FindViewById<Switch>(id)!;
        sw.Checked = value;
        sw.CheckedChange += (_, e) =>
        {
            set(e.IsChecked);
            _host.ApplySettings();
        };
    }

    private void Run(Func<Task> action)
    {
        _ = RunCore();

        async Task RunCore()
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _host.Log.Error("ui", "action failed", ex);
                RunOnUiThread(() => Toast.MakeText(this, ex.Message, ToastLength.Long)!.Show());
            }
        }
    }

    /// <summary>Formats a string resource with text arguments.</summary>
    private string Str(int id, params string[] args) =>
        GetString(id, args.Select(a => (Java.Lang.Object)new Java.Lang.String(a)).ToArray());

    private int Dp(int dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string Ms(double seconds) => Math.Round(seconds * 1000).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Pads the content clear of the status and navigation bars (edge-to-edge on Android 15+).</summary>
    private sealed class InsetsListener : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            else
            {
#pragma warning disable CA1422
                v.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CA1422
            }

            return insets;
        }
    }

    /// <summary>One output in the list: enable switch, status, latency, per-device offset and volume.</summary>
    private sealed class OutputRow
    {
        private readonly MainActivity _activity;
        private readonly string _id;
        private readonly AuraHost _host;
        private readonly CheckBox _enabled;
        private readonly TextView _state;
        private readonly TextView _details;
        private readonly TextView _offsetLabel;
        private readonly SeekBar _offset;
        private readonly SeekBar _volume;
        private bool _updating;
        private bool _offsetTouch;
        private bool _volumeTouch;

        public OutputRow(MainActivity activity, string id, AuraHost host)
        {
            _activity = activity;
            _id = id;
            _host = host;
            var engine = host.Engine;

            Root = new LinearLayout(activity) { Orientation = Android.Widget.Orientation.Vertical };
            Root.SetBackgroundResource(Resource.Drawable.card_background);
            var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            lp.SetMargins(0, activity.Dp(8), 0, 0);
            Root.LayoutParameters = lp;

            var header = new LinearLayout(activity) { Orientation = Android.Widget.Orientation.Horizontal };
            _enabled = new CheckBox(activity) { TextSize = 16 };
            _enabled.SetTypeface(null, Android.Graphics.TypefaceStyle.Bold);
            header.AddView(_enabled, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
            _state = new TextView(activity) { Alpha = 0.8f };
            _state.Gravity = GravityFlags.CenterVertical | GravityFlags.End;
            header.AddView(_state, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.MatchParent));
            Root.AddView(header);

            _details = new TextView(activity) { Alpha = 0.7f, TextSize = 12 };
            Root.AddView(_details);

            _offsetLabel = new TextView(activity) { TextSize = 13 };
            _offsetLabel.SetPadding(0, activity.Dp(6), 0, 0);
            Root.AddView(_offsetLabel);
            // -500 … +500 ms in 5 ms steps.
            _offset = new SeekBar(activity) { Max = 200 };
            Root.AddView(_offset);

            var volumeLabel = new TextView(activity) { Text = activity.GetString(Resource.String.volume), TextSize = 13 };
            Root.AddView(volumeLabel);
            _volume = new SeekBar(activity) { Max = 100 };
            Root.AddView(_volume);

            _enabled.CheckedChange += (_, e) =>
            {
                if (_updating)
                {
                    return;
                }

                _activity.Run(async () =>
                {
                    await engine.SetSinkEnabledAsync(_id, e.IsChecked);
                    _host.Save();
                });
            };
            _offset.ProgressChanged += (_, e) =>
            {
                if (!e.FromUser)
                {
                    return;
                }

                var ms = e.Progress * 5 - 500;
                engine.SetUserOffset(_id, ms / 1000.0);
                _offsetLabel.Text = _activity.Str(Resource.String.offset, ms.ToString("+0;-0;0", CultureInfo.InvariantCulture));
            };
            _offset.StartTrackingTouch += (_, _) => _offsetTouch = true;
            _offset.StopTrackingTouch += (_, _) =>
            {
                _offsetTouch = false;
                _host.Save();
            };
            _volume.ProgressChanged += (_, e) =>
            {
                if (e.FromUser)
                {
                    engine.SetVolume(_id, e.Progress / 100.0);
                }
            };
            _volume.StartTrackingTouch += (_, _) => _volumeTouch = true;
            _volume.StopTrackingTouch += (_, _) =>
            {
                _volumeTouch = false;
                _host.Save();
            };
        }

        public LinearLayout Root { get; }

        public void Update(SinkSnapshot s)
        {
            _updating = true;
            _enabled.Text = s.Name;
            _enabled.Checked = s.Enabled;
            _updating = false;

            var a = _activity;
            _state.Text = (s.Kind == SinkKind.Network ? a.GetString(Resource.String.kind_network) : a.GetString(Resource.String.kind_local)) + " · " +
                          (!s.Running
                              ? a.GetString(Resource.String.status_off)
                              : a.GetString(s.Status switch
                              {
                                  SinkStatus.Connecting => Resource.String.status_connecting,
                                  SinkStatus.Buffering => Resource.String.status_buffering,
                                  SinkStatus.Active => Resource.String.status_active,
                                  SinkStatus.Error => Resource.String.status_error,
                                  _ => Resource.String.status_off,
                              }));

            var lines = new List<string>
            {
                s.Calibrated && s.CalibratedAtUtc is { } when
                    ? a.Str(Resource.String.latency_measured, Ms(s.LatencySeconds), when.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
                    : a.Str(Resource.String.latency_assumed, Ms(s.LatencySeconds)),
            };
            if (s.Running && s.Status == SinkStatus.Active)
            {
                lines.Add(a.Str(Resource.String.sync_info,
                    s.SyncErrorMs.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture),
                    s.RateCorrectionPpm.ToString("+0;-0;0", CultureInfo.InvariantCulture)));
            }

            if (Math.Abs(s.DriftPpm) > 0.5)
            {
                lines.Add(a.Str(Resource.String.drift_info, s.DriftPpm.ToString("+0;-0", CultureInfo.InvariantCulture)));
            }

            if (!string.IsNullOrEmpty(s.Detail))
            {
                lines.Add(s.Detail);
            }

            if (!string.IsNullOrEmpty(s.Message))
            {
                lines.Add(s.Message);
            }

            _details.Text = string.Join("\n", lines);

            if (!_offsetTouch)
            {
                var ms = (int)Math.Round(s.OffsetSeconds * 1000);
                _offset.Progress = Math.Clamp((ms + 500) / 5, 0, 200);
                _offsetLabel.Text = a.Str(Resource.String.offset, ms.ToString("+0;-0;0", CultureInfo.InvariantCulture));
            }

            if (!_volumeTouch)
            {
                _volume.Progress = (int)Math.Round(s.Volume * 100);
            }
        }
    }
}
