using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Core.OnlineVoice;
using Shammy.Core.Productivity;
using Shammy.Core.Search;
using Shammy.Core.Settings;
using Shammy.Core.Updates;
using Shammy.Infrastructure;
using Shammy.Modules.Connect;
using Shammy.Modules.OnlineVoice;
using Shammy.Search;
using Shammy.Views.Screens;
using PetFace = Shammy.Views.Screens.PetFace;

namespace Shammy.Modules.Settings;

/// <summary>
/// The Settings screen (the designed detail, 892×232 inside the notch's card frame): seven tabs on the left,
/// a scrolling panel of standard rows on the right (switches, segments, sliders, steppers, menus), and above it
/// a status line ("Saved · Hover speed Quick") with Done. Every control writes straight to
/// <see cref="AppSettings"/> through <see cref="SettingsCatalog"/> (saved at once); changes made elsewhere (the
/// pipe, the tray) show up while it's open. Accent only on the active tab's bar and Done.
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
    readonly Canvas _overlay = new();
    static string _tab = SettingsCatalog.Sections[0];
    bool _changing, _hooked, _rebuildQueued;

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

        Children.Add(_tabs);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        right.RowDefinitions.Add(new RowDefinition());
        SetColumn(right, 2);
        Children.Add(right);
        right.Children.Add(_status);
        var done2 = ScreenKit.Primary("Done", done, 32, 13);
        done2.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(done2);

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

    /// <summary>The tab on show (the pipe and the tour can open a particular one).</summary>
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

    /// <summary>For the pipe: the tab, the status and the rows' titles.</summary>
    public string Describe() =>
        $"tab={_tab} status='{_status.Text}' rows=" + string.Join(" | ", _rows.Children.OfType<FrameworkElement>().Select(r => r.Tag as string).Where(t => t is not null));

    // ---- Tabs ------------------------------------------------------------------------------------

    void BuildTabs()
    {
        _tabs.Children.Clear();
        foreach (var name in SettingsCatalog.Sections)
        {
            bool on = name == _tab;
            var text = ScreenKit.Text(name, 13, Weight.SemiBold, on ? ScreenKit.Fg : ScreenKit.Fg2);
            var bar = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(2),
                Background = on ? ScreenKit.Accent : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-12, 0, 0, 0),
            };
            var grid = new Grid { Children = { bar, text } };
            var tab = new Pressable(on ? ScreenKit.Tile : Brushes.Transparent, ScreenKit.Tile)
            {
                Height = 30,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 2),
                Child = grid,
                PressScale = 0.98,
            };
            tab.MouseEnter += (_, _) => text.Foreground = ScreenKit.Fg;
            tab.MouseLeave += (_, _) => text.Foreground = name == _tab ? ScreenKit.Fg : ScreenKit.Fg2;
            tab.Click += () => ShowTab(name);
            _tabs.Children.Add(tab);
        }
    }

    public void ShowTab(string name)
    {
        if (!SettingsCatalog.Sections.Contains(name)) return;
        _tab = name;
        CloseMenu();
        BuildTabs();
        BuildPanel();
        ScreenKit.Rise(_rows, 0, 6);
    }

    void BuildPanel(bool keepScroll = false)
    {
        double offset = keepScroll ? _scroll.VerticalOffset : 0;
        _rows.Children.Clear();
        switch (_tab)
        {
            case "General": General(); break;
            case "Notch & pet": NotchAndPet(); break;
            case "Wheel": WheelPage(); break;
            case "Voice": Voice(); break;
            case "Notifications": Notifications(); break;
            case "Time & units": TimeAndUnits(); break;
            case "Clipboard & privacy": ClipboardAndPrivacy(); break;
            case "Calendar": Calendar(); break;
        }
        // The last row has no hairline under it.
        if (_rows.Children.Count > 0 && _rows.Children[^1] is Border { Tag: string } last) last.BorderThickness = new Thickness(0);
        if (keepScroll) Dispatcher.BeginInvoke(() => _scroll.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
        else _scroll.ScrollToTop();
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
    Border Row(string title, string hint, FrameworkElement? control, out TextBlock hintText, FrameworkElement? lead = null)
    {
        var grid = new Grid { MinHeight = 56 - 18 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hintText = ScreenKit.Text(hint, 12, Weight.Regular, ScreenKit.Fg2);
        hintText.TextWrapping = TextWrapping.Wrap;
        hintText.TextTrimming = TextTrimming.None;
        hintText.LineHeight = 16.8;
        hintText.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        var words = ScreenKit.Column(2, ScreenKit.Text(title, 14, Weight.SemiBold), hintText);
        words.VerticalAlignment = VerticalAlignment.Center;
        if (hint.Length == 0) hintText.Visibility = Visibility.Collapsed;
        if (lead is not null)
        {
            lead.Margin = new Thickness(0, 0, 14, 0);
            lead.VerticalAlignment = VerticalAlignment.Center;
            var both = new StackPanel { Orientation = Orientation.Horizontal, Children = { lead, words } };
            grid.Children.Add(both);
        }
        else grid.Children.Add(words);
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
        _rows.Children.Add(row);
        return row;
    }

    Border Row(string title, string hint, FrameworkElement? control, FrameworkElement? lead = null) => Row(title, hint, control, out _, lead);

    Border SwitchRow(string title, string hint, string setting, string saidOn, string saidOff)
    {
        bool on = (bool)(SettingsCatalog.Find(setting) is { } info ? SettingsCatalog.Get(_s, info) ?? false : false);
        return Row(title, hint, new ToggleSwitch(on, v => Set(setting, v, v ? saidOn : saidOff), title));
    }

    void Section(string label, string aside)
    {
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
