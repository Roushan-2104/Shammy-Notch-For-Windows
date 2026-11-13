using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Cards;
using Shammy.Core.Music;
using Shammy.Infrastructure;
using Shammy.Playback;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Music;

/// <summary>
/// The music card on the Focus Wall (Wall.dc.html): a tile whose field is the album cover's own colours, the
/// state and, dimmed, the title as a poster; focused, the title, artist, a scrubber and play/pause (the primary
/// control) with next. Its detail grows the field into the art panel, with the scrubber, prev/play/next, volume
/// and the synced lyrics (the module's single <see cref="LyricsPanel"/>) in the right-hand column. Players don't
/// expose their queue to Windows, so there's no made-up "Up next".
/// </summary>
internal sealed class MusicCard
{
    readonly ShammyServices _services;
    readonly LyricsPanel _lyrics;

    // Tile parts, built once and updated in place.
    readonly WallTile _tile = new();
    readonly TrackedText _state = CardKit.Label("Now playing", null, 10), _poster = CardKit.Poster("", 24);
    readonly TextBlock _title, _artist;
    readonly TextBlock _elapsed = CardKit.Text("0:00", 10.5, FontWeights.Medium), _total = CardKit.Text("0:00", 10.5, FontWeights.Medium);
    readonly CardTrack _scrub = new() { TrackHeight = 3, KnobSize = 0, Interactive = false, Track = CardKit.Hex("#40F5F5F7") };
    readonly Grid _scrubRow = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly ContentControl _playSlot = new() { Focusable = false };

    // The open detail's live parts (null while it's closed).
    Detail? _detail;

    public MusicCard(ShammyServices services, LyricsPanel lyrics)
    {
        _services = services;
        _lyrics = lyrics;

        // Always: the state, in a dark pill over the cover's colours.
        var pill = Ui.Capsule.Make(new Border { Background = CardKit.Hex("#990B0B0D"), Padding = new Thickness(8, 4, 8, 4), Child = _state });
        _tile.Art.Children.Add(CardKit.At(pill, HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad));

        // Dimmed: the title as a poster.
        _tile.Rest.Children.Add(WallTile.BottomLeft(_poster));

        // Focused: a shade so the words read, title and artist, play/pause and next, the scrubber.
        _tile.Lit.Children.Add(new Border { Height = 96, VerticalAlignment = VerticalAlignment.Bottom, Background = ExpandedView.MusicShade(), IsHitTestVisible = false });
        var words = WallTile.Words("", "", out _title, out _artist);
        _title.FontSize = 16;
        var next = CardKit.Quiet("Next track", CardKit.Glyph(CardKit.Paths.Next, CardKit.Ink, 11), () => Media.Next(), 28);
        next.Margin = new Thickness(5, 0, 0, 0);
        var buttons = CardKit.Row(0, _playSlot, next);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        var top = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(words);
        _elapsed.Foreground = _total.Foreground = Ui.Tokens.InkOnArt;
        _scrubRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _scrubRow.ColumnDefinitions.Add(new ColumnDefinition());
        _scrubRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _scrub.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(_scrub, 1);
        Grid.SetColumn(_total, 2);
        _scrubRow.Children.Add(_elapsed);
        _scrubRow.Children.Add(_scrub);
        _scrubRow.Children.Add(_total);
        var bottom = CardKit.Column(0, top, _scrubRow);
        bottom.VerticalAlignment = VerticalAlignment.Bottom;
        bottom.Margin = new Thickness(WallTile.Pad, 0, 10, 10);
        _tile.Lit.Children.Add(bottom);

        // The scrubber only moves while someone can see it.
        CardKit.TickWhileVisible(_tile, TimeSpan.FromSeconds(1), UpdateTime);
        Media.Changed += Refresh;
        Refresh();
    }

    MediaService Media => _services.Media;

    public FrameworkElement Compact => _tile;

    public bool IsRelevant => Media.HasSession;

    /// <summary>A song playing is live (it wins the focus over the weather); a paused one barely.</summary>
    public int Live => Media.IsPlaying ? 50 : Media.HasSession ? 5 : 0;

    /// <summary>The cover itself when the player gives one; otherwise the plum and rust field.</summary>
    Brush Field => Media.Art is { } art ? Fields.Cover(art) : Fields.Music(ArtPalette.Field(null));

    string State => Media.IsPlaying ? "Now playing" : Media.HasSession ? "Paused" : "Nothing playing";

    /// <summary>The peek: "Now playing · Kesariya · Arijit Singh · 1:58 / 4:28", with play/pause.</summary>
    public WallPeek? Peek()
    {
        var m = Media;
        if (!m.HasSession) return null;
        var line = m.Artist;
        if (MediaTime.HasTimeline(m.Duration))
            line = (line.Length > 0 ? line + " · " : "") + $"{MediaTime.Clock(m.Position)} / {MediaTime.Clock(m.Duration)}";
        return new WallPeek(State, m.Title, line, Field, new WallAction(m.IsPlaying ? $"Pause {m.Title}" : $"Play {m.Title}", m.TogglePlayPause, Stays: true, Playing: m.IsPlaying));
    }

    void Refresh()
    {
        var m = Media;
        _tile.Field = Field;
        _state.Text = State;
        _poster.Text = m.HasSession ? m.Title : "Nothing playing";
        _title.Text = m.HasSession ? m.Title : "Nothing playing";
        _artist.Text = m.Artist;
        _playSlot.Content = PlayButton(28, 11);
        _scrubRow.Visibility = MediaTime.HasTimeline(m.Duration) ? Visibility.Visible : Visibility.Collapsed;
        CardKit.Name(_tile, m.HasSession ? $"Music: {m.Title}{(m.Artist.Length > 0 ? " by " + m.Artist : "")}, {State.ToLowerInvariant()}" : "Music, nothing playing");
        UpdateTime();
        _detail?.Refresh();
    }

    void UpdateTime()
    {
        var m = Media;
        if (!MediaTime.HasTimeline(m.Duration)) return;
        var pos = m.Position;
        _elapsed.Text = MediaTime.Clock(pos);
        _total.Text = MediaTime.Clock(m.Duration);
        _scrub.Value = CardMath.Fraction(pos.TotalSeconds, m.Duration.TotalSeconds);
    }

    /// <summary>The primary control: play or pause.</summary>
    Button PlayButton(double size, double icon)
    {
        bool playing = Media.IsPlaying;
        return CardKit.Primary(playing ? "Pause" : "Play", CardKit.PlayPause(playing, icon), () => Media.TogglePlayPause(), size);
    }

    // ---- Detail ---------------------------------------------------------------------------------

    public FrameworkElement BuildDetail()
    {
        var detail = new Detail(this);
        _detail = detail;
        detail.Root.Loaded += (_, _) => _detail = detail;
        detail.Root.Unloaded += (_, _) =>
        {
            if (_detail == detail) _detail = null;
            detail.Close();
        };
        return detail.Root;
    }

    /// <summary>One open detail. Its lyrics panel is the module's single instance, moved in on open.</summary>
    sealed class Detail
    {
        readonly MusicCard _card;
        readonly TrackedText _poster = CardKit.Poster("", 44), _sub = CardKit.Label("");
        readonly TextBlock _elapsed = CardKit.Text("", 12, FontWeights.Medium, secondary: true), _left = CardKit.Text("", 12, FontWeights.Medium, secondary: true);
        readonly CardTrack _scrub = new() { TrackHeight = 4, KnobSize = 10 };
        readonly CardTrack _volume = new() { TrackHeight = 4, KnobSize = 0 };
        readonly Grid _timeline = new();
        readonly ContentControl _playSlot = new() { Focusable = false };
        readonly Border _lyricsHost = new();
        readonly TrackedText _synced = CardKit.Label("Synced", CardKit.Ink2, 10);
        bool _attached;

        public Detail(MusicCard card)
        {
            _card = card;
            var m = card.Media;
            Root = new WallDetail(card.Field, "", "", "");
            _sub.Margin = new Thickness(0, 8, 0, 0);
            _poster.MaxWidth = 264;
            Root.ArtBottomLeft(_poster, _sub);

            _scrub.Committed += f =>
            {
                var media = card.Media;
                if (media.CanSeek && MediaTime.HasTimeline(media.Duration)) media.Seek(TimeSpan.FromSeconds(f * media.Duration.TotalSeconds));
            };
            _volume.Changed += v => card._services.Volume?.SetLevel((float)v);
            _volume.Width = 72;
            _volume.VerticalAlignment = VerticalAlignment.Center;

            // Scrubber: elapsed, track, remaining.
            _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _timeline.ColumnDefinitions.Add(new ColumnDefinition());
            _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _scrub.Margin = new Thickness(10, 0, 10, 0);
            Grid.SetColumn(_scrub, 1);
            Grid.SetColumn(_left, 2);
            _timeline.Children.Add(_elapsed);
            _timeline.Children.Add(_scrub);
            _timeline.Children.Add(_left);
            _timeline.VerticalAlignment = VerticalAlignment.Top;

            // Transport: prev, play/pause (the primary), next; volume on the right.
            var prev = CardKit.Quiet("Previous track", CardKit.Glyph(CardKit.Paths.Previous, CardKit.Ink, 13), () => card.Media.Previous(), 36);
            var next = CardKit.Quiet("Next track", CardKit.Glyph(CardKit.Paths.Next, CardKit.Ink, 13), () => card.Media.Next(), 36);
            var transport = CardKit.Row(10, prev, _playSlot, next);
            transport.VerticalAlignment = VerticalAlignment.Center;
            var volume = CardKit.Row(8, CardKit.Icon(CardKit.Paths.Volume, CardKit.Ink2, 15), _volume);
            volume.VerticalAlignment = VerticalAlignment.Center;
            volume.ToolTip = "Volume";
            var controls = new DockPanel { VerticalAlignment = VerticalAlignment.Bottom };
            DockPanel.SetDock(volume, Dock.Right);
            controls.Children.Add(volume);
            controls.Children.Add(transport);
            Root.Body.Children.Add(_timeline);
            Root.Body.Children.Add(controls);

            // Lyrics: the module's own panel, synced and clickable to seek, the full height on the right.
            var head = new DockPanel();
            DockPanel.SetDock(_synced, Dock.Right);
            head.Children.Add(_synced);
            head.Children.Add(CardKit.Label("Lyrics", CardKit.Ink2));
            var lyrics = new DockPanel { Width = 232 };
            DockPanel.SetDock(head, Dock.Top);
            lyrics.Children.Add(head);
            lyrics.Children.Add(_lyricsHost);
            Root.Aside = lyrics;
            Attach();

            CardKit.TickWhileVisible(Root, TimeSpan.FromSeconds(1), Tick);
            Root.Loaded += (_, _) => Attach();
            Refresh();
        }

        public WallDetail Root { get; }

        public void Refresh()
        {
            var m = _card.Media;
            Root.Field = _card.Field;
            Root.Kicker.Text = _card.State;
            Root.TitleText.Text = m.HasSession ? m.Title : "Nothing playing";
            Root.Line.Text = m.Artist;
            _poster.Text = m.HasSession ? m.Title : "Music";
            _sub.Text = m.Artist.Length > 0 ? m.Artist : _card.State;
            _playSlot.Content = CardKit.Primary(m.IsPlaying ? "Pause" : "Play", CardKit.PlayPause(m.IsPlaying, 16), () => _card.Media.TogglePlayPause(), 46);
            _timeline.Visibility = MediaTime.HasTimeline(m.Duration) ? Visibility.Visible : Visibility.Hidden;
            _scrub.IsHitTestVisible = m.CanSeek;
            _scrub.Cursor = m.CanSeek ? System.Windows.Input.Cursors.Hand : null;
            Tick();
        }

        void Tick()
        {
            var m = _card.Media;
            if (MediaTime.HasTimeline(m.Duration))
            {
                var pos = m.Position;
                _elapsed.Text = MediaTime.Clock(pos);
                _left.Text = MediaTime.Remaining(pos, m.Duration);
                _scrub.Value = CardMath.Fraction(pos.TotalSeconds, m.Duration.TotalSeconds);
            }
            if (_card._services.Volume is { } v && !_volume.IsDragging) _volume.Value = v.Muted ? 0 : v.Level;
        }

        void OnVolume(float level, bool muted)
        {
            if (!_volume.IsDragging) _volume.Value = muted ? 0 : level;
        }

        void OnLyricsChanged() => _synced.Visibility = _card._lyrics.Kind == LyricsKind.Synced ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Moves the lyrics panel in and listens for lyrics and volume; undone by <see cref="Close"/>.</summary>
        void Attach()
        {
            if (_attached) return;
            _attached = true;
            CardKit.Detach(_card._lyrics);
            _card._lyrics.ShowTrack = false;
            _lyricsHost.Child = _card._lyrics;
            _card._lyrics.LyricsChanged += OnLyricsChanged;
            if (_card._services.Volume is { } v) v.Changed += OnVolume;
            OnLyricsChanged();
        }

        public void Close()
        {
            if (!_attached) return;
            _attached = false;
            _card._lyrics.LyricsChanged -= OnLyricsChanged;
            if (_card._services.Volume is { } v) v.Changed -= OnVolume;
            // Let the next detail (or nothing) own the panel; it stops ticking once it's off screen.
            if (_lyricsHost.Child == _card._lyrics) _lyricsHost.Child = null;
        }
    }

    // ---- Test pipe --------------------------------------------------------------------------------

    public string DescribeCompact()
    {
        var m = Media;
        if (!m.HasSession) return "music: nothing playing (card not relevant)";
        var timeline = MediaTime.HasTimeline(m.Duration) ? $"{MediaTime.Clock(m.Position)}/{MediaTime.Clock(m.Duration)}" : "hidden";
        var f = ArtPalette.Field(m.Art);
        return $"music: status={(m.IsPlaying ? "playing" : "paused")} title={m.Title} artist={m.Artist} art={(m.Art is null ? "none" : "yes")} " +
               $"field={f.Glow1.Hex},{f.Glow2.Hex},{f.Base2.Hex} scrubber={timeline} focused={_tile.IsLit} " +
               $"buttons={(m.IsPlaying ? "pause" : "play")},next visible={_tile.IsVisible}";
    }

    public string DescribeDetail()
    {
        var m = Media;
        if (!m.HasSession) return "music detail: nothing playing";
        var timeline = MediaTime.HasTimeline(m.Duration) ? $"{MediaTime.Clock(m.Position)} {MediaTime.Remaining(m.Position, m.Duration)} seek={m.CanSeek}" : "hidden";
        var volume = _services.Volume is { } v ? $"{(int)Math.Round(v.Level * 100)}%{(v.Muted ? " muted" : "")}" : "unavailable";
        var lyric = _lyrics.Kind == LyricsKind.Synced ? $" now=\"{_lyrics.CurrentLine}\"" : "";
        return $"music detail: title={m.Title} artist={m.Artist} timeline={timeline} lyrics={_lyrics.Kind} lines={_lyrics.LineCount}{lyric} " +
               $"upNext=omitted(no queue from Windows) volume={volume} open={(_detail is null ? "no" : "yes")}";
    }
}
