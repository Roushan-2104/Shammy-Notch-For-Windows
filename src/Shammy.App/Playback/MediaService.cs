using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Core.Music;
using Shammy.Infrastructure;
using Windows.Foundation;
using Windows.Media.Control;

namespace Shammy.Playback;

/// <summary>
/// Now-playing info for whatever app Windows considers the current media session (Spotify, a browser
/// tab, Apple Music, VLC...), through the same API the Windows media flyout uses.
/// </summary>
internal sealed class MediaService : IDisposable
{
    readonly Dispatcher _ui;
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    GlobalSystemMediaTransportControlsSession? _session;
    int _propertiesVersion;
    TimeSpan _position;
    DateTimeOffset _positionStamp;
    double _rate = 1;

    public MediaService(Dispatcher ui) => _ui = ui;

    public event Action? Changed;

    bool _demo;

    public bool HasSession => (_demo || _session is not null) && Title.Length > 0;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public ImageSource? Art { get; private set; }
    public Color ArtColor { get; private set; } = Colors.White;
    public bool IsPlaying { get; private set; }

    /// <summary>The app behind the session shown ("Spotify.exe", "Chrome"), empty for none.</summary>
    public string SourceApp => _session?.SourceAppUserModelId ?? "";
    public TimeSpan Duration { get; private set; }
    public bool CanSeek { get; private set; }
    public bool CanSkipNext { get; private set; }
    public bool CanSkipPrevious { get; private set; }

    /// <summary>Apps only report position occasionally, so it's extrapolated between reports.</summary>
    public TimeSpan Position
    {
        get
        {
            if (!IsPlaying || _positionStamp == default) return Clamp(_position);
            var elapsed = DateTimeOffset.Now - _positionStamp;
            return Clamp(_position + TimeSpan.FromTicks((long)(elapsed.Ticks * _rate)));
        }
    }

    TimeSpan Clamp(TimeSpan t) =>
        t < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && t > Duration ? Duration : t;

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Post(() => Attach(Pick()));
        // With nothing shown (say, only a chat app was "playing"), a player that appears later is picked up.
        _manager.SessionsChanged += (_, _) => Post(() =>
        {
            // A hidden tab whose browser closed is forgotten, so it can't keep a later tab of that browser in second place.
            if (_hidden is { } hidden && !StillListed(hidden)) Unhide();
            if (_session is null) Attach(Pick());
        });
        Attach(Pick());
    }

    bool StillListed(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            return _manager?.GetSessions().Any(o => SameApp(o, s)) ?? false;
        }
        catch (Exception ex)
        {
            Log.Warn("Listing media sessions failed", ex);
            return true;
        }
    }

    /// <summary>Apps found this run to be chat or call apps, not music (<see cref="NotMusic"/>).</summary>
    readonly HashSet<string> _notMusic = new(StringComparer.OrdinalIgnoreCase);

    bool IsNotMusic(GlobalSystemMediaTransportControlsSession s)
    {
        var id = s.SourceAppUserModelId ?? "";
        return _notMusic.Contains(id) || NotMusic.Is(id, null, null);
    }

    /// <summary>
    /// An installed web app ("Chrome._crx_…"), the only kind remembered as not music: a browser's own id is shared by
    /// all its tabs (and Firefox's is a hash), so one WhatsApp Web sound must never hide YouTube in it for the run.
    /// Desktop chat apps are caught by their id in <see cref="NotMusic"/> anyway.
    /// </summary>
    static bool IsOneApp(string id) => id.Contains("_crx_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows' current session, unless it's a chat app: then a player that's playing, else the one already shown,
    /// else any other player (a paused Spotify stays on the card).
    /// </summary>
    GlobalSystemMediaTransportControlsSession? Pick()
    {
        if (_manager is null) return null;
        try
        {
            var current = _manager.GetCurrentSession();
            if (current is null) return null;
            if (!IsNotMusic(current))
            {
                // A browser tab hidden for now (WhatsApp Web in a plain tab) stays current in Windows' eyes; a player
                // that's actually playing wins over it. With none, the tab stays attached so its next track is seen.
                if (_hidden is null || current.SourceAppUserModelId != _hidden.SourceAppUserModelId) return current;
                return _manager.GetSessions().FirstOrDefault(s => !IsNotMusic(s) && s.SourceAppUserModelId != _hidden.SourceAppUserModelId &&
                    s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? current;
            }
            var players = _manager.GetSessions().Where(s => !IsNotMusic(s)).ToList();
            var shownId = _session?.SourceAppUserModelId;
            return players.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? players.FirstOrDefault(s => shownId is not null && s.SourceAppUserModelId == shownId)
                ?? players.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn("Listing media sessions failed", ex);
            return null;
        }
    }

    /// <summary>A plain browser tab whose track is a chat app's page, hidden until it shows something else.</summary>
    GlobalSystemMediaTransportControlsSession? _hidden;

    /// <summary>Stops watching the hidden tab; set only while another player is shown in its place.</summary>
    Action? _unwatchHidden;

    void Hide(GlobalSystemMediaTransportControlsSession session)
    {
        Unhide();
        _hidden = session;
    }

    void Unhide()
    {
        StopWatchingHidden();
        _hidden = null;
    }

    static bool SameApp(GlobalSystemMediaTransportControlsSession? a, GlobalSystemMediaTransportControlsSession? b) =>
        a is not null && b is not null && (ReferenceEquals(a, b) || a.SourceAppUserModelId == b.SourceAppUserModelId);

    /// <summary>
    /// While another player is shown, the hidden tab's own changes are watched (when it's the one attached, its usual
    /// handlers already look at every new track).
    /// </summary>
    void WatchHidden()
    {
        var s = _hidden;
        if (s is null || _unwatchHidden is not null || SameApp(s, _session)) return;
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProps = (_, _) => Post(() => _ = RecheckHiddenAsync(s));
        TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTimeline = (_, _) => Post(() => _ = RecheckHiddenAsync(s));
        s.MediaPropertiesChanged += onProps;
        s.TimelinePropertiesChanged += onTimeline;
        _unwatchHidden = () =>
        {
            s.MediaPropertiesChanged -= onProps;
            s.TimelinePropertiesChanged -= onTimeline;
        };
    }

    void StopWatchingHidden()
    {
        _unwatchHidden?.Invoke();
        _unwatchHidden = null;
    }

    /// <summary>The hidden tab moved on (a new track, or its length arrived): if it's music now, it's shown again.</summary>
    async Task RecheckHiddenAsync(GlobalSystemMediaTransportControlsSession s)
    {
        if (!ReferenceEquals(s, _hidden)) return;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props is null || !ReferenceEquals(s, _hidden)) return;
            var t = s.GetTimelineProperties();
            var length = t is null ? TimeSpan.Zero : t.EndTime - t.StartTime;
            var artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            if (NotMusic.Is(s.SourceAppUserModelId, props.Title, artist, length)) return;
            Unhide();
            Attach(Pick());
        }
        catch (Exception ex)
        {
            Log.Warn("Re-checking a hidden media session failed", ex);
        }
    }

    /// <summary>Test pipe only: a made-up song, so screenshots don't show what's really playing on this PC.</summary>
    public void ShowDemoForTest(string title, string artist, ImageSource? art)
    {
        Attach(null);
        _demo = true;
        Title = title;
        Artist = artist;
        Art = art;
        if (art is System.Windows.Media.Imaging.BitmapSource bmp) ArtColor = ArtPalette.Vibrant(bmp);
        IsPlaying = true;
        Duration = TimeSpan.FromMinutes(3.5);
        _position = TimeSpan.FromMinutes(1.2);
        _positionStamp = DateTimeOffset.Now;
        Changed?.Invoke();
    }

    void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_demo) return;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        if (SameApp(_hidden, session)) StopWatchingHidden();
        else WatchHidden();
        if (session is null)
        {
            Title = Artist = "";
            Art = null;
            IsPlaying = false;
            Changed?.Invoke();
            return;
        }

        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync();
    }

    void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        Post(() => _ = RefreshPropertiesAsync());

    void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        Post(() =>
        {
            RefreshPlayback();
            RefreshTimeline();
            Changed?.Invoke();
        });

    void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        Post(() =>
        {
            bool hadLength = Duration > TimeSpan.Zero;
            RefreshTimeline();
            // Whether there's a length decides whether a chat app's name is a song ("Signal") or a page ("WhatsApp"),
            // and the length can come before or after the title: look again when it appears or goes.
            if (hadLength != Duration > TimeSpan.Zero) _ = RefreshPropertiesAsync();
            Changed?.Invoke();
        });

    async Task RefreshPropertiesAsync()
    {
        var session = _session;
        if (session is null) return;
        int version = ++_propertiesVersion;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _propertiesVersion || props is null) return;
            var artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            if (NotMusic.Is(session.SourceAppUserModelId, props.Title, artist, Duration))
            {
                // A chat app's "track" (WhatsApp's web app reports one titled "WhatsApp"): never shown as music.
                var id = session.SourceAppUserModelId ?? "";
                if (IsOneApp(id))
                {
                    _notMusic.Add(id);
                    Attach(Pick());
                    return;
                }
                // A plain browser tab: the id is the whole browser's, so only this track is hidden. The tab is
                // watched for its next track (or a length arriving late), and a player that's playing takes over.
                Title = Artist = "";
                Art = null;
                Hide(session);
                var other = Pick();
                if (other is not null && other.SourceAppUserModelId != id) Attach(other);
                else Changed?.Invoke();
                return;
            }
            if (SameApp(session, _hidden)) Unhide();

            ImageSource? art = null;
            var color = Colors.White;
            if (props.Thumbnail is not null)
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // Sharp enough for the 300 px art panel on a 2x screen.
                bmp.DecodePixelWidth = 600;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                art = bmp;
                color = ArtPalette.Vibrant(bmp);
            }
            if (version != _propertiesVersion) return;
            // Title and artwork change together, so nothing sees a new song with the old song's colour.
            Title = props.Title ?? "";
            Artist = artist;
            Art = art;
            ArtColor = color;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading media properties failed", ex);
        }
        Changed?.Invoke();
    }

    void RefreshPlayback()
    {
        var info = _session?.GetPlaybackInfo();
        if (info is null) return;
        IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        _rate = info.PlaybackRate is > 0 ? info.PlaybackRate.Value : 1;
        CanSeek = info.Controls.IsPlaybackPositionEnabled;
        CanSkipNext = info.Controls.IsNextEnabled;
        CanSkipPrevious = info.Controls.IsPreviousEnabled;
    }

    void RefreshTimeline()
    {
        var t = _session?.GetTimelineProperties();
        if (t is null) return;
        Duration = t.EndTime - t.StartTime;
        _position = t.Position - t.StartTime;
        // Some apps never stamp the position; treat that as "just now" so the bar doesn't jump.
        _positionStamp = t.LastUpdatedTime.Year < 2000 ? DateTimeOffset.Now : t.LastUpdatedTime;
    }

    public void TogglePlayPause() => Fire(_session?.TryTogglePlayPauseAsync());
    public void Next() => Fire(_session?.TrySkipNextAsync());
    public void Previous() => Fire(_session?.TrySkipPreviousAsync());

    public void Seek(TimeSpan position)
    {
        var t = _session?.GetTimelineProperties();
        if (t is null || !CanSeek) return;
        _position = position;
        _positionStamp = DateTimeOffset.Now;
        Fire(_session!.TryChangePlaybackPositionAsync((t.StartTime + position).Ticks));
        Changed?.Invoke();
    }

    static async void Fire(Windows.Foundation.IAsyncOperation<bool>? op)
    {
        if (op is null) return;
        try { await op; }
        catch (Exception ex) { Log.Warn("Media command failed", ex); }
    }

    void Post(Action a) => _ui.BeginInvoke(a);

    public void Dispose()
    {
        Unhide();
        Attach(null);
    }
}
