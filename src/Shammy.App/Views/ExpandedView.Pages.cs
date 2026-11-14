using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shammy.Core.Notch;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Modules.Wheel;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The open island's two pages: the home (the now moment and six tools), and, one slide to the right, everything
/// Shammy does, so nothing needs searching: the features you use most as big tiles, then every other one A to Z.
/// Slide with a sideways swipe or Shift + scroll over the open island, the two dots at the bottom, or the arrow
/// keys. The page ranks by the same uses the wheel learns from (<see cref="Essentials"/>), and what you open here
/// counts too. Closing the island brings it back to the home.
/// </summary>
public partial class ExpandedView
{
    const double BigTileW = 108, BigTileH = 80, SmallSlot = 80, SmallCircle = 40;

    int _page; // 0 = home, 1 = everything
    readonly PageSwipe _pageSwipe = new();
    readonly List<Button> _essentialButtons = new();
    readonly List<Essential> _essentialShown = new();
    Button? _dotHome, _dotAll;
    Border? _dotHomeMark, _dotAllMark;

    void InitPages()
    {
        (_dotHome, _dotHomeMark) = Dot("Home page", () => GoToPage(0));
        (_dotAll, _dotAllMark) = Dot("Everything page", () => GoToPage(1));
        PageDots.Children.Add(_dotHome);
        PageDots.Children.Add(_dotAll);
        // The island opens on the home again next time.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue || _page == 0) return;
            _page = 0;
            ApplyPage(animate: false);
        };
        UpdateDots();
    }

    static (Button, Border) Dot(string name, Action go)
    {
        var mark = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), IsHitTestVisible = false };
        return (CardKit.Bare(name, mark, go, 18, 14), mark);
    }

    void UpdateDots()
    {
        if (_dotHomeMark is null || _dotAllMark is null) return;
        _dotHomeMark.Background = _page == 0 ? CardKit.Ink : CardKit.Hex("#4DFFFFFF");
        _dotAllMark.Background = _page == 1 ? CardKit.Ink : CardKit.Hex("#4DFFFFFF");
        _dotHomeMark.Width = _page == 0 ? 14 : 6;
        _dotAllMark.Width = _page == 1 ? 14 : 6;
    }

    /// <summary>Which page shows: 0 the home, 1 everything.</summary>
    internal int Page => _page;

    /// <summary>
    /// A sideways scroll over the open island. On the home level it slides the pages (one page per swipe) and is
    /// used up; with a card open it isn't, so it can still skip tracks.
    /// </summary>
    internal bool SwipePages(int delta)
    {
        if (_openId is not null) return false;
        int direction = _pageSwipe.Add(delta, DateTime.UtcNow);
        if (direction != 0) GoToPage(_page + direction);
        return true;
    }

    internal void GoToPage(int page)
    {
        page = Math.Clamp(page, 0, 1);
        if (_openId is not null || page == _page) return;
        int direction = page > _page ? 1 : -1;
        _page = page;
        ApplyPage(animate: true, direction);
    }

    /// <summary>Shows the current page (on the home level), or hides both pages' extras while a card is open.</summary>
    void ApplyPage(bool animate, int direction = 1)
    {
        if (_openId is not null)
        {
            EssentialsPanel.Visibility = Visibility.Collapsed;
            PageDots.Visibility = Visibility.Collapsed;
            return;
        }
        bool all = _page == 1;
        if (all) BuildEssentials();
        HomePanel.Visibility = all ? Visibility.Collapsed : Visibility.Visible;
        EssentialsPanel.Visibility = all ? Visibility.Visible : Visibility.Collapsed;
        PageDots.Visibility = Visibility.Visible;
        UpdateDots();
        if (all) EssentialsPanel.ScrollToTop();
        // Both pages are the home's size: this also takes the island back from a card's size (coming back from a
        // card opened on the everything page) and keeps the right-now sentence and its once-a-second refresh going.
        RefreshHome();
        if (animate && IsVisible && !ReducedMotion) Slide(all ? EssentialsPanel : HomePanel, direction);
    }

    /// <summary>The page arrives from the side it was swiped from (a short ease; Calm motion just swaps).</summary>
    static void Slide(FrameworkElement el, int direction)
    {
        var t = new TranslateTransform();
        el.RenderTransform = t;
        el.Opacity = 1;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        t.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 48, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop });
    }

    // ---- The everything page -------------------------------------------------------------------------

    void BuildEssentials()
    {
        var all = Essentials.Catalog(_cards.Select(c => (c.Id, c.Title)));
        var uses = WheelLink.Current?.Uses ?? new Dictionary<string, IReadOnlyList<DateTime>>();
        var (most, others) = Essentials.Arrange(all, uses, DateTime.Now);

        EssentialsStack.Children.Clear();
        _essentialButtons.Clear();
        _essentialShown.Clear();

        EssentialsStack.Children.Add(SectionTitle("Most used"));
        var top = new UniformGrid { Columns = Essentials.TopCount, Rows = 1, Margin = new Thickness(0, 8, 0, 16) };
        foreach (var e in most) top.Children.Add(Centered(BigTile(e)));
        EssentialsStack.Children.Add(top);

        EssentialsStack.Children.Add(SectionTitle("Everything, A to Z"));
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var e in others) wrap.Children.Add(SmallTile(e));
        EssentialsStack.Children.Add(wrap);
    }

    static TextBlock SectionTitle(string text)
    {
        var t = CardKit.Text(text.ToUpperInvariant(), 10.5, FontWeights.SemiBold, secondary: true);
        t.Margin = new Thickness(2, 0, 0, 0);
        return t;
    }

    static FrameworkElement Centered(FrameworkElement el)
    {
        el.HorizontalAlignment = HorizontalAlignment.Center;
        return el;
    }

    // Cards that have no wheel droplet: the icon each card's own module uses.
    static readonly IReadOnlyDictionary<string, string> CardGlyphs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["alarms"] = "", ["circles"] = "", ["health"] = "", ["quick"] = "",
        ["battery"] = "", ["cricket"] = "", ["system"] = "",
    };

    /// <summary>The feature's icon: the wheel's own glyph or its card's, else its initial.</summary>
    static TextBlock IconFor(Essential e, double size)
    {
        // The wheel answers an unknown id with its info glyph, so a card's own icon is asked first.
        var glyph = CardGlyphs.TryGetValue(e.Id, out var card) ? card : WheelSlots.Find(e.Id) is not null ? WheelModule.GlyphFor(e.Id) : "";
        if (glyph.Length > 0) return CardKit.FluentGlyph(glyph, size);
        var letter = CardKit.Text(e.Name[..1].ToUpperInvariant(), size * 0.9, FontWeights.Bold);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return letter;
    }

    Button BigTile(Essential e)
    {
        var name = CardKit.Text(e.Name, 12.5, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = BigTileW - 14;
        name.Margin = new Thickness(0, 8, 0, 0);
        var face = new StackPanel();
        face.Children.Add(IconFor(e, 22));
        face.Children.Add(name);
        var tile = CardKit.Tile(e.Name, face, () => RunEssential(e), BigTileW, BigTileH);
        Track(tile, e);
        return tile;
    }

    FrameworkElement SmallTile(Essential e)
    {
        var button = CardKit.Quiet(e.Name, IconFor(e, 16), () => RunEssential(e), SmallCircle);
        button.HorizontalAlignment = HorizontalAlignment.Center;
        Track(button, e);
        var label = CardKit.Text(e.Name, 11, FontWeights.Medium, secondary: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = SmallSlot - 6;
        label.Margin = new Thickness(0, 4, 0, 10);
        var slot = new StackPanel { Width = SmallSlot };
        slot.Children.Add(button);
        slot.Children.Add(label);
        return slot;
    }

    void Track(Button button, Essential e)
    {
        AutomationProperties.SetHelpText(button, e.IsCard ? "Opens its card" : "Runs it");
        _essentialButtons.Add(button);
        _essentialShown.Add(e);
    }

    void RunEssential(Essential e)
    {
        if (_s is null) return;
        try
        {
            bool ran = e.IsCard ? OpenByClick(e.Id) : HostLinks.RunWheelAction(e.Id, OpenByClick, _s);
            if (!ran) Log.Warn($"The everything page couldn't run {e.Id}");
            // The wheel learns its own actions as they run; cards and moment-only actions are noted here.
            if (e.IsCard || WheelSlots.ById(e.Id) is null) WheelLink.Current?.NoteUse(e.Id);
        }
        catch (Exception ex)
        {
            Log.Warn($"The everything page's {e.Id} failed", ex);
        }
    }

    // ---- Keyboard -------------------------------------------------------------------------------------

    int EssentialIndexOf(DependencyObject? d)
    {
        for (; d is not null && d != this; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is Button b && _essentialButtons.IndexOf(b) is var i and >= 0) return i;
        return -1;
    }

    /// <summary>Arrow keys on the home level; true when they did something.</summary>
    bool PageKey(Key key, int tool, bool inside)
    {
        if (_page == 0)
        {
            // Right with nothing chosen, or past the last tool, slides to everything; Down reaches the tools.
            if (key == Key.Right && (!inside || tool == _toolButtons.Count - 1))
            {
                GoToPage(1);
                return true;
            }
            if (key == Key.Down && !inside)
            {
                FocusTool(0);
                return true;
            }
            return false;
        }
        int at = EssentialIndexOf(Keyboard.FocusedElement as DependencyObject);
        switch (key)
        {
            case Key.Left when at <= 0:
                GoToPage(0);
                return true;
            case Key.Left:
                _essentialButtons[at - 1].Focus();
                return true;
            case Key.Right or Key.Down when at < 0:
                if (_essentialButtons.Count > 0) _essentialButtons[0].Focus();
                return true;
            case Key.Right when at < _essentialButtons.Count - 1:
                _essentialButtons[at + 1].Focus();
                return true;
        }
        return false;
    }

    // ---- The test pipe --------------------------------------------------------------------------------

    /// <summary>
    /// "home-page" says the page and what the everything page lists; "home-page 0|1" goes there; "home-page swipe
    /// &lt;delta&gt;" scrolls sideways as a touchpad would; "home-page run &lt;n&gt;" presses item n of the everything page.
    /// </summary>
    internal string HomePage(string arg)
    {
        var a = arg.Trim();
        if (_openId is not null && a.Length > 0) GoHome(animate: false);
        if (a == "0" || a == "1") GoToPage(int.Parse(a));
        else if (a.StartsWith("swipe", StringComparison.OrdinalIgnoreCase) && int.TryParse(a[5..].Trim(), out var delta)) SwipePages(delta);
        else if (a.StartsWith("run", StringComparison.OrdinalIgnoreCase) && !int.TryParse(a[3..].Trim(), out _))
        {
            // "home-page run alarms": that feature, wherever it sits on the page.
            if (_page != 1) GoToPage(1);
            int at = _essentialShown.FindIndex(x => string.Equals(x.Id, a[3..].Trim(), StringComparison.OrdinalIgnoreCase));
            if (at < 0) return $"error: no {a[3..].Trim()} on the page";
            _essentialButtons[at].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return $"ran {_essentialShown[at].Id} ({(_essentialShown[at].IsCard ? "card" : "action")})";
        }
        else if (a.StartsWith("run", StringComparison.OrdinalIgnoreCase) && int.TryParse(a[3..].Trim(), out var n))
        {
            if (_page != 1) GoToPage(1);
            if (n < 1 || n > _essentialButtons.Count) return $"error: home-page run <1-{_essentialButtons.Count}>";
            var e = _essentialShown[n - 1];
            _essentialButtons[n - 1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return $"ran {e.Id} ({(e.IsCard ? "card" : "action")})";
        }
        if (a.Equals("glyphs", StringComparison.OrdinalIgnoreCase))
        {
            if (_page != 1) GoToPage(1);
            static string Of(Button b) => b.Content is Border { Child: TextBlock t } ? string.Join("", t.Text.Select(c => $"U+{(int)c:X4}"))
                : b.Content is Border { Child: StackPanel { Children.Count: > 0 } s } && s.Children[0] is TextBlock t2 ? string.Join("", t2.Text.Select(c => $"U+{(int)c:X4}")) : "?";
            return string.Join(" ", _essentialShown.Select((e, i) => $"{e.Id}={Of(_essentialButtons[i])}"));
        }
        if (_page == 1 && _essentialShown.Count == 0) BuildEssentials();
        var shown = _page == 1 ? _essentialShown : Essentials.Arrange(Essentials.Catalog(_cards.Select(c => (c.Id, c.Title))),
            WheelLink.Current?.Uses ?? new Dictionary<string, IReadOnlyList<DateTime>>(), DateTime.Now) is var (m, o) ? m.Concat(o).ToList() : new List<Essential>();
        return $"page={_page} dots={(PageDots.Visibility == Visibility.Visible ? "shown" : "hidden")} size={Width:0}x{Height:0} home={HomeWidth:0}x{HomeHeight:0} items={shown.Count} " +
               $"most=[{string.Join(",", shown.Take(Essentials.TopCount).Select(x => x.Id))}] rest=[{string.Join(",", shown.Skip(Essentials.TopCount).Select(x => x.Id))}]";
    }
}
