using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Shammy.Core.Guide;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Guide;

/// <summary>What the guide is showing: the group, the filter and the words typed into its finder.</summary>
internal enum GuideFilter { All, New, Untried, Off }

/// <summary>
/// The guide (the card family's shape, 720 wide): the header says how many things Shammy does and how many are
/// not tried yet; under it a full-width finder that takes everyday words, one sideways-scrolling row of capsules
/// (Everything, the New / Not tried / Off filters and the groups, each with its count), and the features as glass
/// tiles in two columns: the name (a dot when not tried yet), one line, the words that start it, Try and its
/// switch. About four rows show, then the tiles scroll; the "right now" suggestions sit in a slim line under them.
/// All it knows comes from <see cref="FeatureGuide"/>; what it does goes through the <see cref="Host"/> it is given.
/// </summary>
internal sealed class GuideView : CardSheet
{
    public const double SheetWidth = 720;

    /// <summary>What the view needs from the module.</summary>
    internal sealed record Host(
        Func<string?> Version,
        Func<string, bool> Used,
        Func<GuideFeature, bool> IsOn,
        Func<GuideFeature, bool, string> SetOn,
        Func<GuideFeature, string> Try,
        Func<(string Moment, IReadOnlyList<GuideSuggestion> Suggestions)> Suggest);

    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    const double TileGap = 10, TileHeight = 92;

    readonly Host _host;
    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer _chipScroll;
    readonly Grid _tiles = new();
    readonly StackPanel _now = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 10, 0, 0) };
    readonly TextBlock _status = new();
    readonly TextBox _find;
    readonly ScrollViewer _scroll;
    int _group = -1;
    GuideFilter _filter;

    public GuideView(Host host, string query, GuideFilter filter) : base("Guide", SheetWidth)
    {
        _host = host;
        _filter = filter;

        _find = new TextBox
        {
            Style = Application.Current?.TryFindResource("BareTextBox") as Style,
            Text = query,
            MaxLength = 80,
        };
        System.Windows.Automation.AutomationProperties.SetName(_find, "Find a feature in your own words");
        var field = Sheet.Field(_find, "Say what you want, in your own words", CardKit.Paths.Search, 38);
        _find.TextChanged += (_, _) => BuildList();

        _chipScroll = new ScrollViewer
        {
            Content = _chips,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(0, 1, 0, 1),
        };
        // A wheel over the capsules moves them sideways; the right edge fades while more are hidden there.
        _chipScroll.PreviewMouseWheel += (_, e) =>
        {
            if (_chipScroll.ScrollableWidth <= 0) return;
            _chipScroll.ScrollToHorizontalOffset(_chipScroll.HorizontalOffset - e.Delta / 2.0);
            e.Handled = true;
        };
        _chipScroll.ScrollChanged += (_, _) => Fade();

        _tiles.ColumnDefinitions.Add(new ColumnDefinition());
        _tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileGap) });
        _tiles.ColumnDefinitions.Add(new ColumnDefinition());
        // About four rows show (the fourth peeks), then the tiles scroll.
        _scroll = new ScrollViewer
        {
            Content = _tiles,
            MaxHeight = 3.4 * TileHeight + 3 * TileGap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Margin = new Thickness(0, 10, 0, 0),
        };

        Body.Children.Add(new StackPanel { Children = { field, _chipScroll, _scroll, _now } });

        BuildChips();
        BuildNow();
        BuildList();
    }

    public string Query => _find.Text;

    /// <summary>Shows a search (and filter) asked for from outside: a command, the pipe, the update note.</summary>
    public void Show(string query, GuideFilter filter)
    {
        _group = -1;
        _filter = filter;
        BuildChips();
        if (_find.Text == query) BuildList();
        else _find.Text = query;
        _scroll.ScrollToTop();
        _chipScroll.ScrollToLeftEnd();
    }

    public void Refresh()
    {
        BuildNow();
        BuildChips();
        BuildList();
    }

    public void Say(string text)
    {
        _status.Text = text;
        Header();
    }

    /// <summary>The features on show right now, in order.</summary>
    public IReadOnlyList<GuideFeature> Shown()
    {
        string version = _host.Version() ?? "";
        IEnumerable<GuideFeature> among = FeatureGuide.All;
        if (_group >= 0) among = among.Where(f => f.Group == _group);
        among = _filter switch
        {
            GuideFilter.New => among.Where(f => FeatureGuide.IsNew(f, version)),
            GuideFilter.Untried => among.Where(f => Untried(f)),
            GuideFilter.Off => among.Where(f => IsOff(f)),
            _ => among,
        };
        return FeatureFinder.Find(_find.Text, among.ToList()).Select(m => m.Feature).ToList();
    }

    /// <summary>Only things you start yourself can be "not tried": the keys and the passive features never nag.</summary>
    bool Untried(GuideFeature f) => f.Try.Length > 0 && !_host.Used(f.Id);

    bool IsOff(GuideFeature f) => f.Switch != GuideSwitch.None && !_host.IsOn(f);

    public string Describe() =>
        $"group={(_group < 0 ? "all" : FeatureGuide.Groups[_group])} filter={_filter} query=\"{_find.Text}\" rows={Shown().Count} " +
        $"first={Shown().FirstOrDefault()?.Id ?? "-"} now=\"{string.Join(",", _now.Children.OfType<Pressable>().Select(p => p.Tag))}\" status=\"{_status.Text}\"";

    // ---- The header ------------------------------------------------------------------------------

    /// <summary>"Guide · 51 things · 18 not tried yet" (the count in light numbers), or what just happened.</summary>
    void Header()
    {
        var t = TitleText;
        t.Inlines.Clear();
        t.Inlines.Add(new System.Windows.Documents.Run("Guide"));
        var quiet = CardKit.Face(FontWeights.Medium);
        System.Windows.Documents.Run Quiet(string s)
        {
            var r = CardKit.Run(s, FontWeights.Medium, CardKit.Ink2);
            r.FontFamily = quiet;
            r.FontSize = 13;
            return r;
        }
        if (_status.Tag is string said)
        {
            t.Inlines.Add(Quiet("  ·  " + said));
            return;
        }
        int untried = FeatureGuide.All.Count(Untried);
        t.Inlines.Add(Quiet($"  ·  {FeatureGuide.All.Count} things  ·  "));
        var number = new System.Windows.Documents.Run(untried.ToString())
        {
            FontFamily = Ui.Tokens.FontRes("ShammyDisplayFont"),
            FontWeight = FontWeights.Light,
            FontSize = 15,
            Foreground = Ink.Soft,
        };
        t.Inlines.Add(number);
        t.Inlines.Add(Quiet(" not tried yet"));
    }

    // ---- The capsule row -------------------------------------------------------------------------

    /// <summary>Everything, the three filters, then the groups: one row, the chosen ones in the accent tint.</summary>
    void BuildChips()
    {
        _chips.Children.Clear();
        string version = _host.Version() ?? "";
        _chips.Children.Add(Capsule("Everything", FeatureGuide.All.Count, _group < 0 && _filter == GuideFilter.All, "Everything, every feature", () =>
        {
            _group = -1;
            _filter = GuideFilter.All;
        }));
        (GuideFilter Filter, string Name, int Count)[] filters =
        {
            (GuideFilter.New, "New", FeatureGuide.All.Count(f => FeatureGuide.IsNew(f, version))),
            (GuideFilter.Untried, "Not tried", FeatureGuide.All.Count(Untried)),
            (GuideFilter.Off, "Off", FeatureGuide.All.Count(IsOff)),
        };
        foreach (var (f, name, count) in filters)
        {
            var filter = f;
            _chips.Children.Add(Capsule(name, count, _filter == filter, $"Filter: {name}", () => _filter = _filter == filter ? GuideFilter.All : filter));
        }
        var rule = new Border { Width = 1, Height = 18, Background = Sheet.GlassEdge, Margin = new Thickness(4, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _chips.Children.Add(rule);
        for (int g = 0; g < FeatureGuide.Groups.Count; g++)
        {
            int group = g;
            string name = FeatureGuide.Groups[g];
            int count = FeatureGuide.All.Count(f => f.Group == group);
            _chips.Children.Add(Capsule(name, count, _group == group, $"{name}, {count} features", () => _group = _group == group ? -1 : group));
        }
        Dispatcher.BeginInvoke(Fade, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>A capsule with its count in light numbers; <paramref name="on"/> wears the accent tint.</summary>
    Button Capsule(string name, int count, bool on, string spoken, Action pick)
    {
        var label = CardKit.Text(name, 13, FontWeights.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        var number = Sheet.Num(count.ToString(), 13, CardKit.Ink2);
        number.Margin = new Thickness(6, 0, 0, 0);
        var face = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = on ? Ink.Tint : Sheet.CapFill,
            BorderBrush = on ? Ink.TintEdge : Brushes.Transparent,
            BorderThickness = new Thickness(on ? 1 : 0),
            Padding = new Thickness(13, 0, 13, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { label, number } },
            SnapsToDevicePixels = true,
        };
        var button = new Button { Content = face, Template = Bare, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 6, 0), ToolTip = spoken };
        System.Windows.Automation.AutomationProperties.SetName(button, spoken);
        System.Windows.Automation.AutomationProperties.SetItemStatus(button, on ? "Chosen" : "");
        Ui.FocusKit.Button(button);
        Ui.Press.Attach(button);
        if (!on)
        {
            button.MouseEnter += (_, _) => face.Background = Sheet.CapHover;
            button.MouseLeave += (_, _) => face.Background = Sheet.CapFill;
        }
        button.Click += (_, e) =>
        {
            e.Handled = true;
            pick();
            double offset = _chipScroll.HorizontalOffset;
            BuildChips();
            _chipScroll.ScrollToHorizontalOffset(offset);
            BuildList();
            _scroll.ScrollToTop();
        };
        return button;
    }

    /// <summary>The capsule row fades at the right edge while more capsules are hidden there.</summary>
    void Fade()
    {
        bool more = _chipScroll.ScrollableWidth > 0.5 && _chipScroll.HorizontalOffset < _chipScroll.ScrollableWidth - 0.5;
        if (!more)
        {
            _chipScroll.OpacityMask = null;
            return;
        }
        if (_chipScroll.OpacityMask is LinearGradientBrush) return;
        _chipScroll.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Black, 0.86), new GradientStop(Colors.Transparent, 1) },
        };
    }

    static readonly ControlTemplate Bare = MakeBare();

    static ControlTemplate MakeBare()
    {
        var t = new ControlTemplate(typeof(System.Windows.Controls.Primitives.ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        t.Seal();
        return t;
    }

    // ---- Right now -------------------------------------------------------------------------------

    /// <summary>"Something is playing · Live captions · Volume mixer": a click finds that feature.</summary>
    void BuildNow()
    {
        _now.Children.Clear();
        var (moment, suggestions) = _host.Suggest();
        _now.Visibility = suggestions.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (suggestions.Count == 0) return;
        var label = Sheet.Meta(moment, 12);
        label.Margin = new Thickness(0, 0, 8, 0);
        _now.Children.Add(label);
        foreach (var s in suggestions)
        {
            var feature = s.Feature;
            var chip = ScreenKit.Secondary(feature.Name, () => Show(feature.Name, GuideFilter.All), 24, 12, 12);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chip.ToolTip = char.ToUpperInvariant(s.Why[0]) + s.Why[1..];
            chip.Tag = feature.Id;
            _now.Children.Add(chip);
        }
    }

    // ---- The tiles -------------------------------------------------------------------------------

    void BuildList()
    {
        _tiles.Children.Clear();
        _tiles.RowDefinitions.Clear();
        var shown = Shown();
        string version = _host.Version() ?? "";
        if (shown.Count == 0)
        {
            var none = Sheet.Meta(_filter == GuideFilter.Untried && _find.Text.Length == 0 ? "You have tried everything here." : "Nothing matches. Try another word, or Everything.", 13);
            none.Margin = new Thickness(4, 2, 0, 2);
            Grid.SetColumnSpan(none, 3);
            _tiles.Children.Add(none);
        }
        for (int i = 0; i < shown.Count; i++)
        {
            int row = i / 2;
            if (i % 2 == 0)
            {
                if (row > 0) _tiles.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileGap) });
                _tiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var tile = Tile(shown[i], version);
            Grid.SetRow(tile, row * 2);
            Grid.SetColumn(tile, i % 2 == 0 ? 0 : 2);
            _tiles.Children.Add(tile);
        }
        int on = FeatureGuide.All.Count(f => f.Switch == GuideSwitch.None || _host.IsOn(f));
        if (_status.Text.Length == 0 || _status.Tag is null)
        {
            _status.Text = $"{FeatureGuide.All.Count} features · {on} on · {FeatureGuide.All.Count(Untried)} not tried";
            _status.Tag = null;
        }
        Header();
    }

    /// <summary>One feature: the name (a dot when not tried yet, "New" when new), one quiet line, the words that start it, Try and its switch.</summary>
    Border Tile(GuideFeature f, string version)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Sheet.Word(f.Name, 14));
        if (Untried(f))
        {
            var dot = new Ellipse { Width = 7, Height = 7, Fill = CardKit.Accent, Margin = new Thickness(7, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Not tried yet" };
            System.Windows.Automation.AutomationProperties.SetName(dot, "Not tried yet");
            name.Children.Add(dot);
        }
        if (FeatureGuide.IsNew(f, version))
        {
            var fresh = Sheet.Chip("New", quiet: true, height: 18);
            fresh.Margin = new Thickness(8, 0, 0, 0);
            name.Children.Add(fresh);
        }
        top.Children.Add(name);
        if (f.Switch != GuideSwitch.None)
        {
            var toggle = new ToggleSwitch(_host.IsOn(f), on =>
            {
                Announce(_host.SetOn(f, on));
                BuildNow();
                double offset = _chipScroll.HorizontalOffset;
                BuildChips();
                _chipScroll.ScrollToHorizontalOffset(offset);
            }, f.Name)
            { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, -4, 0, -4) };
            Grid.SetColumn(toggle, 1);
            top.Children.Add(toggle);
        }

        var line = Sheet.Meta(f.Line, 12);
        line.ToolTip = f.Line;
        line.Margin = new Thickness(0, 4, 0, 0);
        var words = new StackPanel();
        words.Children.Add(top);
        words.Children.Add(line);
        // A switch that turns off more than its name says, says so.
        if (f.Also.Length > 0 && f.Switch != GuideSwitch.None)
        {
            var also = Sheet.Meta(f.Also, 11.5);
            also.TextTrimming = TextTrimming.None;
            also.TextWrapping = TextWrapping.Wrap;
            also.FontStyle = FontStyles.Italic;
            also.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(also);
        }

        var foot = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        foot.ColumnDefinitions.Add(new ColumnDefinition());
        foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var phrase = Sheet.Chip(f.Phrase, font: Mono);
        phrase.HorizontalAlignment = HorizontalAlignment.Left;
        phrase.ToolTip = PhraseLabel(f.Phrase) + ": " + f.Phrase;
        if (phrase.Child is TextBlock pt)
        {
            pt.FontWeight = FontWeights.Normal;
            pt.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        foot.Children.Add(phrase);
        if (f.Try.Length > 0)
        {
            var go = Sheet.Cap("Try", "Try " + f.Name, () =>
            {
                Announce(_host.Try(f));
                BuildNow();
                BuildList();
            }, height: 26);
            go.Margin = new Thickness(10, 0, 0, 0);
            go.ToolTip = "Try it";
            Grid.SetColumn(go, 1);
            foot.Children.Add(go);
        }
        words.Children.Add(foot);

        var tile = Sheet.Tile(words, new Thickness(14, 11, 12, 11), 18);
        tile.MinHeight = TileHeight;
        tile.Tag = f.Id;
        tile.VerticalAlignment = VerticalAlignment.Stretch;
        return tile;
    }

    static readonly string[] Gestures = { "hold ", "hover", "rest the", "scroll ", "drag ", "tap ", "copy anything", "the tab", "a message", "Copilot" };

    /// <summary>"Say or type" for words, "How" for something you do with the keys or the pointer, "Find it in" for a Settings place.</summary>
    static string PhraseLabel(string phrase) =>
        phrase.StartsWith("Settings ›", StringComparison.Ordinal) ? "Find it in"
        : Gestures.Any(g => phrase.StartsWith(g, StringComparison.Ordinal)) ? "How"
        : "Say or type";

    /// <summary>What just happened, in place of the counts until the list is next rebuilt from scratch.</summary>
    void Announce(string text)
    {
        _status.Text = text;
        _status.Tag = text;
        Header();
    }
}
