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

    /// <summary>
    /// One open Music card (the card family's shape, 760 wide): the cover as it is (the island's waves carry its
    /// colours), the title, artist, timeline and transport with play/pause the one accent, volume, and three lines of
    /// lyrics beside it. Its lyrics panel is the module's single instance, moved in on open.
    /// </summary>
    sealed class Detail
    {
        public const double SheetWidth = 760, CoverSize = 150;

        readonly MusicCard _card;
        readonly TextBlock _title = Sheet.Word("", 26), _artist = Sheet.Meta("", 14);
        readonly TextBlock _elapsed = Sheet.Num("", 13, CardKit.Ink2), _left = Sheet.Num("", 13, CardKit.Ink2);
        readonly CardTrack _scrub = new() { TrackHeight = 5, KnobSize = 10, Fill = Ink.Gradient, Track = Sheet.Track };
        readonly CardTrack _volume = new() { TrackHeight = 4, KnobSize = 0, Fill = CardKit.Ink, Track = Sheet.Track };
        readonly Grid _timeline = new() { Margin = new Thickness(0, 12, 0, 0) };
        readonly ContentControl _playSlot = new() { Focusable = false };
        readonly Border _cover = new() { Width = CoverSize, Height = CoverSize, CornerRadius = new CornerRadius(22), ClipToBounds = true };
        readonly Border _lyricsHost = new() { Height = CoverSize };
        bool _attached;

        public Detail(MusicCard card)
        {
            _card = card;
            Root = new CardSheet("Music", SheetWidth);
            _title.Margin = new Thickness(0, 0, 0, 2);

            _scrub.Committed += f =>
            {
                var media = card.Media;
                if (media.CanSeek && MediaTime.HasTimeline(media.Duration)) media.Seek(TimeSpan.FromSeconds(f * media.Duration.TotalSeconds));
            };
            _volume.Changed += v => card._services.Volume?.SetLevel((float)v);
            _volume.Width = 72;
            _volume.VerticalAlignment = VerticalAlignment.Center;

            // Timeline: elapsed, track, remaining.
            _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _timeline.ColumnDefinitions.Add(new ColumnDefinition());
            _timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _scrub.Margin = new Thickness(10, 0, 10, 0);
            _scrub.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_scrub, 1);
            Grid.SetColumn(_left, 2);
            _timeline.Children.Add(_elapsed);
            _timeline.Children.Add(_scrub);
            _timeline.Children.Add(_left);

            // Transport: prev, play/pause (the one accent), next; volume on the right.
            var prev = Sheet.Round("Previous track", CardKit.Paths.Previous, () => card.Media.Previous(), 38, iconSize: 13, filled: true);
            var next = Sheet.Round("Next track", CardKit.Paths.Next, () => card.Media.Next(), 38, iconSize: 13, filled: true);
            var transport = Sheet.Row(10, prev, _playSlot, next);
            var volume = Sheet.Row(8, CardKit.Icon(CardKit.Paths.Volume, CardKit.Ink2, 15), _volume);
            volume.ToolTip = "Volume";
            var controls = Sheet.Spread(transport, volume);
            controls.Margin = new Thickness(0, 12, 0, 0);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(_title);
            info.Children.Add(_artist);
            info.Children.Add(_timeline);
            info.Children.Add(controls);

            // Lyrics: the module's own panel (synced, click a line to seek), beside the controls.
            _lyricsHost.Width = 210;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(_cover);
            info.Margin = new Thickness(20, 0, 20, 0);
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            Grid.SetColumn(_lyricsHost, 2);
            row.Children.Add(_lyricsHost);
            Root.Body.Children.Add(row);
            Attach();

            CardKit.TickWhileVisible(Root, TimeSpan.FromSeconds(1), Tick);
            Root.Loaded += (_, _) => Attach();
            Refresh();
        }

        public CardSheet Root { get; }

        public void Refresh()
        {
            var m = _card.Media;
            Root.Status = _card.State.ToLowerInvariant();
            _title.Text = m.HasSession ? m.Title : "Nothing playing";
            _artist.Text = m.Artist;
            _artist.Visibility = m.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (m.Art is { } art)
            {
                _cover.Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
                _cover.Child = null;
            }
            else
            {
                _cover.Background = Ink.Tint;
                _cover.Child = CardKit.Icon("M9 18V6l10-2v12 M9 18a3 3 0 1 1-6 0a3 3 0 1 1 6 0z M19 16a3 3 0 1 1-6 0a3 3 0 1 1 6 0z", Ink.Soft, 40, 1.8);
            }
            _playSlot.Content = Sheet.Round(m.IsPlaying ? "Pause" : "Play", m.IsPlaying ? CardKit.Paths.Pause : CardKit.Paths.Play,
                () => _card.Media.TogglePlayPause(), 50, accent: true, iconSize: 17, filled: true);
            _timeline.Visibility = MediaTime.HasTimeline(m.Duration) ? Visibility.Visible : Visibility.Collapsed;
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

        /// <summary>Moves the lyrics panel in and listens for volume; undone by <see cref="Close"/>.</summary>
        void Attach()
        {
            if (_attached) return;
            _attached = true;
            CardKit.Detach(_card._lyrics);
            _card._lyrics.ShowTrack = false;
            _lyricsHost.Child = _card._lyrics;
            if (_card._services.Volume is { } v) v.Changed += OnVolume;
        }

        public void Close()
        {
            if (!_attached) return;
            _attached = false;
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
