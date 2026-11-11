using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Shammy.Modules;
using Shammy.Modules.Wheel;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The top of the open notch's home, above the tiles: the right-now line (the one thing that matters this minute,
/// from the glance feed: a meeting soon, Claude waiting, a timer, a recording, new messages, low battery) and the
/// dock (the wheel's eight actions as keys, so the same favourites are one click away with or without the middle
/// button). Each can be switched off in Settings › Notch &amp; pet. Nothing here runs while the notch is closed.
/// </summary>
public partial class ExpandedView
{
    const double KeySize = 48, KeyGap = 8, NowHeight = 34, StripGap = 8;

    /// <summary>The line shows only items at least this important (the time, the weather and the song don't qualify).</summary>
    const int NowMinPriority = 20;

    StackPanel? _homeTop;
    Border? _nowLine;
    TextBlock? _nowText, _nowMore;
    Ellipse? _nowDot;
    Border? _nowAction;
    TextBlock? _nowActionText;
    StackPanel? _dock;
    DispatcherTimer? _nowTimer;
    GlanceFeed? _feed;
    List<GlanceEntry> _nowItems = new();
    int _nowIndex;
    bool _nowPicked; // +N was clicked this time the notch is open: stay on that item while it lasts
    string _dockKey = "";

    /// <summary>The height the home strip takes above the tiles (0 when both parts are off or empty).</summary>
    double HomeTopHeight
    {
        get
        {
            if (_homeTop is not { Visibility: Visibility.Visible }) return 0;
            double h = 0;
            if (_nowLine is { Visibility: Visibility.Visible }) h += NowHeight + StripGap;
            if (_dock is { Visibility: Visibility.Visible }) h += KeySize + StripGap;
            return h;
        }
    }

    static double DockWidth => 8 * KeySize + 7 * KeyGap;

    void EnsureHomeTop()
    {
        if (_homeTop is not null) return;
        _homeTop = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        Grid.SetRow(_homeTop, 1);
        Body.Children.Add(_homeTop);

        // ---- The right-now line
        _nowDot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 10, 0) };
        _nowText = new TextBlock { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _nowText.SetResourceReference(TextBlock.FontFamilyProperty, "WallFontSemiBold");
        _nowText.SetResourceReference(TextBlock.ForegroundProperty, "WallText");
        _nowActionText = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold };
        _nowActionText.SetResourceReference(TextBlock.FontFamilyProperty, "WallFont");
        _nowActionText.SetResourceReference(TextBlock.ForegroundProperty, "WallOnAccent");
        _nowAction = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3, 10, 4), Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand, Child = _nowActionText, VerticalAlignment = VerticalAlignment.Center };
        _nowAction.SetResourceReference(Border.BackgroundProperty, "WallAccent");
        _nowAction.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RunNow();
        };
        _nowMore = new TextBlock { FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, ToolTip = "Next" };
        _nowMore.SetResourceReference(TextBlock.ForegroundProperty, "WallText2");
        _nowMore.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _nowIndex = (_nowIndex + 1) % Math.Max(1, _nowItems.Count);
            _nowPicked = true;
            ShowNowItem();
        };
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_nowDot, Dock.Left);
        DockPanel.SetDock(_nowMore, Dock.Right);
        DockPanel.SetDock(_nowAction, Dock.Right);
        row.Children.Add(_nowDot);
        row.Children.Add(_nowMore);
        row.Children.Add(_nowAction);
        row.Children.Add(_nowText);
        _nowLine = new Border
        {
            Height = NowHeight,
            Width = DockWidth,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 6, 0),
            Margin = new Thickness(0, 0, 0, StripGap),
            Child = row,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
        };
        _nowLine.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RunNow();
        };
        AutomationProperties.SetLiveSetting(_nowLine, AutomationLiveSetting.Polite);
        _homeTop.Children.Add(_nowLine);

        // ---- The dock
        _dock = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, StripGap), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_dock, "Dock");
        _homeTop.Children.Add(_dock);

        _feed = _s is { } s ? new GlanceFeed(s) : null;
        _nowTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshNow(), Dispatcher);
    }

    /// <summary>Called as the notch opens and on settings changes: rebuild what changed, size the rail below.</summary>
    void RefreshHomeTop()
    {
        if (_s is null) return;
        EnsureHomeTop();
        RefreshDock();
        RefreshNow();
        bool home = _openId is null && IsVisible;
        if (!home) _nowPicked = false;
        _homeTop!.Visibility = home ? Visibility.Visible : Visibility.Collapsed;
        if (home && _s.Settings.NowLineEnabled) _nowTimer!.Start();
        else _nowTimer!.Stop();
        double top = home ? HomeTopHeight : 0;
        Rail.Margin = new Thickness(0, 6 + top, 0, 0);
        EmptyText.Margin = new Thickness(0, top, 0, 0);
    }

    void RefreshNow()
    {
        if (_nowLine is null || _s is null) return;
        if (_feed is not null) _feed.Providers ??= HostLinks.Glances;
        bool wasShown = _nowLine.Visibility == Visibility.Visible;
        var current = _nowItems.ElementAtOrDefault(_nowIndex)?.Id;
        _nowItems = _s.Settings.NowLineEnabled && _feed is not null && _openId is null
            ? _feed.Current().Where(e => e.Item.Priority >= NowMinPriority && e.Kind is not (GlanceKind.Music or GlanceKind.Time)).ToList()
            : new List<GlanceEntry>();
        // New messages since Alerts was last opened (never their words: just how many and from where).
        if (_s.Settings.NowLineEnabled && _openId is null && _s.Notifications.UnreadCount is var unread and > 0)
        {
            var apps = string.Join(", ", _s.Notifications.History.Take(unread).Select(n => n.AppName).Distinct().Take(2));
            _nowItems.Add(new GlanceEntry("alerts", new GlanceItem(unread == 1 ? $"1 new message · {apps}" : $"{unread} new messages · {apps}",
                NotchPalette.Calendar, 35, ShowAlerts)));
            _nowItems = _nowItems.OrderByDescending(e => e.Item.Priority).ToList();
        }
        // Keep showing the same item while it lasts; otherwise the most important one.
        int keep = _nowPicked && current is not null ? _nowItems.FindIndex(e => e.Id == current) : -1;
        _nowIndex = keep >= 0 ? keep : 0;
        _nowLine.Visibility = _nowItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_nowItems.Count > 0) ShowNowItem();
        // The line came or went: the notch grows or shrinks to fit.
        if (wasShown != (_nowLine.Visibility == Visibility.Visible)) RebuildRail(animate: false);
    }

    void ShowNowItem()
    {
        if (_nowItems.ElementAtOrDefault(_nowIndex) is not { } entry) return;
        var item = entry.Item;
        Color accent;
        try { accent = CardKit.Color(item.AccentHex.Length > 0 ? item.AccentHex : "#FFFF8A3D"); }
        catch (FormatException) { accent = Color.FromRgb(0xFF, 0x8A, 0x3D); }
        _nowDot!.Fill = new SolidColorBrush(accent);
        _nowDot.Effect = new DropShadowEffect { Color = accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 };
        _nowLine!.Background = new LinearGradientBrush(Color.FromArgb(40, accent.R, accent.G, accent.B), Color.FromArgb(0, accent.R, accent.G, accent.B), 0);
        _nowLine.BorderBrush = new SolidColorBrush(Color.FromArgb(72, accent.R, accent.G, accent.B));
        _nowText!.Text = item.Text;
        _nowAction!.Visibility = item.OnClick is null ? Visibility.Collapsed : Visibility.Visible;
        _nowActionText!.Text = entry.Id == "calendar" ? "Join" : "Open";
        _nowMore!.Text = _nowItems.Count > 1 ? $"+{_nowItems.Count - 1}" : "";
        _nowMore.Visibility = _nowItems.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_nowLine, "Right now: " + item.Text);
    }

    void RunNow()
    {
        if (_nowItems.ElementAtOrDefault(_nowIndex)?.Item.OnClick is not { } click) return;
        try { click(); }
        catch (Exception ex) { Infrastructure.Log.Warn("A right-now action failed", ex); }
    }

    /// <summary>The dock: the wheel's beads as keys. Rebuilt only when the beads, the Cat theme or the switch change.</summary>
    void RefreshDock()
    {
        if (_dock is null || _s is null) return;
        var link = WheelLink.Current;
        bool on = _s.Settings.DockEnabled && link is not null;
        _dock.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) return;
        var slots = link!.Slots;
        string key = string.Join(",", slots.Select(s => s.ActionId)) + (_s.Settings.CatTheme ? "|cat" : "");
        if (key == _dockKey) return;
        _dockKey = key;
        _dock.Children.Clear();
        for (int i = 0; i < slots.Count; i++)
        {
            var id = slots[i].ActionId;
            var name = Core.Wheel.WheelSlots.ById(id)?.Name ?? id;
            _dock.Children.Add(DockKey(i, id, name, WheelModule.GlyphFor(id), _s.Settings.CatTheme, link));
        }
    }

    FrameworkElement DockKey(int index, string id, string name, string glyph, bool cat, WheelModule link)
    {
        var face = new Border { Width = KeySize, Height = KeySize, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1) };
        face.SetResourceReference(Border.BackgroundProperty, "WallTile");
        face.SetResourceReference(Border.BorderBrushProperty, "WallHairline");
        var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "WallText");
        var number = new TextBlock { Text = (index + 1).ToString(), FontSize = 9, Margin = new Thickness(7, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        number.SetResourceReference(TextBlock.ForegroundProperty, "WallText2");
        var inner = new Grid();
        inner.Children.Add(icon);
        inner.Children.Add(number);
        face.Child = inner;

        var root = new Grid { Width = KeySize, Height = KeySize, Margin = new Thickness(index == 0 ? 0 : KeyGap, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = name };
        root.Children.Add(face);
        // With the Cat theme, the key under the pointer wears two small ears.
        Path? ears = null;
        if (cat)
        {
            ears = new Path
            {
                Data = Geometry.Parse("M3,12 Q3,4 7,1 Q12,5 14,12 Z M31,12 Q31,4 27,1 Q22,5 20,12 Z"),
                Width = 34,
                Height = 12,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -9, 0, 0),
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false,
            };
            ears.SetResourceReference(Shape.FillProperty, "WallAccent");
            root.Children.Add(ears);
        }
        AutomationProperties.SetName(root, $"{index + 1}: {name}");
        root.MouseEnter += (_, _) =>
        {
            face.SetResourceReference(Border.BackgroundProperty, "WallSurfaceHover");
            face.SetResourceReference(Border.BorderBrushProperty, "WallAccent");
            if (ears is not null) ears.Visibility = Visibility.Visible;
        };
        root.MouseLeave += (_, _) =>
        {
            face.SetResourceReference(Border.BackgroundProperty, "WallTile");
            face.SetResourceReference(Border.BorderBrushProperty, "WallHairline");
            if (ears is not null) ears.Visibility = Visibility.Hidden;
        };
        root.MouseLeftButtonDown += (_, e) => e.Handled = true;
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            link.RunFromDock(id);
        };
        return root;
    }

    /// <summary>For the state pipe.</summary>
    internal string DescribeHomeTop() =>
        $"now={(_nowLine?.Visibility == Visibility.Visible ? $"'{_nowText?.Text}' of {_nowItems.Count}" : "hidden")} " +
        $"dock={(_dock?.Visibility == Visibility.Visible ? _dockKey : "hidden")} top={HomeTopHeight}";

    /// <summary>Test hook: what clicking dock key <paramref name="index"/> would do (0-based).</summary>
    internal string ClickDockForTest(int index)
    {
        if (WheelLink.Current is not { } link || index < 0 || index >= link.Slots.Count) return "error: no such key";
        link.RunFromDock(link.Slots[index].ActionId);
        return "clicked " + link.Slots[index].ActionId;
    }
}
