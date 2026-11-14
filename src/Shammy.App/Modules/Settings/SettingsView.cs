using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Palette;
using Shammy.Infrastructure;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Settings;

/// <summary>
/// The Settings card (the card family's shape, 760 wide, as tall as the tab up to a cap): the header holds the
/// status line ("Saved · Hover speed Quick") and a "Find a setting" box (filters the rows of every tab at once);
/// under it a slim rail of the eleven tabs as text capsules (the chosen one lifted glass) and, on the right, the
/// tab's standard rows (switches, segments, sliders, steppers, menus), scrolling when the tab is tall. Every
/// control writes straight to <see cref="AppSettings"/> through <see cref="SettingsCatalog"/> (saved at once);
/// changes made elsewhere (the pipe, the tray, the palette) show up while it's open.
/// </summary>
internal sealed partial class SettingsView : CardSheet
{
    public const double SheetWidth = 760, RailWidth = 170, PanelMaxHeight = 420;

    static readonly Brush RailLifted = Ui.Tokens.Brush("#1AFFFFFF"), RailHover = Ui.Tokens.Brush("#0FFFFFFF"),
        GlassWell = Ui.Tokens.Brush("#12FFFFFF");

    readonly Action<bool> _setPet;
    readonly AppSettings _s;
    readonly Func<IReadOnlyList<string>> _modules;
    readonly StackPanel _tabs = new();
    readonly StackPanel _rows = new();
    readonly ScrollViewer _scroll;
    readonly TextBlock _status = Sheet.Meta("Saved automatically", 13);
    readonly TextBox _searchBox;
    readonly DispatcherTimer _searchTimer;
    readonly Canvas _overlay = new();
    readonly Dictionary<Border, string> _words = new();
    static string _tab = SettingsCatalog.Sections[0];
    string _query = "", _subsection = "";
    bool _changing, _hooked, _rebuildQueued, _searching;

    /// <param name="done">Kept for callers; the card's own Back goes home now.</param>
    /// <param name="setPet">Shows or hides the pet on the notch (and saves it).</param>
    public SettingsView(AppSettings settings, Func<IReadOnlyList<string>> modules, Action done, Action<bool> setPet, string? tab = null)
        : base("Settings", SheetWidth)
    {
        _setPet = setPet;
        _s = settings;
        _modules = modules;
        if (tab is not null && SettingsCatalog.Sections.Contains(tab)) _tab = tab;

        // The header: what was just saved, then the search box.
        _status.MaxWidth = 280;
        _status.Margin = new Thickness(0, 0, 4, 0);
        Action(_status);
        _searchBox = NameBox("");
        _searchBox.MaxLength = 40;
        System.Windows.Automation.AutomationProperties.SetName(_searchBox, "Search settings");
        var search = Sheet.Field(_searchBox, "Find a setting", CardKit.Paths.Search, 34);
        search.Width = 200;
        search.MouseLeftButtonDown += (_, _) => _searchBox.Focus();
        Action(search);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RailWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        _tabs.VerticalAlignment = VerticalAlignment.Top;
        body.Children.Add(_tabs);

        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Input, (_, _) =>
        {
            _searchTimer!.Stop();
            ApplySearch(_searchBox.Text);
        }, Dispatcher) { IsEnabled = false };
        _searchBox.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _searchBox.Text.Length > 0)
            {
                e.Handled = true;
                _searchBox.Text = "";
                ApplySearch("");
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                ApplySearch(_searchBox.Text);
            }
        };

        // The tab's rows, straight on the island's glass; a tall tab scrolls.
        _scroll = new ScrollViewer
        {
            Content = _rows,
            MaxHeight = PanelMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Padding = new Thickness(4, 0, 4, 0),
        };
        _scroll.ScrollChanged += (_, _) => CloseMenu();
        SetColumn(_scroll, 2);
        body.Children.Add(_scroll);
        Body.Children.Add(body);

        // Menus float over everything here; a click anywhere else closes them.
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource == _overlay) CloseMenu();
        };
        Children.Add(_overlay);

        Loaded += (_, _) => Hook(true);
        Unloaded += (_, _) => Hook(false);
        BuildTabs();
        BuildPanel();
    }

    /// <summary>The tab on show (the pipe, the tray, the palette and the tour can open a particular one).</summary>
    public static string Tab
    {
        get => _tab;
        set
        {
            if (SettingsCatalog.Sections.Contains(value)) _tab = value;
        }
    }

    void Hook(bool on)
    {
        if (on == _hooked) return;
        _hooked = on;
        if (on)
        {
            _s.Changed += OnSettingChanged;
            Theme.PaletteTheme.Changed += OnPaletteChanged;
        }
        else
        {
            _s.Changed -= OnSettingChanged;
            Theme.PaletteTheme.Changed -= OnPaletteChanged;
            _searchTimer.Stop();
            CloseMenu();
        }
    }

    /// <summary>A new song under Album: the Album pill shows its colours.</summary>
    void OnPaletteChanged()
    {
        if (Dispatcher.CheckAccess()) PaintAlbumPill();
        else Dispatcher.BeginInvoke(PaintAlbumPill);
    }

    /// <summary>A change from somewhere else (the pipe, a voice command) rebuilds the panel; our own don't.</summary>
    void OnSettingChanged(string name)
    {
        if (_changing || _rebuildQueued) return;
        _rebuildQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            if (IsLoaded) BuildPanel(keepScroll: true);
        });
    }

    /// <summary>For the pipe: the tab (or the search), the status and the rows' titles.</summary>
    public string Describe() =>
        $"tab={_tab} search='{_query}' status='{_status.Text}' rows=" +
        string.Join(" | ", _rows.Children.OfType<FrameworkElement>().Select(r => r.Tag as string).Where(t => t is not null));

    /// <summary>For the pipe: searches as if typed in the box.</summary>
    public void Search(string text)
    {
        _searchBox.Text = text;
        ApplySearch(text);
    }

    // ---- Search ----------------------------------------------------------------------------------

    void ApplySearch(string text)
    {
        var q = text.Trim();
        if (q == _query) return;
        _query = q;
        CloseMenu();
        BuildTabs();
        BuildPanel();
    }

    // ---- Tabs ------------------------------------------------------------------------------------

    /// <summary>The rail: the eleven tabs as text capsules, the chosen one lifted glass (none while searching).</summary>
    void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in SettingsCatalog.Sections)
        {
            // While searching, no tab is lit: the results come from all of them.
            bool on = name == _tab && _query.Length == 0;
            var text = ScreenKit.Text(name, 13, Weight.SemiBold, on ? ScreenKit.Fg : ScreenKit.Fg2);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            var tab = new Pressable(on ? RailLifted : Brushes.Transparent, on ? RailLifted : RailHover)
            {
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Child = text,
                PressScale = 0.98,
            };
            System.Windows.Automation.AutomationProperties.SetName(tab, name);
            tab.MouseEnter += (_, _) => text.Foreground = ScreenKit.Fg;
            tab.MouseLeave += (_, _) => text.Foreground = name == _tab && _query.Length == 0 ? ScreenKit.Fg : ScreenKit.Fg2;
            tab.Click += () => ShowTab(name);
            _tabs.Children.Add(tab);
        }
    }

    public void ShowTab(string name)
    {
        if (!SettingsCatalog.Sections.Contains(name)) return;
        _tab = name;
        // Picking a tab ends a search.
        if (_query.Length > 0 || _searchBox.Text.Length > 0)
        {
            _query = "";
            _searchTimer.Stop();
            _searchBox.Text = "";
        }
        CloseMenu();
        BuildTabs();
        BuildPanel();
        ScreenKit.Rise(_rows, 0, 6);
    }

    void BuildSection(string section)
    {
        _subsection = "";
        switch (section)
        {
            case SettingsCatalog.General: General(); break;
            case SettingsCatalog.Notch: NotchPage(); break;
            case SettingsCatalog.Wheel: WheelPage(); break;
            case SettingsCatalog.Pet: PetPage(); break;
            case SettingsCatalog.Voice: Voice(); break;
            case SettingsCatalog.Notifications: Notifications(); break;
            case SettingsCatalog.Routines: Routines(); break;
            case SettingsCatalog.Clipboard: ClipboardPage(); break;
            case SettingsCatalog.Connections: Connections(); break;
            case SettingsCatalog.Units: UnitsAndLanguage(); break;
            case SettingsCatalog.Advanced: Advanced(); break;
        }
    }

    void BuildPanel(bool keepScroll = false)
    {
        double offset = keepScroll ? _scroll.VerticalOffset : 0;
        _rows.Children.Clear();
        _words.Clear();
        if (_query.Length == 0) BuildSection(_tab);
        else SearchAll();
        // The last row has no hairline under it.
        if (_rows.Children.Count > 0 && _rows.Children[^1] is Border { Tag: string } last) last.BorderThickness = new Thickness(0);
        if (keepScroll) Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        else _scroll.ScrollToTop();
    }

    /// <summary>Every tab's rows that match the search, under their tab's name; the controls work as on their tab.</summary>
    void SearchAll()
    {
        int found = 0;
        _searching = true;
        try
        {
            foreach (var section in SettingsCatalog.Sections)
            {
                int start = _rows.Children.Count;
                try { BuildSection(section); }
                catch (Exception ex)
                {
                    Log.Warn($"Building the {section} settings for a search failed", ex);
                }
                var built = _rows.Children.Cast<UIElement>().Skip(start).ToList();
                _rows.Children.RemoveRange(start, built.Count);
                var kept = built.Where(e => e is Border { Tag: string } b && _words.TryGetValue(b, out var w)
                                            && ShammySettingSearch.Matches(_query, w + " " + section)).ToList();
                if (kept.Count == 0) continue;
                var label = ScreenKit.Label(section);
                label.Margin = new Thickness(0, found == 0 ? 8 : 14, 0, 2);
                _rows.Children.Add(label);
                foreach (var e in kept) _rows.Children.Add(e);
                found += kept.Count;
            }
        }
        finally { _searching = false; }
        if (found == 0)
        {
            // Not a setting, but a feature (night light lives in the Laptop card): say where it is instead.
            var feature = Core.Guide.FeatureFinder.Best(_query);
            var said = feature is null
                ? $"No setting matches “{_query}”."
                : $"No setting matches “{_query}”. {feature.Name}: {feature.Line} Try “{feature.Phrase}”.";
            var none = ScreenKit.Text(said, 13, Weight.Regular, ScreenKit.Fg2);
            none.TextWrapping = TextWrapping.Wrap;
            none.Margin = new Thickness(0, 12, 0, 12);
            _rows.Children.Add(none);
        }
    }

    // ---- Saving ----------------------------------------------------------------------------------

    void Set(string name, object? value, string said)
    {
        if (SettingsCatalog.Find(name) is not { } info) return;
        _changing = true;
        try { SettingsCatalog.Set(_s, info, value); }
        finally { _changing = false; }
        Say(said);
    }

    void Say(string said)
    {
        _status.Text = said.StartsWith("Saved", StringComparison.Ordinal) ? said : "Saved · " + said;
        Ui.A11y.Announce(_status.Text, _status);
    }

    // ---- Rows ------------------------------------------------------------------------------------

    /// <summary>A standard row: title and a one-line hint on the left, the control on the right, a hairline under it.</summary>
    Border Row(string title, string hint, FrameworkElement? control, out TextBlock hintText, FrameworkElement? lead = null, string words = "")
    {
        var grid = new Grid { MinHeight = 56 - 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hintText = ScreenKit.Text(hint, 12, Weight.Regular, ScreenKit.Fg2);
        hintText.TextWrapping = TextWrapping.Wrap;
        hintText.TextTrimming = TextTrimming.None;
        hintText.LineHeight = 16.8;
        hintText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var titleText = ScreenKit.Column(2, ScreenKit.Text(title, 14, Weight.SemiBold), hintText);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        if (hint.Length == 0) hintText.Visibility = Visibility.Collapsed;
        if (lead is not null)
        {
            lead.Margin = new Thickness(0, 0, 14, 0);
            lead.VerticalAlignment = VerticalAlignment.Center;
            var both = new StackPanel { Orientation = Orientation.Horizontal, Children = { lead, titleText } };
            grid.Children.Add(both);
        }
        else grid.Children.Add(titleText);
        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.Margin = new Thickness(20, 0, 0, 0);
            SetColumn(control, 1);
            grid.Children.Add(control);
        }
        var row = new Border
        {
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 9, 0, 9),
            Child = grid,
            Tag = title,
        };
        // What the search box looks through: the title, its hint, the group it sits in and any other words for it.
        _words[row] = $"{title} {hint} {_subsection} {words}";
        _rows.Children.Add(row);
        return row;
    }

    Border Row(string title, string hint, FrameworkElement? control, FrameworkElement? lead = null, string words = "") =>
        Row(title, hint, control, out _, lead, words);

    /// <summary>A row with its control under the title (the palette pills), then the hint; searched like any row.</summary>
    Border StackedRow(string title, string hint, FrameworkElement control, out TextBlock hintText, string words = "")
    {
        hintText = ScreenKit.Text(hint, 12, Weight.Regular, ScreenKit.Fg2);
        hintText.TextWrapping = TextWrapping.Wrap;
        hintText.TextTrimming = TextTrimming.None;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.Margin = new Thickness(0, 8, 0, 6);
        var row = new Border
        {
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 9, 0, 10),
            Child = new StackPanel { Children = { ScreenKit.Text(title, 14, Weight.SemiBold), control, hintText } },
            Tag = title,
        };
        _words[row] = $"{title} {hint} {_subsection} {words}";
        _rows.Children.Add(row);
        return row;
    }

    Border SwitchRow(string title, string hint, string setting, string saidOn, string saidOff, string words = "")
    {
        bool on = (bool)(SettingsCatalog.Find(setting) is { } info ? SettingsCatalog.Get(_s, info) ?? false : false);
        return Row(title, hint, new ToggleSwitch(on, v => Set(setting, v, v ? saidOn : saidOff), title), words: words);
    }

    /// <summary>A small group heading inside a tab (left out of search results, but its name is searched).</summary>
    void Section(string label, string aside)
    {
        _subsection = label;
        if (_searching) return;
        var grid = new Grid { Margin = new Thickness(0, 12, 0, 4) };
        grid.Children.Add(ScreenKit.Label(label));
        if (aside.Length > 0)
        {
            var a = ScreenKit.Text(aside, 12, Weight.Regular, ScreenKit.Fg2);
            a.HorizontalAlignment = HorizontalAlignment.Right;
            grid.Children.Add(a);
        }
        _rows.Children.Add(grid);
    }

    static void Dim(FrameworkElement e, bool on)
    {
        e.Opacity = on ? 1 : 0.4;
        e.IsHitTestVisible = on;
    }
}
