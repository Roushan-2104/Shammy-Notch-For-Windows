using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Music;
using Shammy.Motion;
using Shammy.Playback;

namespace Shammy.Modules.Music;

/// <summary>
/// The synced lyrics in the music detail (Focus Wall: Schibsted Grotesk, white on the ground, the current line
/// bright and the rest fading — no accent): synced lines scroll on a spring so the current one sits a little above centre,
/// bright, with its neighbours fading by distance. Plain lyrics get a scrollable text block instead.
/// Frames only run while the tab is on screen and the track is playing.
/// </summary>
internal sealed class LyricsPanel : Grid
{
    // Lines are timed to when singing starts; showing them a beat early reads as "in sync".
    static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(250);
    const double FocusFraction = 0.42;

    readonly MediaService _media;
    readonly TextBlock _track;
    readonly Grid _viewport;
    readonly StackPanel _lines;
    readonly TranslateTransform _scroll = new();
    readonly ScrollViewer _plainScroll;
    readonly TextBlock _plain;
    readonly StackPanel _status;
    readonly TextBlock _statusGlyph, _statusText;
    readonly SpringValue _y = new(0, 0.3);

    TrackLyrics _lyrics = TrackLyrics.None;
    double[] _centers = Array.Empty<double>();
    int _index = int.MinValue;
    IDisposable? _ticking;
    bool _frameHooked, _stale;

    public LyricsPanel(MediaService media)
    {
        _media = media;
        Margin = new Thickness(0, 6, 0, 0);
        ClipToBounds = true;
        System.Windows.Documents.TextElement.SetFontFamily(this, Shammy.Views.Cards.CardKit.Font);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _track = new TextBlock
        {
            FontSize = 11,
            Foreground = Shammy.Views.Cards.CardKit.Ink2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2),
        };
        Children.Add(_track);

        // Fade lines out towards the top and bottom edges instead of hard-clipping them.
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.22));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.78));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        fade.Freeze();

        _lines = new StackPanel { RenderTransform = _scroll, VerticalAlignment = VerticalAlignment.Top };
        _lines.SizeChanged += (_, _) => MeasureLines();
        _viewport = new Grid { ClipToBounds = true, OpacityMask = fade, Background = Brushes.Transparent };
        _viewport.Children.Add(_lines);
        _viewport.SizeChanged += (_, _) => Sync(snap: true);
        SetRow(_viewport, 1);
        Children.Add(_viewport);

        _plain = new TextBlock
        {
            FontSize = 13.5,
            LineHeight = 20,
            Foreground = Shammy.Views.Cards.CardKit.Ink2,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 8, 12),
        };
        _plainScroll = new ScrollViewer
        {
            Content = _plain,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Visibility = Visibility.Collapsed,
        };
        SetRow(_plainScroll, 1);
        Children.Add(_plainScroll);

        _statusGlyph = new TextBlock { FontFamily = Ui.Tokens.FontRes("IconFont"), FontSize = 20, Foreground = Shammy.Views.Cards.CardKit.Ink2, HorizontalAlignment = HorizontalAlignment.Center };
        _statusText = new TextBlock { FontSize = 12.5, Foreground = Shammy.Views.Cards.CardKit.Ink2, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        _status = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        _status.Children.Add(_statusGlyph);
        _status.Children.Add(_statusText);
        SetRow(_status, 1);
        Children.Add(_status);

        // While the screen is shared the words stay off it (a cover on top; the lines keep their place).
        _hidden = new TextBlock
        {
            Text = Shammy.Core.Connect.PrivacyText.Hidden,
            FontSize = 12.5,
            Foreground = Shammy.Views.Cards.CardKit.Ink2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        SetRow(_hidden, 0);
        SetRowSpan(_hidden, 2);
        Children.Add(_hidden);
        Action<bool> privacy = _ => Dispatcher.BeginInvoke(ApplyPrivacy);
        Loaded += (_, _) =>
        {
            Infrastructure.PrivacyState.Changed += privacy;
            ApplyPrivacy();
        };
        Unloaded += (_, _) => Infrastructure.PrivacyState.Changed -= privacy;

        IsVisibleChanged += (_, _) => UpdateTicking();
        ShowStatus("\uE8D6", "Nothing playing", "");
    }

    readonly TextBlock _hidden;

    void ApplyPrivacy()
    {
        bool hide = Infrastructure.PrivacyState.Presenting;
        _hidden.Visibility = hide ? Visibility.Visible : Visibility.Collapsed;
        double shown = hide ? 0 : 1;
        _track.Opacity = _viewport.Opacity = _plainScroll.Opacity = _status.Opacity = shown;
        _viewport.IsHitTestVisible = _plainScroll.IsHitTestVisible = !hide;
    }

    public LyricsKind Kind => _lyrics.Kind;
    public int LineCount => _lyrics.Lines.Count;
    public int CurrentIndex => _lyrics.Kind == LyricsKind.Synced ? Lrc.IndexAt(_lyrics.Lines, _media.Position + Lead) : -1;
    public string CurrentLine => CurrentIndex >= 0 && CurrentIndex < _lyrics.Lines.Count ? _lyrics.Lines[CurrentIndex].Text : "";
    public bool IsTicking => _ticking is not null;

    /// <summary>The track line above the lyrics; the music card's detail already shows the title, so it hides it.</summary>
    public bool ShowTrack
    {
        set => _track.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Raised when the lyrics shown change (new track, found, not found), so a "Synced" badge can follow.</summary>
    public event Action? LyricsChanged;

    public void ShowStatus(string glyph, string text, string track)
    {
        _lyrics = TrackLyrics.None;
        _lines.Children.Clear();
        _centers = Array.Empty<double>();
        _index = int.MinValue;
        _track.Text = track;
        _statusGlyph.Text = glyph;
        _statusText.Text = text;
        _status.Visibility = Visibility.Visible;
        _viewport.Visibility = Visibility.Collapsed;
        _plainScroll.Visibility = Visibility.Collapsed;
        UpdateTicking();
        LyricsChanged?.Invoke();
    }

    public void ShowLyrics(TrackLyrics lyrics, string track)
    {
        switch (lyrics.Kind)
        {
            case LyricsKind.None:
                ShowStatus("\uE8D6", "No lyrics found", track);
                return;
            case LyricsKind.Instrumental:
                ShowStatus("\uE8D6", "Instrumental", track);
                return;
        }

        _lyrics = lyrics;
        _track.Text = track;
        _status.Visibility = Visibility.Collapsed;
        _index = int.MinValue;
        _lines.Children.Clear();
        _centers = Array.Empty<double>();

        if (lyrics.Kind == LyricsKind.Plain)
        {
            _plain.Text = lyrics.Plain;
            _plainScroll.ScrollToTop();
            _plainScroll.Visibility = Visibility.Visible;
            _viewport.Visibility = Visibility.Collapsed;
            UpdateTicking();
            LyricsChanged?.Invoke();
            return;
        }

        _plainScroll.Visibility = Visibility.Collapsed;
        _viewport.Visibility = Visibility.Visible;
        var fg = Shammy.Views.Cards.CardKit.Ink;
        var display = Shammy.Views.Cards.CardKit.Font;
        foreach (var line in lyrics.Lines)
        {
            var tb = new TextBlock
            {
                // An empty timed line is an instrumental break.
                Text = line.Text.Length > 0 ? line.Text : "\u266A",
                FontFamily = display,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0, 3, 0, 3),
                Opacity = 0.2,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(0.88, 0.88),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = line.Time,
            };
            tb.MouseLeftButtonUp += OnLineClicked;
            _lines.Children.Add(tb);
        }
        // SizeChanged only fires if the new lines lay out to a different total height. The same song
        // again (or one that happens to wrap to the same height) would otherwise never get its line
        // positions, and the lyrics would sit there frozen and dim.
        Dispatcher.BeginInvoke(MeasureLines, System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateTicking();
        LyricsChanged?.Invoke();
    }

    void OnLineClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TimeSpan t } && _media.CanSeek)
        {
            _media.Seek(t);
            e.Handled = true;
        }
    }

    /// <summary>Called on every media change (play/pause, seek, new track).</summary>
    public void OnMediaChanged()
    {
        UpdateTicking();
        Sync(snap: false);
    }

    void UpdateTicking()
    {
        bool visible = IsVisible;
        bool synced = _lyrics.Kind == LyricsKind.Synced;

        // Apply spring values whenever frames happen to run; this alone never keeps the clock alive.
        bool hook = visible && synced;
        if (hook != _frameHooked)
        {
            _frameHooked = hook;
            if (hook) Animator.Frame += OnFrame;
            else Animator.Frame -= OnFrame;
        }

        bool tick = hook && _media.IsPlaying;
        if (tick && _ticking is null)
        {
            _ticking = Animator.Subscribe(_ => Sync(snap: false));
            Sync(snap: true);
        }
        else if (!tick && _ticking is not null)
        {
            _ticking.Dispose();
            _ticking = null;
        }
        if (hook && !tick) Sync(snap: true);
    }

    void MeasureLines()
    {
        var centers = new double[_lines.Children.Count];
        double y = 0;
        for (int i = 0; i < centers.Length; i++)
        {
            double h = ((FrameworkElement)_lines.Children[i]).ActualHeight;
            centers[i] = y + h / 2;
            y += h;
        }
        _centers = centers;
        Sync(snap: true);
    }

    void Sync(bool snap)
    {
        if (_lyrics.Kind != LyricsKind.Synced || _centers.Length == 0 || _centers.Length != _lyrics.Lines.Count) return;
        int index = Lrc.IndexAt(_lyrics.Lines, _media.Position + Lead);
        if (!IsVisible)
        {
            // Nobody is watching: remember where we are and jump straight there when the tab opens.
            _index = index;
            _stale = true;
            return;
        }
        // snap = layout or visibility changed, so retarget even when the line is the same.
        if (index == _index && !snap && !_stale) return;
        bool jump = _stale || _index == int.MinValue;
        _stale = false;
        _index = index;

        double focus = _viewport.ActualHeight * FocusFraction;
        // Before the first line, park it just below the focus point.
        double center = index >= 0 ? _centers[index] : _centers[0] - 34;
        double target = focus - center;
        if (jump) _y.Snap(target);
        else _y.AnimateTo(target, response: 0.55, damping: 0.86);
        Apply();
    }

    void OnFrame(double dt) => Apply();

    void Apply()
    {
        if (_centers.Length != _lines.Children.Count) return;
        double y = _y.Value;
        _scroll.Y = y;
        double focus = _viewport.ActualHeight * FocusFraction;
        const double lineHeight = 26;
        for (int i = 0; i < _centers.Length; i++)
        {
            var el = (FrameworkElement)_lines.Children[i];
            double dist = Math.Abs(_centers[i] + y - focus) / lineHeight;
            if (dist > 4)
            {
                if (el.Opacity != 0.12) el.Opacity = 0.12;
                continue;
            }
            el.Opacity = 0.16 + 0.84 * Math.Exp(-dist * 1.15);
            double s = 0.88 + 0.12 * Math.Exp(-dist * 1.8);
            var scale = (ScaleTransform)el.RenderTransform;
            scale.ScaleX = scale.ScaleY = s;
        }
    }

}
