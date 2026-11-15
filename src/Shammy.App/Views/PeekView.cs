using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The hover peek (440×68, Wall.dc.html): the rail's focused tile in miniature — its artwork swatch, a tracked
/// kicker, the title and one line — and that tile's primary control in the accent. Play/pause and other in-place
/// actions keep the peek; anything that goes somewhere (Join, details) closes it. Clicking the rest opens the
/// notch. With no tile to show, it falls back to the time and the most important glance.
/// </summary>
internal sealed class PeekView : Grid
{
    public const double PeekWidth = 440, PeekHeight = 68;

    readonly ShammyServices _services;
    readonly GlanceFeed _feed;
    readonly Border _swatch;
    readonly TrackedText _kicker;
    readonly TextBlock _title, _line;
    readonly ContentControl _primary = new() { Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    string _primaryText = "-", _cardId = "-";

    public PeekView(ShammyServices services, GlanceFeed feed)
    {
        _services = services;
        _feed = feed;
        Width = PeekWidth;
        Height = PeekHeight;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _swatch = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(11),
            BorderBrush = CardKit.Hairline,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Children.Add(_swatch);

        _kicker = CardKit.Label("", CardKit.Ink2);
        _kicker.HorizontalAlignment = HorizontalAlignment.Left;
        _title = CardKit.Text("", 17, FontWeights.Bold);
        _line = CardKit.Text("", 12, FontWeights.Medium, secondary: true);
        _title.Margin = new Thickness(0, 2, 0, 0);
        _line.Margin = new Thickness(0, 2, 0, 0);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(13, 0, 12, 0) };
        words.Children.Add(_kicker);
        words.Children.Add(_title);
        words.Children.Add(_line);
        SetColumn(words, 1);
        Children.Add(words);

        _primary.Margin = new Thickness(0, 0, 14, 0);
        SetColumn(_primary, 2);
        Children.Add(_primary);
    }

    /// <summary>
    /// The primary control was clicked with an action that goes somewhere: the notch closes the peek and runs it.
    /// (A click anywhere else on the peek opens the notch; the notch window handles that.)
    /// </summary>
    public event Action<GlanceItem?>? GlanceClicked;

    public string Describe() =>
        $"card={_cardId} kicker={_kicker.Text} title={_title.Text} subtitle={_line.Text} pill={_primaryText}";

    /// <summary>The open notch whose focused tile this shows (found through the window when not set).</summary>
    internal ExpandedView? Wall
    {
        get => _wall ?? (Window.GetWindow(this) is Shell.NotchWindow w ? w.ExpandedView : null);
        set => _wall = value;
    }

    ExpandedView? _wall;

    /// <summary>Fresh content each time the peek opens (and when the song changes while it's open).</summary>
    public void Refresh()
    {
        WallPeek? peek = null;
        try
        {
            if (Wall?.PeekCard() is { Peek: { } make } card)
            {
                peek = make();
                _cardId = card.Id;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Building the peek failed", ex);
        }
        if (peek is null)
        {
            ShowFallback();
            return;
        }
        _swatch.Background = peek.Swatch;
        // The card's icon on its colour field (a bare field reads as an empty box).
        var glyph = ExpandedView.CardGlyph(_cardId);
        _swatch.Child = glyph.Length > 0 ? Centered(CardKit.FluentGlyph(glyph, 19)) : null;
        _kicker.Text = peek.Kicker;
        _title.Text = peek.Title;
        _line.Text = peek.Line;
        _primary.Content = peek.Primary is { } p ? Primary(p) : null;
        _primaryText = peek.Primary?.Label ?? "-";
        CardKit.Name(this, $"Open Shammy. {peek.Kicker}: {peek.Title}, {peek.Line}");
    }

    static FrameworkElement Centered(FrameworkElement e)
    {
        e.HorizontalAlignment = HorizontalAlignment.Center;
        e.VerticalAlignment = VerticalAlignment.Center;
        return e;
    }

    FrameworkElement Primary(WallAction a)
    {
        void Run()
        {
            if (a.Stays)
            {
                try { a.Run(); }
                catch (Exception ex) { Log.Warn("Peek action failed", ex); }
                // Media and timers report the change themselves; refresh once now so the glyph flips at once.
                Dispatcher.BeginInvoke(Refresh, System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            GlanceClicked?.Invoke(new GlanceItem(a.Label, NotchPalette.Music, OnClick: a.Run));
        }
        return a.Playing is { } playing
            ? CardKit.Primary(a.Label, CardKit.PlayPause(playing, 14), Run, 38)
            : CardKit.PrimaryPill(a.Label, a.Label, Run, 34, 13);
    }

    /// <summary>Nothing on the rail: the time and date, and the most important glance as the primary pill.</summary>
    void ShowFallback()
    {
        _cardId = "-";
        var now = DateTime.Now;
        _swatch.Background = Fields.Calendar();
        _swatch.Child = null;
        _kicker.Text = now.ToString("dddd", CultureInfo.CurrentCulture);
        _title.Text = BuiltInGlances.Time(now, Shammy.Core.Productivity.ClockPreference.Is24Hour).Time;
        _line.Text = now.ToString("d MMMM", CultureInfo.CurrentCulture);
        var top = _feed.Top(_services.Media.HasSession ? _services.Media.Title : "");
        if (top is null)
        {
            _primary.Content = null;
            _primaryText = "-";
            return;
        }
        var item = top.Item;
        _primaryText = item.Text;
        var pill = CardKit.PrimaryPill(item.Text, item.Text, () => GlanceClicked?.Invoke(item), 34, 13);
        pill.MaxWidth = 190;
        _primary.Content = pill;
    }
}
