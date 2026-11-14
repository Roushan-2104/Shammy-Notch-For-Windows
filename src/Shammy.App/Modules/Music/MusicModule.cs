using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Music;
using Shammy.Infrastructure;
using Shammy.Playback;
using NAudio.CoreAudioApi;

namespace Shammy.Modules.Music;

public sealed class MusicSettings
{
    public bool AlbumGlow { get; set; } = true;

    /// <summary>Record songs played on this PC so voice expects their names ("Remember songs for voice").</summary>
    public bool RememberSongs { get; set; } = true;
}

/// <summary>
/// Music: "play ..." on YouTube Music (or Spotify), transport commands, a music card (synced lyrics in its detail), wheel for
/// volume and swipe for next/previous over the notch, and an album-colour outline while playing.
/// </summary>
internal sealed class MusicModule : IShammyModule
{
    ModuleContext _ctx = null!;
    MusicSettings _settings = new();
    YouTubeMusicPlayer _player = null!;
    LyricsSource _lyrics = null!;
    LyricsPanel _panel = null!;
    MusicCard _card = null!;
    bool _cardRelevant;

    readonly SwipeDetector _swipe = new();
    readonly WheelStepper _wheel = new();
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    DateTime _deviceUsed;

    string _trackKey = "";
    int _trackVersion;
    Color? _glow;
    string? _glowTrack, _glowPending;
    DispatcherTimer? _glowFallback;
    YouTubeMusicSong? _lastPlayed;

    // Songs heard on this PC, so "play ..." by voice expects their names. Recorded once a track has played
    // for a while (skipped tracks and half-loaded metadata don't count), never while presenting.
    static readonly TimeSpan HeardAfter = TimeSpan.FromSeconds(20);
    readonly ListeningHistoryStore _historyStore = new();
    ListeningHistory _history = new();
    volatile string[] _historyNames = Array.Empty<string>();
    DispatcherTimer? _heardTimer;
    string _heardKey = "";

    public string Name => "music";

    MediaService Media => _ctx.Services.Media;

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _settings = ctx.Load<MusicSettings>();
        _player = new YouTubeMusicPlayer(ctx);
        _lyrics = new LyricsSource(ctx);
        _panel = new LyricsPanel(Media);

        ctx.AddCommand(new MusicTextCommand(this));
        _history = _historyStore.Load();
        _historyNames = _history.Names().ToArray();
        // Off means off: nothing recorded and nothing remembered offered to voice (the built-in list still is).
        ctx.AddSongVocabulary(() => _settings.RememberSongs ? _historyNames : Array.Empty<string>());
        ctx.AddTrayToggle("Remember songs for voice", () => _settings.RememberSongs, on =>
        {
            _settings.RememberSongs = on;
            ctx.Save(_settings);
            if (!on) _heardTimer?.Stop();
        });
        ctx.AddTrayItem("Clear song history", ClearHistory);
        // v2: the music card replaces the Lyrics tab; its detail hosts the same synced lyrics panel.
        _card = new MusicCard(ctx.Services, _panel);
        ctx.AddCard(new NotchCard
        {
            Id = "music",
            Title = "Music",
            Width = 340,
            Compact = _card.Compact,
            Detail = _card.BuildDetail,
            Order = 20,
            Chip = true,
            IsRelevant = () => _card.IsRelevant,
            Live = () => _card.Live,
            Peek = _card.Peek,
        });
        // No glance of its own: the resting notch's built-in song glance (with the music bars) already shows it.
        Views.Cards.CardPipes.Register(ctx, "music", _card.DescribeCompact, _card.DescribeDetail);
        ctx.AddTrayToggle("Flash album colour on new songs", () => _settings.AlbumGlow, on =>
        {
            _settings.AlbumGlow = on;
            ctx.Save(_settings);
            UpdateGlow();
        });
        ctx.NotchWheel += OnWheel;
        ctx.NotchHorizontalWheel += OnSwipe;

        ctx.AddPipeCommand("music-search", PipeSearch);
        ctx.AddPipeCommand("music-pick", PipePick);
        ctx.AddPipeCommand("music-lyrics", _ => PipeLyrics());
        ctx.AddPipeCommand("music-wheel", arg => PipeWheel(arg));
        ctx.AddPipeCommand("music-swipe", arg => PipeSwipe(arg));
        ctx.AddPipeCommand("music-state", _ => PipeState());
        ctx.AddPipeCommand("music-parse", arg => MusicCommand.Parse(arg) is { } i ? $"{i.Action} {i.Query}".TrimEnd() : "none");

        // The notch shows this under the record. It hides with the lyrics panel while the screen is shared.
        HostLinks.CurrentLyric = () => Media.IsPlaying && _panel.Kind == LyricsKind.Synced && !Infrastructure.PrivacyState.Presenting ? _panel.CurrentLine : null;

        Media.Changed += OnMediaChanged;
        OnMediaChanged();
    }

    // ---- Commands --------------------------------------------------------------------------------

    sealed class MusicTextCommand(MusicModule module) : ITextCommand, ICommandPreview
    {
        public bool CanHandle(string text) => MusicCommand.Parse(text) is not null;
        public Task<CommandResult> RunAsync(string text) => module.RunAsync(MusicCommand.Parse(text)!);

        public CommandPreview? Preview(string text) => MusicCommand.Parse(text) switch
        {
            { Action: MusicAction.PlayYouTubeMusic } i => new CommandPreview("", $"Play “{i.Query}”", "YouTube Music"),
            { Action: MusicAction.PlaySpotify } i => new CommandPreview("", $"Play “{i.Query}”", "Spotify"),
            { Action: MusicAction.Pause } => new CommandPreview("", "Pause", "Media"),
            { Action: MusicAction.Resume } => new CommandPreview("", "Resume", "Media"),
            { Action: MusicAction.Next } => new CommandPreview("", "Next track", "Media"),
            { Action: MusicAction.Previous } => new CommandPreview("", "Previous track", "Media"),
            { Action: MusicAction.ShowLyrics } => new CommandPreview("", "Show lyrics", "Media"),
            _ => null,
        };
    }

    async Task<CommandResult> RunAsync(MusicIntent intent)
    {
        var m = Media;
        switch (intent.Action)
        {
            case MusicAction.PlayYouTubeMusic:
                return await PlayOnYouTubeMusicAsync(intent.Query);

            case MusicAction.PlaySpotify:
                return PlayOnSpotify(intent.Query);

            case MusicAction.Pause:
                if (!m.HasSession) return new CommandResult("\uE769", "Nothing is playing", IsError: true);
                if (m.IsPlaying) m.TogglePlayPause();
                return new CommandResult("\uE769", "Paused", NowPlaying());

            case MusicAction.Resume:
                if (!m.HasSession) return new CommandResult("\uE768", "Nothing to resume", "Say \u201Cplay\u201D and a song name", IsError: true);
                if (!m.IsPlaying) m.TogglePlayPause();
                return new CommandResult("\uE768", "Playing", NowPlaying());

            case MusicAction.Next:
                if (!m.HasSession) return new CommandResult("\uE893", "Nothing is playing", IsError: true);
                m.Next();
                return new CommandResult("\uE893", "Next track");

            case MusicAction.Previous:
                if (!m.HasSession) return new CommandResult("\uE892", "Nothing is playing", IsError: true);
                m.Previous();
                return new CommandResult("\uE892", "Previous track");

            case MusicAction.ShowLyrics:
                LoadLyricsForCurrentTrack(debounce: false);
                // The music card's detail holds the synced lyrics; OpenCard waits for the confirmation card.
                _ctx.OpenCard("music");
                return new CommandResult("\uE8D6", "Lyrics", m.HasSession ? NowPlaying() : "Nothing playing");
        }
        return new CommandResult("\uE783", "Not a music command", IsError: true);
    }

    async Task<CommandResult> PlayOnYouTubeMusicAsync(string query)
    {
        YouTubeMusicSong? song = null;
        try
        {
            song = await _player.SearchAsync(query);
        }
        catch (Exception ex)
        {
            _ctx.Warn("YouTube Music search failed", ex);
        }

        if (song is null)
        {
            _player.Open(YouTubeMusic.SearchPageUrl(query));
            return new CommandResult("\uE721", "Opened YouTube Music search", $"Couldn\u2019t pick a song for \u201C{query}\u201D");
        }

        _lastPlayed = song;
        var how = _player.Open(YouTubeMusic.WatchUrl(song.VideoId));
        _ctx.Info($"Playing a song via {how}");
        var detail = song.Title.Length == 0 ? query : song.Artist.Length == 0 ? song.Title : $"{song.Title} \u00B7 {song.Artist}";
        return new CommandResult("\uE768", "Playing", detail);
    }

    CommandResult PlayOnSpotify(string query)
    {
        // Spotify has no keyless way to resolve a track, so this lands on its search page with the
        // top result one click away.
        try
        {
            Process.Start(new ProcessStartInfo("spotify:search:" + Uri.EscapeDataString(query)) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            _ctx.Warn("Spotify isn't registered for spotify: links; using the web player", ex);
            _ctx.Open("https://open.spotify.com/search/" + Uri.EscapeDataString(query));
        }
        return new CommandResult("\uE8D6", "Opened in Spotify", $"Search for \u201C{query}\u201D \u00B7 pick the top result");
    }

    string NowPlaying()
    {
        var m = Media;
        return m.Artist.Length > 0 ? $"{m.Title} \u00B7 {m.Artist}" : m.Title;
    }

    // ---- Media changes: lyrics and glow ------------------------------------------------------------

    void OnMediaChanged()
    {
        UpdateGlow();
        var m = Media;
        var key = m.HasSession ? LyricsSource.Key(m.Title, m.Artist) : "";
        if (key != _trackKey)
        {
            _trackKey = key;
            LoadLyricsForCurrentTrack(debounce: true);
        }
        _panel.OnMediaChanged();
        WatchForHeard();
        if (_card.IsRelevant != _cardRelevant)
        {
            _cardRelevant = _card.IsRelevant;
            _ctx.CardsChanged();
        }
    }

    /// <summary>Restarts the "heard it" countdown when a new track starts playing.</summary>
    void WatchForHeard()
    {
        var m = Media;
        if (!_settings.RememberSongs || !m.HasSession || !m.IsPlaying || _trackKey == _heardKey) return;
        _heardTimer ??= new DispatcherTimer(HeardAfter, DispatcherPriority.Background, (_, _) => RecordHeard(), _ctx.Ui) { IsEnabled = false };
        if (_heardTimer.IsEnabled && _heardTimer.Tag as string == _trackKey) return;
        _heardTimer.Tag = _trackKey;
        _heardTimer.Stop();
        _heardTimer.Start();
    }

    void RecordHeard()
    {
        _heardTimer!.Stop();
        var m = Media;
        // Still the same track and still playing; nothing is recorded while the screen may be watched.
        if (!_settings.RememberSongs || !m.HasSession || !m.IsPlaying || _heardTimer.Tag as string != _trackKey || PrivacyState.Presenting) return;
        _heardKey = _trackKey;
        if (!_history.Record(m.Title, m.Artist, DateTime.UtcNow)) return;
        _historyNames = _history.Names().ToArray();
        _historyStore.SaveInBackground(_history.Songs);
    }

    void ClearHistory()
    {
        _heardTimer?.Stop();
        _history = new ListeningHistory();
        _historyNames = Array.Empty<string>();
        // The track playing now isn't re-recorded straight away; the next one is (if remembering is on).
        _heardKey = _trackKey;
        _historyStore.Delete();
        _ctx.ShowMessage("", "Song history cleared");
    }

    void LoadLyricsForCurrentTrack(bool debounce)
    {
        var m = Media;
        int version = ++_trackVersion;
        if (!m.HasSession)
        {
            _panel.ShowStatus("\uE8D6", "Nothing playing", "");
            return;
        }
        string title = m.Title, artist = m.Artist, track = NowPlaying();
        if (_lyrics.TryGetCached(title, artist, out var cached))
        {
            _panel.ShowLyrics(cached, track);
            return;
        }
        _panel.ShowStatus("\uE895", "Finding lyrics\u2026", track);
        _ = LoadAsync();

        async Task LoadAsync()
        {
            // Apps often report the title, then the artist and duration a moment later.
            if (debounce) await Task.Delay(700);
            if (version != _trackVersion) return;
            TrackLyrics lyrics;
            try
            {
                lyrics = await _lyrics.GetAsync(title, artist, Media.Duration);
            }
            catch (Exception ex)
            {
                _ctx.Warn("Lyrics lookup failed", ex);
                if (version == _trackVersion) _panel.ShowStatus("\uE8D6", "Couldn\u2019t reach the lyrics service", track);
                return;
            }
            if (version != _trackVersion) return;
            _panel.ShowLyrics(lyrics, track);
            _panel.OnMediaChanged();
        }
    }

    /// <summary>A new song (once its artwork colour is known) flashes the notch edge in that colour.</summary>
    void UpdateGlow()
    {
        var m = Media;
        if (!_settings.AlbumGlow || !m.HasSession || !m.IsPlaying) return;
        var track = NowPlaying();
        if (track == _glowTrack) return;
        // Browser players (YouTube Music runs in Chrome) publish the title first and the artwork a moment
        // later. Wait for it; only if it never comes, flash in the neutral colour.
        if (m.Art is null)
        {
            if (_glowPending != track)
            {
                _glowPending = track;
                _glowFallback ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
                {
                    _glowFallback!.Stop();
                    if (_glowPending is { } pending && pending == NowPlaying() && pending != _glowTrack) Flash(pending, Media.ArtColor);
                }, _ctx.Ui) { IsEnabled = false };
                _glowFallback.Stop();
                _glowFallback.Start();
            }
            return;
        }
        Flash(track, m.ArtColor);
    }

    void Flash(string track, Color color)
    {
        _glowFallback?.Stop();
        _glowPending = null;
        _glowTrack = track;
        _glow = color;
        _ctx.FlashEdge(color);
    }

    // ---- Gestures --------------------------------------------------------------------------------

    void OnWheel(int delta)
    {
        int steps = _wheel.Add(delta);
        if (steps != 0) StepVolume(steps);
    }

    /// <summary>Same feel as the volume keys: 2% steps, snapped to the 2% grid, and turning up unmutes.
    /// Shammy's volume HUD shows by itself because it listens for endpoint volume notifications.</summary>
    float? StepVolume(int steps)
    {
        try
        {
            var v = Device().AudioEndpointVolume;
            if (steps > 0 && v.Mute) v.Mute = false;
            double snapped = Math.Round(v.MasterVolumeLevelScalar * 50) / 50;
            v.MasterVolumeLevelScalar = (float)Math.Clamp(snapped + steps * 0.02, 0, 1);
            return v.MasterVolumeLevelScalar;
        }
        catch (Exception ex)
        {
            _ctx.Warn("Changing volume failed", ex);
            _device?.Dispose();
            _device = null;
            return null;
        }
    }

    MMDevice Device()
    {
        // Re-resolve the default output after a pause in scrolling so switching speakers is followed.
        if (_device is null || DateTime.UtcNow - _deviceUsed > TimeSpan.FromSeconds(2))
        {
            _device?.Dispose();
            _enumerator ??= new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        _deviceUsed = DateTime.UtcNow;
        return _device;
    }

    void OnSwipe(int delta) => Swipe(delta, DateTime.UtcNow);

    int Swipe(int delta, DateTime now)
    {
        int direction = _swipe.Add(delta, now);
        if (direction == 0 || !Media.HasSession) return 0;
        if (direction > 0)
        {
            Media.Next();
            _ctx.ShowMessage("\uE893", "Next track", seconds: 1.4);
        }
        else
        {
            Media.Previous();
            _ctx.ShowMessage("\uE892", "Previous track", seconds: 1.4);
        }
        return direction;
    }

    // ---- Test pipe -------------------------------------------------------------------------------

    string PipeSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "error: usage music-search <query>";
        try
        {
            // The pipe handler runs on the UI thread; SearchAsync never resumes there, so blocking is safe.
            var song = Task.Run(() => _player.SearchAsync(query.Trim())).GetAwaiter().GetResult();
            return song is null ? "none" : $"videoId={song.VideoId} title={song.Title} artist={song.Artist}";
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    /// <summary>Dry run of "play &lt;query&gt;": the song it would pick and the results it chose from, without playing.</summary>
    string PipePick(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "error: usage music-pick <query>";
        query = query.Trim();
        try
        {
            var all = Task.Run(() => _player.SearchAllAsync(query)).GetAwaiter().GetResult();
            if (all.Count == 0) return "none";
            var pick = SongPick.Choose(all, query)!;
            static string Row(YouTubeMusicSong s)
            {
                var marks = SongPick.VariantsIn(s.Title);
                return $"{s.Title} \u00B7 {s.Artist}{(marks.Count > 0 ? $" [{string.Join(",", marks)}]" : "")}";
            }
            return $"pick={pick.Title} \u00B7 {pick.Artist} videoId={pick.VideoId} first={all[0].Title} " +
                   $"results={string.Join(" | ", all.Take(8).Select(Row))}";
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
    }

    string PipeLyrics()
    {
        var m = Media;
        if (!m.HasSession) return "none: nothing playing";
        TrackLyrics lyrics;
        try
        {
            lyrics = Task.Run(() => _lyrics.GetAsync(m.Title, m.Artist, m.Duration)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return "error: " + ex.Message;
        }
        var head = $"track={NowPlaying()} kind={lyrics.Kind}";
        if (lyrics.Kind != LyricsKind.Synced)
            return lyrics.Kind == LyricsKind.Plain ? $"{head} plainLines={lyrics.Plain.Split('\n').Length}" : head;
        int index = Lrc.IndexAt(lyrics.Lines, m.Position + TimeSpan.FromMilliseconds(250));
        var line = index >= 0 ? lyrics.Lines[index].Text : "";
        return $"{head} lines={lyrics.Lines.Count} index={index} position={m.Position:m\\:ss\\.f} line={line}";
    }

    string PipeWheel(string arg)
    {
        if (!int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta)) return "error: usage music-wheel <delta>";
        int steps = _wheel.Add(delta);
        var level = steps != 0 ? StepVolume(steps) : null;
        return $"steps={steps} volume={(level is { } l ? l.ToString("0.00", CultureInfo.InvariantCulture) : "unchanged")}";
    }

    string PipeSwipe(string arg)
    {
        if (!int.TryParse(arg.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta)) return "error: usage music-swipe <delta>";
        int direction = Swipe(delta, DateTime.UtcNow);
        var fired = direction > 0 ? "next" : direction < 0 ? "previous" : "none";
        return $"fired={fired} accumulated={_swipe.Accumulated} session={Media.HasSession}";
    }

    string PipeState()
    {
        var m = Media;
        string volume;
        try { volume = Device().AudioEndpointVolume.MasterVolumeLevelScalar.ToString("0.00", CultureInfo.InvariantCulture); }
        catch (Exception) { volume = "?"; }
        var glow = _glow is { } c ? $"last #{c.R:X2}{c.G:X2}{c.B:X2}" : "none";
        return $"track={(m.HasSession ? NowPlaying() : "-")} playing={m.IsPlaying} glow={glow} glowSetting={_settings.AlbumGlow} " +
               $"lyrics={_panel.Kind} lines={_panel.LineCount} index={_panel.CurrentIndex} ticking={_panel.IsTicking} tabVisible={_panel.IsVisible} layout=[{_panel.DescribeLayout()}] " +
               $"volume={volume} lastPlayed={_lastPlayed?.VideoId ?? "-"} ytmApp={_player.Describe()} heardSongs={_history.Songs.Count} rememberSongs={_settings.RememberSongs}";
    }

    public void Dispose()
    {
        if (_ctx is null) return;
        Media.Changed -= OnMediaChanged;
        _ctx.NotchWheel -= OnWheel;
        _ctx.NotchHorizontalWheel -= OnSwipe;
        _glowFallback?.Stop();
        _heardTimer?.Stop();
        _device?.Dispose();
        _enumerator?.Dispose();
    }
}
