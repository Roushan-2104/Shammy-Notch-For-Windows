using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Palette;
using Shammy.Infrastructure;
using Shammy.Views.Screens;

namespace Shammy.Modules.Settings;

/// <summary>
/// The Settings screen (the designed detail, 892×232 inside the notch's card frame): eleven tabs on the left, a
/// scrolling panel of standard rows on the right (switches, segments, sliders, steppers, menus), and above it a
/// search box (filters the rows of every tab at once), the status line ("Saved · Hover speed Quick") and Done.
/// Every control writes straight to <see cref="AppSettings"/> through <see cref="SettingsCatalog"/> (saved at once);
/// changes made elsewhere (the pipe, the tray, the palette) show up while it's open. Accent only on the active
/// tab's bar and Done.
/// </summary>
internal sealed partial class SettingsView : Grid
{
    readonly Action<bool> _setPet;
    readonly AppSettings _s;
    readonly Func<IReadOnlyList<string>> _modules;
    readonly Action _done;
    readonly StackPanel _tabs = new();
    readonly StackPanel _rows = new();
    readonly ScrollViewer _scroll;
    readonly TextBlock _status = ScreenKit.Text("Saved automatically", 13, Weight.Regular, ScreenKit.Fg2);
    readonly TextBox _searchBox;
    readonly DispatcherTimer _searchTimer;
    readonly Canvas _overlay = new();
    readonly Dictionary<Border, string> _words = new();
    static string _tab = SettingsCatalog.Sections[0];
    string _query = "", _subsection = "";
    bool _changing, _hooked, _rebuildQueued, _searching;

    /// <param name="setPet">Shows or hides the pet on the notch (and saves it).</param>
    public SettingsView(AppSettings settings, Func<IReadOnlyList<string>> modules, Action done, Action<bool> setPet, string? tab = null)
    {
        _setPet = setPet;
        _s = settings;
        _modules = modules;
        _done = done;
        if (tab is not null && SettingsCatalog.Sections.Contains(tab)) _tab = tab;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(188) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        ColumnDefinitions.Add(new ColumnDefinition());

        // Eleven tabs fit the card's height; the list still scrolls if a larger notch size needs it.
        Children.Add(new ScrollViewer
        {
            Content = _tabs,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        });

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        right.RowDefinitions.Add(new RowDefinition());
        SetColumn(right, 2);
        Children.Add(right);

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _searchBox = NameBox("");
        _searchBox.MaxLength = 40;
        System.Windows.Automation.AutomationProperties.SetName(_searchBox, "Search settings");
        var search = SearchField(_searchBox, 230);
        header.Children.Add(search);
        _status.Margin = new Thickness(14, 0, 12, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_status, 1);
        header.Children.Add(_status);
        var done2 = ScreenKit.Primary("Done", done, 32, 13);
        done2.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(done2, 2);
        header.Children.Add(done2);
        right.Children.Add(header);

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

        _scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(18, 4, 18, 8),
        };
        _scroll.ScrollChanged += (_, _) => CloseMenu();
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

        // Menus float over everything here; a click anywhere else closes them.
        SetColumnSpan(_overlay, 3);
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
        }
        else
        {
            _s.Changed -= OnSettingChanged;
            _searchTimer.Stop();
            CloseMenu();
        }
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

    /// <summary>The search box: a magnifier, the text, and a quiet "Search settings" while it's empty.</summary>
    static Border SearchField(TextBox box, double width)
    {
        var hint = ScreenKit.Text("Search settings", 13, Weight.Regular, ScreenKit.Fg2);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var glass = ScreenKit.Icon("M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM16.5 16.5 20 20", ScreenKit.Fg2, 13, 2.2);
        glass.VerticalAlignment = VerticalAlignment.Center;
        glass.Margin = new Thickness(0, 0, 8, 0);
        var text = new Grid { Children = { hint, box } };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(glass);
        SetColumn(text, 1);
        grid.Children.Add(text);
        var field = new Border
        {
            Width = width,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = ScreenKit.Ground,
            BorderBrush = ScreenKit.Hairline,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 0, 9, 0),
            Child = grid,
            Cursor = Cursors.IBeam,
        };
        field.MouseLeftButtonDown += (_, _) => box.Focus();
        box.GotKeyboardFocus += (_, _) => field.BorderBrush = ScreenKit.Fg;
        box.LostKeyboardFocus += (_, _) => field.BorderBrush = ScreenKit.Hairline;
        return field;
    }

    // ---- Tabs ------------------------------------------------------------------------------------

    void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in SettingsCatalog.Sections)
        {
            // While searching, no tab is lit: the results come from all of them.
            bool on = name == _tab && _query.Length == 0;
            var text = ScreenKit.Text(name, 13, Weight.SemiBold, on ? ScreenKit.Fg : ScreenKit.Fg2);
            text.VerticalAlignment = VerticalAlignment.Center;
            var bar = new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(2),
                Background = on ? ScreenKit.Accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-12, 0, 0, 0),
            };
            var grid = new Grid { Children = { bar, text } };
            var tab = new Pressable(on ? ScreenKit.Tile : Brushes.Transparent, ScreenKit.Tile)
            {
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(16, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 1),
                Child = grid,
                PressScale = 0.98,
            };
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
            var none = ScreenKit.Text($"No setting matches “{_query}”.", 13, Weight.Regular, ScreenKit.Fg2);
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

    void Say(string said) => _status.Text = said.StartsWith("Saved", StringComparison.Ordinal) ? said : "Saved · " + said;

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
