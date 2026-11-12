using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Guide;
using Shammy.Views.Screens;

namespace Shammy.Modules.Guide;

/// <summary>What the guide is showing: the group, the filter and the words typed into its finder.</summary>
internal enum GuideFilter { All, New, Untried, Off }

/// <summary>
/// The guide (the same 892×232 frame as Settings): groups on the left; on the right a finder that takes everyday
/// words, the "right now" suggestions, and a scrolling list with one row per feature: its name, one line, the
/// words that start it, Try it, and its switch. All it knows comes from <see cref="FeatureGuide"/>; what it does
/// goes through the <see cref="Host"/> it is given.
/// </summary>
internal sealed class GuideView : Grid
{
    /// <summary>What the view needs from the module.</summary>
    internal sealed record Host(
        Func<string?> Version,
        Func<string, bool> Used,
        Func<GuideFeature, bool> IsOn,
        Func<GuideFeature, bool, string> SetOn,
        Func<GuideFeature, string> Try,
        Func<(string Moment, IReadOnlyList<GuideSuggestion> Suggestions)> Suggest);

    static readonly Brush Mint = ScreenKit.B("#7DE3C3"), MintInk = ScreenKit.B("#06261C");
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    readonly Host _host;
    readonly StackPanel _groups = new(), _rows = new();
    readonly WrapPanel _now = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = ScreenKit.Text("", 12, Weight.Regular, ScreenKit.Fg2);
    readonly TextBox _find;
    readonly ScrollViewer _scroll;
    Segmented _filters = null!;
    int _group = -1;
    GuideFilter _filter;

    public GuideView(Host host, string query, GuideFilter filter)
    {
        _host = host;
        _filter = filter;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition());
        Children.Add(_groups);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        right.RowDefinitions.Add(new RowDefinition());
        SetColumn(right, 2);
        Children.Add(right);

        _find = new TextBox
        {
            Style = Application.Current?.TryFindResource("BareTextBox") as Style,
            Text = query,
            MaxLength = 80,
            FontSize = 13,
            FontFamily = ScreenKit.Font(Weight.Medium).Family,
            Foreground = ScreenKit.Fg,
            CaretBrush = ScreenKit.Fg,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(_find, "Find a feature in your own words");
        var hint = ScreenKit.Text("Say what you want, in your own words", 13, Weight.Regular, ScreenKit.Fg2);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _find.TextChanged += (_, _) =>
        {
            hint.Visibility = _find.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildList();
        };
        var field = new Border
        {
            Width = 300,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = ScreenKit.Ground,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new Grid { Children = { hint, _find } },
        };
        right.Children.Add(field);

        var labels = new[] { "All", "New", "Not tried", "Off" };
        _filters = new Segmented(labels, (int)_filter, i =>
        {
            _filter = (GuideFilter)i;
            BuildList();
        }, minWidth: 54, height: 26) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(_filters);

        var strip = new Grid();
        strip.ColumnDefinitions.Add(new ColumnDefinition());
        strip.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strip.Children.Add(_now);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(12, 0, 2, 0);
        SetColumn(_status, 1);
        strip.Children.Add(_status);
        SetRow(strip, 1);
        right.Children.Add(strip);

        _scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(16, 2, 16, 6),
        };
        var panel = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = ScreenKit.Tile,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Child = _scroll,
            ClipToBounds = true,
        };
        SetRow(panel, 2);
        right.Children.Add(panel);

        BuildGroups();
        BuildNow();
        BuildList();
    }

    public string Query => _find.Text;

    /// <summary>Shows a search (and filter) asked for from outside: a command, the pipe, the update note.</summary>
    public void Show(string query, GuideFilter filter)
    {
        _group = -1;
        _filter = filter;
        _filters.Index = (int)filter;
        BuildGroups();
        if (_find.Text == query) BuildList();
        else _find.Text = query;
        _scroll.ScrollToTop();
    }

    public void Refresh()
    {
        BuildNow();
        BuildList();
    }

    public void Say(string text) => _status.Text = text;

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
            GuideFilter.Off => among.Where(f => f.Switch != GuideSwitch.None && !_host.IsOn(f)),
            _ => among,
        };
        return FeatureFinder.Find(_find.Text, among.ToList()).Select(m => m.Feature).ToList();
    }

    /// <summary>Only things you start yourself can be "not tried": the keys and the passive features never nag.</summary>
    bool Untried(GuideFeature f) => f.Try.Length > 0 && !_host.Used(f.Id);

    public string Describe() =>
        $"group={(_group < 0 ? "all" : FeatureGuide.Groups[_group])} filter={_filter} query=\"{_find.Text}\" rows={Shown().Count} " +
        $"first={Shown().FirstOrDefault()?.Id ?? "-"} now=\"{string.Join(",", _now.Children.OfType<Pressable>().Select(p => p.Tag))}\" status=\"{_status.Text}\"";

    void BuildGroups()
    {
        _groups.Children.Clear();
        for (int g = -1; g < FeatureGuide.Groups.Count; g++)
        {
            int group = g;
            bool on = group == _group;
            string name = group < 0 ? "Everything" : FeatureGuide.Groups[group];
            int count = group < 0 ? FeatureGuide.All.Count : FeatureGuide.All.Count(f => f.Group == group);
            var text = ScreenKit.Text(name, 13, Weight.SemiBold, on ? ScreenKit.Fg : ScreenKit.Fg2);
            var number = ScreenKit.Text(count.ToString(), 12, Weight.Regular, ScreenKit.Fg2);
            number.HorizontalAlignment = HorizontalAlignment.Right;
            var bar = new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(2),
                Background = on ? ScreenKit.Accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-12, 0, 0, 0),
            };
            var row = new Pressable(on ? ScreenKit.Tile : Brushes.Transparent, ScreenKit.Tile)
            {
                Height = 28,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Child = new Grid { Children = { bar, text, number } },
                PressScale = 0.98,
            };
            System.Windows.Automation.AutomationProperties.SetName(row, $"{name}, {count} features");
            row.Click += () =>
            {
                _group = group;
                BuildGroups();
                BuildList();
                _scroll.ScrollToTop();
            };
            _groups.Children.Add(row);
        }
    }

    /// <summary>"Something is playing · Live captions · Volume mixer": a click finds that feature.</summary>
    void BuildNow()
    {
        _now.Children.Clear();
        var (moment, suggestions) = _host.Suggest();
        if (suggestions.Count == 0) return;
        var label = ScreenKit.Text(moment, 12, Weight.Regular, ScreenKit.Fg2);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(2, 0, 8, 0);
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

    void BuildList()
    {
        _rows.Children.Clear();
        var shown = Shown();
        string version = _host.Version() ?? "";
        if (shown.Count == 0)
        {
            var none = ScreenKit.Text(_filter == GuideFilter.Untried && _find.Text.Length == 0 ? "You have tried everything here." : "Nothing matches. Try another word, or the All filter.",
                13, Weight.Regular, ScreenKit.Fg2);
            none.Margin = new Thickness(0, 22, 0, 0);
            none.HorizontalAlignment = HorizontalAlignment.Center;
            _rows.Children.Add(none);
        }
        for (int i = 0; i < shown.Count; i++) _rows.Children.Add(Row(shown[i], version, last: i == shown.Count - 1));
        int on = FeatureGuide.All.Count(f => f.Switch == GuideSwitch.None || _host.IsOn(f));
        if (_status.Text.Length == 0 || _status.Tag is null)
        {
            _status.Text = $"{FeatureGuide.All.Count} features · {on} on · {FeatureGuide.All.Count(Untried)} not tried";
            _status.Tag = null;
        }
    }

    Border Row(GuideFeature f, string version, bool last)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(ScreenKit.Text(f.Name, 14, Weight.SemiBold));
        if (FeatureGuide.IsNew(f, version)) name.Children.Add(Badge("NEW", Mint, MintInk, null));
        if (Untried(f)) name.Children.Add(Badge("NOT TRIED YET", Brushes.Transparent, ScreenKit.Fg2, ScreenKit.Hairline));

        var line = ScreenKit.Text(f.Line, 12, Weight.Regular, ScreenKit.Fg2);
        line.TextWrapping = TextWrapping.Wrap;
        var say = new StackPanel { Orientation = Orientation.Horizontal };
        var sayLabel = ScreenKit.Label(PhraseLabel(f.Phrase), ScreenKit.Fg2, 9.5);
        sayLabel.VerticalAlignment = VerticalAlignment.Center;
        sayLabel.Margin = new Thickness(0, 0, 7, 0);
        say.Children.Add(sayLabel);
        say.Children.Add(new TextBlock { Text = f.Phrase, FontFamily = Mono, FontSize = 11.5, Foreground = ScreenKit.Fg, VerticalAlignment = VerticalAlignment.Center });
        var phrase = new Border
        {
            Background = ScreenKit.Ground,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = say,
        };
        var words = ScreenKit.Column(1, name, line, phrase);
        // A switch that turns off more than its name says, says so.
        if (f.Also.Length > 0 && f.Switch != GuideSwitch.None)
        {
            var also = ScreenKit.Text(f.Also, 11.5, Weight.Regular, ScreenKit.Fg2);
            also.TextWrapping = TextWrapping.Wrap;
            also.FontStyle = FontStyles.Italic;
            also.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(also);
        }
        words.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(words);

        if (f.Try.Length > 0)
        {
            var go = ScreenKit.Secondary("Try it", () =>
            {
                Announce(_host.Try(f));
                BuildNow();
                BuildList();
            }, 28, 14, 12.5);
            go.VerticalAlignment = VerticalAlignment.Center;
            go.Margin = new Thickness(14, 0, 0, 0);
            System.Windows.Automation.AutomationProperties.SetName(go, "Try " + f.Name);
            SetColumn(go, 1);
            grid.Children.Add(go);
        }
        if (f.Switch != GuideSwitch.None)
        {
            var toggle = new ToggleSwitch(_host.IsOn(f), on =>
            {
                Announce(_host.SetOn(f, on));
                BuildNow();
            }, f.Name)
            { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            SetColumn(toggle, 2);
            grid.Children.Add(toggle);
        }
        return new Border
        {
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
            Padding = new Thickness(0, 9, 0, 9),
            Child = grid,
            Tag = f.Id,
        };
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
    }

    static Border Badge(string text, Brush fill, Brush ink, Brush? ring) => new()
    {
        Background = fill,
        BorderBrush = ring ?? Brushes.Transparent,
        BorderThickness = new Thickness(ring is null ? 0 : 1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(5, 0, 5, 1),
        Margin = new Thickness(8, 1, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = ScreenKit.Label(text, ink, 9),
    };
}
