using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.Session;
using Android.OS;
using MusicAura.Core.Engine;

namespace MusicAura.Droid;

/// <summary>
/// Foreground service that keeps the session alive with the screen off: media notification,
/// wake/Wi-Fi locks, audio focus, and a MediaSession so the Bluetooth speaker's own
/// play/pause/next buttons and the lock screen control playback.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class PlaybackService : Service
{
    public const string ActionToggle = "com.musicaura.action.TOGGLE";
    public const string ActionNext = "com.musicaura.action.NEXT";
    public const string ActionPrevious = "com.musicaura.action.PREVIOUS";
    public const string ActionDisconnect = "com.musicaura.action.DISCONNECT";

    private const string ChannelId = "playback";
    private const int NotificationId = 1;

    private AuraHost? _host;
    private MediaSession? _session;
    private PowerManager.WakeLock? _wakeLock;
    private AudioFocusRequestClass? _focusRequest;
    private Handler? _handler;
    private Action? _tick;
    private string? _lastNotificationKey;

    public static void Start(Context context)
    {
        var intent = new Intent(context, typeof(PlaybackService));
        context.StartForegroundService(intent);
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        _host = AuraHost.Get(this);
        _handler = new Handler(Looper.MainLooper!);

        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(ChannelId, GetString(Resource.String.notification_channel), NotificationImportance.Low));

        _session = new MediaSession(this, "MusicAura");
        _session.SetCallback(new SessionCallback(_host));
        _session.Active = true;

        var power = (PowerManager)GetSystemService(PowerService)!;
        _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "musicaura:playback");
        _wakeLock!.SetReferenceCounted(false);
        _wakeLock.Acquire();
        _host.Network.AcquireWifiLock();
        RequestAudioFocus();

        _tick = () =>
        {
            Update();
            _handler?.PostDelayed(_tick!, 1000);
        };
        _handler.PostDelayed(_tick, 1000);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = BuildNotification(_host!.Engine.GetSnapshot());
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }

        var engine = _host.Engine;
        switch (intent?.Action)
        {
            case ActionToggle:
                _ = engine.TogglePlayPauseAsync();
                break;
            case ActionNext:
                _ = engine.NextAsync();
                break;
            case ActionPrevious:
                _ = engine.PreviousAsync();
                break;
            case ActionDisconnect:
                _ = Task.Run(async () =>
                {
                    await engine.StopSessionAsync().ConfigureAwait(false);
                    _handler?.Post(StopSelf);
                });
                break;
        }

        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_tick is not null)
        {
            _handler?.RemoveCallbacks(_tick);
        }

        if (_focusRequest is not null)
        {
            ((AudioManager)GetSystemService(AudioService)!).AbandonAudioFocusRequest(_focusRequest);
        }

        _session?.Release();
        _wakeLock?.Release();
        _host?.Network.ReleaseWifiLock();
        base.OnDestroy();
    }

    private void Update()
    {
        var host = _host!;
        var snapshot = host.Engine.GetSnapshot();
        if (!snapshot.SessionActive)
        {
            StopForeground(StopForegroundFlags.Remove);
            StopSelf();
            return;
        }

        var playing = snapshot.State == EngineState.Playing;
        var actions = PlaybackState.ActionPlay | PlaybackState.ActionPause | PlaybackState.ActionPlayPause |
                      PlaybackState.ActionSkipToNext | PlaybackState.ActionSkipToPrevious | PlaybackState.ActionStop;
        _session!.SetPlaybackState(new PlaybackState.Builder()
            .SetActions(actions)!
            .SetState(playing ? PlaybackStateCode.Playing : PlaybackStateCode.Paused, (long)snapshot.Position.TotalMilliseconds, playing ? 1f : 0f)!
            .Build());

        var key = $"{snapshot.State}|{snapshot.Track?.Id}";
        if (key == _lastNotificationKey)
        {
            return;
        }

        _lastNotificationKey = key;
        var metadata = new MediaMetadata.Builder()
            .PutString(MediaMetadata.MetadataKeyTitle, snapshot.Track?.Title ?? GetString(Resource.String.app_name))!
            .PutString(MediaMetadata.MetadataKeyArtist, snapshot.Track?.Artist ?? "")!;
        if (snapshot.Duration is { } d)
        {
            metadata.PutLong(MediaMetadata.MetadataKeyDuration, (long)d.TotalMilliseconds);
        }

        _session.SetMetadata(metadata.Build());
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.Notify(NotificationId, BuildNotification(snapshot));
    }

    private Notification BuildNotification(EngineSnapshot snapshot)
    {
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable)!;
        var builder = new Notification.Builder(this, ChannelId)
            .SetSmallIcon(Resource.Drawable.ic_notification)!
            .SetContentTitle(snapshot.Track?.DisplayName ?? GetString(Resource.String.app_name))!
            .SetContentText(snapshot.State == EngineState.Playing
                ? string.Join(", ", snapshot.Sinks.Where(s => s.Running).Select(s => s.Name))
                : GetString(Resource.String.notification_idle))!
            .SetContentIntent(open)!
            .SetOngoing(true)!
            .SetVisibility(NotificationVisibility.Public)!
            .AddAction(Action(Android.Resource.Drawable.IcMediaPrevious, Resource.String.prev, ActionPrevious))!
            .AddAction(snapshot.State == EngineState.Playing
                ? Action(Android.Resource.Drawable.IcMediaPause, Resource.String.pause, ActionToggle)
                : Action(Android.Resource.Drawable.IcMediaPlay, Resource.String.play, ActionToggle))!
            .AddAction(Action(Android.Resource.Drawable.IcMediaNext, Resource.String.next, ActionNext))!
            .AddAction(Action(Android.Resource.Drawable.IcMenuCloseClearCancel, Resource.String.disconnect, ActionDisconnect))!;

        var style = new Notification.MediaStyle().SetShowActionsInCompactView(0, 1, 2)!;
        if (_session is not null)
        {
            style.SetMediaSession(_session.SessionToken);
        }

        builder.SetStyle(style);
        return builder.Build()!;
    }

    private Notification.Action Action(int icon, int title, string action)
    {
        var intent = new Intent(this, typeof(PlaybackService)).SetAction(action);
        var pending = PendingIntent.GetService(this, action.GetHashCode(StringComparison.Ordinal), intent,
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
#pragma warning disable CA1422 // the Icon-based overload needs API 23; this one is fine everywhere
        return new Notification.Action.Builder(icon, GetString(title), pending).Build()!;
#pragma warning restore CA1422
    }

    private void RequestAudioFocus()
    {
        var audio = (AudioManager)GetSystemService(AudioService)!;
        _focusRequest = new AudioFocusRequestClass.Builder(AudioFocus.Gain)
            .SetAudioAttributes(new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!)!
            .SetOnAudioFocusChangeListener(new FocusListener(_host!))!
            .Build()!;
        audio.RequestAudioFocus(_focusRequest);
    }

    private sealed class FocusListener : Java.Lang.Object, AudioManager.IOnAudioFocusChangeListener
    {
        private readonly AuraHost _host;

        public FocusListener(AuraHost host) => _host = host;

        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            // A call or another player took over: pause everything (TV included).
            if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient)
            {
                _ = _host.Engine.PauseAsync();
            }
        }
    }

    private sealed class SessionCallback : MediaSession.Callback
    {
        private readonly AuraHost _host;

        public SessionCallback(AuraHost host) => _host = host;

        public override void OnPlay() => _ = _host.Engine.PlayAsync();

        public override void OnPause() => _ = _host.Engine.PauseAsync();

        public override void OnSkipToNext() => _ = _host.Engine.NextAsync();

        public override void OnSkipToPrevious() => _ = _host.Engine.PreviousAsync();

        public override void OnStop() => _ = _host.Engine.StopAsync();

        public override void OnSeekTo(long pos) => _ = _host.Engine.SeekAsync(TimeSpan.FromMilliseconds(pos));
    }
}
