using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;
using Shammy.Core.Calendar;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules.Connect;
using Shammy.Views.Cards;

namespace Shammy.Modules.Calendar;

/// <summary>
/// The calendar wing: tap the date in the open island's header and the month pours out of the island's right side,
/// joined to it by a liquid neck. A huge light month name, today as a glossy drop in the palette's colour, this week
/// softly lit, warmer weekends, faded days gone by and a bead for each event; under it the chosen day as a line from
/// morning to night with a "now" needle, and its events with Join on the next meeting. Tap a day to see it; scroll
/// or use the arrows for other months. Its own small topmost window, never activated (the island keeps the keyboard).
/// The window owner (the notch) places it and keeps the island open while the pointer is on it.
/// </summary>
internal sealed class CalendarWing : Window
{
    const double Pad = 20, Cell = 42, CellGap = 4, NeckHeight = 132;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly Grid _layout = new();
    readonly Canvas _neckHost = new() { Width = CalendarWingFit.Neck };
    readonly Path _neck = new();
    readonly Border _slab = new();
    readonly Grid _glowLayer = new() { IsHitTestVisible = false, ClipToBounds = true };
    readonly StackPanel _body = new();
    readonly ScaleTransform _pour = new(1, 1);
    readonly ScaleTransform _neckPour = new(1, 1);
    IntPtr _hwnd;
    DateTime _month, _selected;
    double _wingWidth = CalendarWingFit.MaxWidth;
    readonly List<Action> _stop = new();

    public CalendarWing()
    {
        Title = "Shammy calendar";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _neck.RenderTransform = _neckPour;
        _neckHost.Children.Add(_neck);
        _layout.Children.Add(_neckHost);

        _slab.CornerRadius = new CornerRadius(32);
        _slab.BorderThickness = new Thickness(1);
        _slab.BorderBrush = Ui.Tokens.Brush("#1CFFFFFF");
        _slab.ClipToBounds = true;
        _slab.RenderTransformOrigin = new Point(0, 0.5);
        _slab.RenderTransform = _pour;
        var inner = new Grid();
        inner.Children.Add(_glowLayer);
        _body.Margin = new Thickness(Pad, Pad - 2, Pad, 16);
        inner.Children.Add(_body);
        _slab.Child = inner;
        Grid.SetColumn(_slab, 1);
        _layout.Children.Add(_slab);
        Content = _layout;

        _slab.MouseWheel += (_, e) =>
        {
            e.Handled = true;
            Flip(e.Delta > 0 ? -1 : 1);
        };
        MouseEnter += (_, _) => PointerOver?.Invoke(true);
        MouseLeave += (_, _) => PointerOver?.Invoke(false);
        PrivacyState.Changed += _ => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible) Build();
        });
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // Clicks land without taking the keyboard from the island (or from the app you're in).
            Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        };
        new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>The pointer came onto the wing (true) or left it (false): the notch holds the island open meanwhile.</summary>
    public event Action<bool>? PointerOver;

    /// <summary>The wing wants to fold away (to let the island show the Connect card).</summary>
    public event Action? FoldWanted;

    public bool IsOpen { get; private set; }

    /// <summary>For the test pipe: "open month=2026-11 selected=2026-11-15 events=2 width=470".</summary>
    public string Describe() => IsOpen
        ? string.Create(Inv, $"open month={_month:yyyy-MM} selected={_selected:yyyy-MM-dd} events={DayItems(_selected).Count} width={_wingWidth:0}")
        : "closed";

    /// <summary>Pours the wing out at this month, today chosen. <paramref name="fill"/> is the island's own colour (for the neck and the slab).</summary>
    public void Pour(double wingWidth, double scale, double islandHeight, Brush fill, bool excludeFromCapture)
    {
        _wingWidth = wingWidth;
        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;
        _layout.LayoutTransform = new ScaleTransform(scale, scale);
        _slab.Background = fill;
        _neck.Fill = fill;
        // The neck meets the island's straight right side, halfway down it; pinched in the middle, like the island's button.
        double top = Math.Max(0, islandHeight / 2 - NeckHeight / 2);
        double w = CalendarWingFit.Neck + 2, h = NeckHeight, m = h * 0.28;
        _neck.Data = Geometry.Parse(string.Create(Inv,
            $"M0,0 C{w * 0.45:0.#},{m:0.#} {w * 0.55:0.#},{m:0.#} {w:0.#},0 L{w:0.#},{h:0.#} C{w * 0.55:0.#},{h - m:0.#} {w * 0.45:0.#},{h - m:0.#} 0,{h:0.#} Z"));
        Canvas.SetTop(_neck, top);
        _neckHost.Margin = new Thickness(-1, 0, -1, 0);
        _neckPour.CenterY = top + h / 2;
        Build();
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, excludeFromCapture ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
        IsOpen = true;
        Show();
        Animate(open: true);
    }

    /// <summary>Folds the wing back into the island (at once with <paramref name="animate"/> off).</summary>
    public void Fold(bool animate)
    {
        if (!IsOpen) return;
        IsOpen = false;
        StopLoops();
        if (!animate || !MotionPrefs.Lively)
        {
            Hide();
            return;
        }
        Animate(open: false);
    }

    /// <summary>Moves the wing so its neck meets the island's right edge (device pixels).</summary>
    public void PlaceAt(int left, int top)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void Animate(bool open)
    {
        if (!MotionPrefs.Lively)
        {
            _pour.ScaleX = _neckPour.ScaleX = 1;
            _slab.Opacity = 1;
            return;
        }
        var ms = open ? 420 : 200;
        IEasingFunction ease = open ? new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseIn };
        // The drop beads out first (the neck), then the wing swells to full width from the island's side.
        var neck = Lean(new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 160 : ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        var widen = Lean(new DoubleAnimation(open ? 0.08 : 1, open ? 1 : 0.08, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(open ? 90 : 0) });
        var tall = Lean(new DoubleAnimation(open ? 0.55 : 1, open ? 1 : 0.55, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(open ? 90 : 0) });
        var fade = Lean(new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 240 : ms)) { BeginTime = TimeSpan.FromMilliseconds(open ? 90 : 0) });
        if (!open) fade.Completed += (_, _) =>
        {
            if (!IsOpen) Hide();
        };
        if (open)
        {
            _pour.ScaleX = 0.08;
            _pour.ScaleY = 0.55;
            _slab.Opacity = 0;
        }
        _neckPour.BeginAnimation(ScaleTransform.ScaleXProperty, neck);
        _pour.BeginAnimation(ScaleTransform.ScaleXProperty, widen);
        _pour.BeginAnimation(ScaleTransform.ScaleYProperty, tall);
        _slab.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The wing is a layered window: every repaint costs CPU, so its motion runs at a modest frame rate.</summary>
    static AnimationTimeline Lean(AnimationTimeline a)
    {
        Timeline.SetDesiredFrameRate(a, 40);
        return a;
    }

    /// <summary>Test pipe: chooses a day (and shows its month).</summary>
    internal string Pick(DateTime day)
    {
        _selected = day.Date;
        _month = new DateTime(day.Year, day.Month, 1);
        Build();
        return Describe();
    }

    /// <summary>Test pipe: another month, as the arrows do.</summary>
    internal string FlipBy(int months)
    {
        Flip(months);
        return Describe();
    }

    void Flip(int months)
    {
        _month = _month.AddMonths(months);
        if (_selected.Year != _month.Year || _selected.Month != _month.Month)
            _selected = _month.Year == DateTime.Today.Year && _month.Month == DateTime.Today.Month ? DateTime.Today : _month;
        Build();
    }

    // ---- Building ----------------------------------------------------------------------------------

    static IReadOnlyList<CalendarOccurrence> Items => CalendarModule.Agenda;

    static List<CalendarOccurrence> DayItems(DateTime day) => CalendarAgenda.OnDay(Items, day);

    /// <summary>Meetings with a call link take the palette's first colour, all-day events its third, the rest its second.</summary>
    static Brush KindBrush(CalendarOccurrence o) => o.HasLink ? Ink.A : o.AllDay ? Ink.C : Ink.B;

    void Build()
    {
        StopLoops();
        _body.Children.Clear();
        _glowLayer.Children.Clear();
        _body.Width = _wingWidth - 2 * Pad;
        var now = DateTime.Now;
        bool h24 = CardKit.H24;

        // Soft palette light inside the glass (cheap radial fills, no blur).
        _glowLayer.Children.Add(Glow(Ink.A, 0.42, 360, 300, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, -120, -130, 0)));
        _glowLayer.Children.Add(Glow(Ink.B, 0.18, 300, 240, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(-110, 0, 0, -90)));

        _body.Children.Add(Header(now));
        _body.Children.Add(Weekdays());
        _body.Children.Add(MonthGrid(now));
        _body.Children.Add(Day(now, h24));
    }

    FrameworkElement Header(DateTime now)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bool thisMonth = _month.Year == now.Year && _month.Month == now.Month;

        var left = new StackPanel();
        var name = Sheet.Num(_month.ToString("MMMM", Inv), 50);
        name.Foreground = Sheen();
        left.Children.Add(name);
        int left_ = DayItems(now).Count(o => !o.AllDay && o.End > now);
        var line = thisMonth
            ? $"{now.ToString("dddd", Inv)} · {(left_ == 0 ? "nothing left today" : left_ == 1 ? "1 event left today" : $"{left_} events left today")}"
            : _month.ToString("yyyy", Inv);
        var meta = Sheet.Meta(line, 12.5);
        meta.Margin = new Thickness(2, 4, 0, 0);
        left.Children.Add(meta);
        grid.Children.Add(left);

        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var big = Sheet.Num(thisMonth ? now.Day.ToString(Inv) : _month.ToString("MMM", Inv), 50, Ink.A);
        big.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(big);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        chips.Children.Add(Round("‹", "Previous month", () => Flip(-1)));
        var todayChip = CardKit.Pill("Today", "This month, today", () =>
        {
            _month = new DateTime(now.Year, now.Month, 1);
            _selected = DateTime.Today;
            Build();
        }, 28, 11.5);
        todayChip.Margin = new Thickness(5, 0, 5, 0);
        chips.Children.Add(todayChip);
        chips.Children.Add(Round("›", "Next month", () => Flip(1)));
        right.Children.Add(chips);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    static Button Round(string glyph, string name, Action click)
    {
        var t = CardKit.Text(glyph, 15, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, -2, 0, 0);
        return CardKit.Tile(name, t, click, 28, 28, 14);
    }

    FrameworkElement Weekdays()
    {
        var row = new UniformGrid7 { Margin = new Thickness(0, 14, 0, 4) };
        string[] names = { "M", "T", "W", "T", "F", "S", "S" };
        for (int i = 0; i < 7; i++)
        {
            var t = CardKit.Label(names[i], i >= 5 ? Ink.Soft : CardKit.Ink2, 10.5);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            row.Children.Add(t);
        }
        return row;
    }

    FrameworkElement MonthGrid(DateTime now)
    {
        var days = CalendarAgenda.MonthGrid(_month.Year, _month.Month);
        var host = new Grid();
        // This week: a soft band of the palette's light across its row.
        int weekRow = days.ToList().FindIndex(d => d == now.Date) is var i and >= 0 ? i / 7 : -1;
        if (weekRow >= 0)
        {
            var c = Ink.A.Color;
            host.Children.Add(new Border
            {
                Height = Cell + 4,
                CornerRadius = new CornerRadius(16),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(-6, weekRow * (Cell + CellGap) - 2, -6, 0),
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new(Color.FromArgb(0, c.R, c.G, c.B), 0), new(Color.FromArgb(0x24, c.R, c.G, c.B), 0.5), new(Color.FromArgb(0, c.R, c.G, c.B), 1),
                    }, 0),
            });
        }
        var grid = new UniformGrid7();
        foreach (var d in days) grid.Children.Add(DayCell(d, now));
        host.Children.Add(grid);
        return host;
    }

    FrameworkElement DayCell(DateTime d, DateTime now)
    {
        bool inMonth = d.Month == _month.Month;
        bool today = d == now.Date;
        bool selected = d == _selected.Date;
        bool weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        var items = inMonth ? DayItems(d) : new List<CalendarOccurrence>();

        var number = CardKit.Text(d.Day.ToString(Inv), 14.5, today ? FontWeights.Bold : FontWeights.Medium);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.Foreground = today ? CardKit.OnAccent : weekend ? Ink.Soft : CardKit.Ink;
        var beads = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 5, Margin = new Thickness(0, 3, 0, 0) };
        foreach (var o in items.Take(3))
            beads.Children.Add(new Ellipse { Width = 5, Height = 5, Margin = new Thickness(1.5, 0, 1.5, 0), Fill = today ? CardKit.OnAccent : KindBrush(o) });
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(number);
        stack.Children.Add(beads);

        var face = new Grid { Height = Cell, Margin = new Thickness(0, 0, 0, CellGap) };
        if (today) face.Children.Add(TodayDrop());
        else if (selected && inMonth)
            face.Children.Add(new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), BorderBrush = CardKit.Ink, Background = Sheet.CapFill });
        face.Children.Add(stack);
        if (!inMonth) face.Opacity = 0.28;
        else if (d < now.Date) face.Opacity = 0.55;

        var name = d.ToString("dddd d MMMM", Inv) + (items.Count == 0 ? "" : items.Count == 1 ? ", 1 event" : $", {items.Count} events");
        var b = CardKit.Bare(name, face, () =>
        {
            _selected = d;
            if (!inMonth) _month = new DateTime(d.Year, d.Month, 1);
            Build();
        }, double.NaN, Cell + CellGap);
        return b;
    }

    /// <summary>Today: a glossy drop in the palette's colour that breathes slowly while the wing is out.</summary>
    FrameworkElement TodayDrop()
    {
        var a = Ink.A.Color;
        var root = new Grid();
        var halo = new Border
        {
            CornerRadius = new CornerRadius(17),
            Margin = new Thickness(-3),
            Background = new RadialGradientBrush(Color.FromArgb(0x66, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B)),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        root.Children.Add(halo);
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.3, 0.2),
                Center = new Point(0.35, 0.3),
                RadiusX = 0.95,
                RadiusY = 0.95,
                GradientStops = { new GradientStop(Lighten(a, 0.55), 0), new GradientStop(a, 0.5), new GradientStop(Darken(a, 0.35), 1) },
            },
        });
        // The gloss: a small soft highlight near the top left.
        root.Children.Add(new Ellipse
        {
            Width = 14, Height = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(9, 5, 0, 0), Fill = new SolidColorBrush(Color.FromArgb(0x8C, 255, 255, 255)), IsHitTestVisible = false,
        });
        if (MotionPrefs.Lively)
        {
            var breathe = new DoubleAnimation(1, 1.16, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(breathe, 10);
            var scale = (ScaleTransform)halo.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
            var fade = new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(fade, 10);
            halo.BeginAnimation(OpacityProperty, fade);
            _stop.Add(() =>
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                halo.BeginAnimation(OpacityProperty, null);
            });
        }
        return root;
    }

    FrameworkElement Day(DateTime now, bool h24)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        var items = DayItems(_selected);
        bool isToday = _selected.Date == now.Date;
        bool hidden = PrivacyState.Presenting;

        var head = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        head.Children.Add(CardKit.Label((isToday ? "Today · " : "") + _selected.ToString("dddd d MMM", Inv), CardKit.Ink, 11.5));
        var count = Sheet.Meta(items.Count == 0 ? "" : items.Count == 1 ? "1 event" : $"{items.Count} events", 12);
        count.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(count);
        panel.Children.Add(head);

        if (items.Count == 0 && Links.Calendar is { Connected: false })
        {
            panel.Children.Add(Sheet.Meta("Connect Google Calendar to see your events here.", 12.5));
            var connect = CardKit.PrimaryPill("Connect", "Connect Google Calendar", () =>
            {
                FoldWanted?.Invoke();
                Links.Calendar?.OpenSetup();
            });
            connect.HorizontalAlignment = HorizontalAlignment.Left;
            connect.Margin = new Thickness(0, 8, 0, 0);
            panel.Children.Add(connect);
            return panel;
        }

        // The day as a line: each timed event a capsule, and a "now" needle on today.
        var (startH, endH) = CalendarAgenda.TimelineHours(items, _selected);
        double span = (endH - startH) * 60.0;
        var track = new Grid { Height = 40 };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(13), Background = Sheet.GlassFill, BorderBrush = Sheet.GlassEdge, BorderThickness = new Thickness(1) });
        var lane = new Canvas { ClipToBounds = false, Margin = new Thickness(6, 0, 6, 0) };
        track.Children.Add(lane);
        double laneW = _body.Width - 12;
        double X(DateTime t) => Math.Clamp(((t - _selected.Date).TotalMinutes - startH * 60) / span, 0, 1) * laneW;
        foreach (var o in items.Where(o => !o.AllDay))
        {
            double x0 = X(o.Start), x1 = Math.Max(X(o.End), x0 + 18);
            var cap = new Border
            {
                Width = x1 - x0, Height = 24, CornerRadius = new CornerRadius(12), Background = KindBrush(o),
                Child = x1 - x0 < 54 || hidden ? null : new TextBlock
                {
                    Text = o.Title, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = CardKit.OnAccent,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 7, 0),
                    FontFamily = CardKit.Face(FontWeights.SemiBold),
                },
                ToolTip = hidden ? null : o.Title,
            };
            Canvas.SetLeft(cap, x0);
            Canvas.SetTop(cap, 8);
            lane.Children.Add(cap);
        }
        if (isToday && now.Hour >= startH && now.Hour < endH)
        {
            double x = X(now);
            lane.Children.Add(Place(new Rectangle { Width = 2, Height = 48, RadiusX = 1, RadiusY = 1, Fill = CardKit.Ink }, x - 1, -4));
            lane.Children.Add(Place(new Ellipse { Width = 9, Height = 9, Fill = CardKit.Ink }, x - 4.5, -8));
        }
        panel.Children.Add(track);

        var hours = new Grid { Margin = new Thickness(4, 4, 4, 6) };
        for (int k = 0; k <= 4; k++)
        {
            int h = startH + (endH - startH) * k / 4;
            var t = Sheet.Meta(CalendarText.HourLabel(h, h24).ToLowerInvariant(), 10);
            t.HorizontalAlignment = k == 0 ? HorizontalAlignment.Left : k == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            if (k is > 0 and < 4) t.Margin = new Thickness((laneW - 8) * k / 4 - 14, 0, 0, 0);
            hours.Children.Add(t);
        }
        panel.Children.Add(hours);

        if (items.Count == 0)
        {
            var free = Sheet.Meta(isToday ? "Nothing planned · a free day" : "Nothing planned", 12.5);
            free.Margin = new Thickness(4, 2, 0, 0);
            panel.Children.Add(free);
            return panel;
        }

        // The list: the next one today gets "in 36 min"; the next meeting with a call link gets Join.
        var next = isToday ? items.FirstOrDefault(o => !o.AllDay && o.End > now) : null;
        var meeting = isToday ? items.FirstOrDefault(o => !o.AllDay && o.HasLink && o.End > now) : null;
        foreach (var o in items.Take(3)) panel.Children.Add(EventRow(o, now, h24, hidden, o == next || o == meeting, o == meeting));
        if (items.Count > 3)
        {
            var more = Sheet.Meta($"+{items.Count - 3} more", 12);
            more.Margin = new Thickness(12, 2, 0, 0);
            panel.Children.Add(more);
        }
        return panel;
    }

    static UIElement Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    FrameworkElement EventRow(CalendarOccurrence o, DateTime now, bool h24, bool hidden, bool soon, bool join)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4), MinHeight = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bg = new Border { CornerRadius = new CornerRadius(14), Margin = new Thickness(-8, 0, -8, 0), Background = soon ? Ink.Tint : null, BorderBrush = soon ? Ink.TintEdge : null, BorderThickness = new Thickness(soon ? 1 : 0) };
        Grid.SetColumnSpan(bg, 4);
        row.Children.Add(bg);

        var time = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (o.AllDay) time.Children.Add(Sheet.Word("All day", 13, FontWeights.SemiBold));
        else
        {
            time.Children.Add(Sheet.Word(CalendarText.Clock(o.Start, h24), 14, FontWeights.SemiBold));
            if (!h24)
            {
                var ap = Sheet.Meta(CalendarText.AmPm(o.Start).ToLowerInvariant(), 10.5);
                ap.Margin = new Thickness(2, 1, 0, 0);
                time.Children.Add(ap);
            }
        }
        row.Children.Add(time);
        var bar = new Rectangle { Width = 4, Height = 24, RadiusX = 2, RadiusY = 2, Fill = KindBrush(o), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 8, 4) };
        words.Children.Add(Sheet.Word(hidden ? "Busy" : o.Title, 14));
        var parts = new List<string>();
        if (soon && !o.AllDay) parts.Add(CalendarText.Until(o.Start, o.End, now));
        if (!hidden && !string.IsNullOrEmpty(o.Provider)) parts.Add(o.Provider!);
        else if (!hidden && o.Location.Length > 0) parts.Add(o.Location);
        if (parts.Count == 0) parts.Add(CalendarText.Length(o));
        words.Children.Add(Sheet.Meta(string.Join(" · ", parts), 12));
        Grid.SetColumn(words, 2);
        row.Children.Add(words);

        if (join)
        {
            var pill = CardKit.PrimaryPill("Join", "Join " + (hidden ? "meeting" : o.Title), () =>
            {
                Links.Calendar?.Join(o);
                FoldWanted?.Invoke();
            });
            pill.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(pill, 3);
            row.Children.Add(pill);
        }
        return row;
    }

    // ---- Looks ---------------------------------------------------------------------------------------

    static FrameworkElement Glow(SolidColorBrush ink, double opacity, double w, double h, HorizontalAlignment ha, VerticalAlignment va, Thickness margin)
    {
        var c = ink.Color;
        return new Ellipse
        {
            Width = w, Height = h, HorizontalAlignment = ha, VerticalAlignment = va, Margin = margin, Opacity = opacity,
            Fill = new RadialGradientBrush(Color.FromArgb(0xFF, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)),
        };
    }

    /// <summary>The month's name: off-white with a light band that sweeps across it once as the wing pours out.</summary>
    Brush Sheen()
    {
        var ink = ((SolidColorBrush)CardKit.Ink).Color;
        var shine = Lighten(Ink.A.Color, 0.6);
        var stops = new GradientStopCollection { new(ink, 0), new(ink, 0.4), new(shine, 0.5), new(ink, 0.6), new(ink, 1) };
        var brush = new LinearGradientBrush(stops, new Point(-1, 0), new Point(0, 0)) { MappingMode = BrushMappingMode.RelativeToBoundingBox };
        if (!MotionPrefs.Lively) return new SolidColorBrush(ink);
        var start = new PointAnimation(new Point(-1, 0), new Point(1, 0), TimeSpan.FromMilliseconds(1300)) { BeginTime = TimeSpan.FromMilliseconds(260), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var end = new PointAnimation(new Point(0, 0), new Point(2, 0), TimeSpan.FromMilliseconds(1300)) { BeginTime = TimeSpan.FromMilliseconds(260), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(start, 24);
        Timeline.SetDesiredFrameRate(end, 24);
        brush.BeginAnimation(LinearGradientBrush.StartPointProperty, start);
        brush.BeginAnimation(LinearGradientBrush.EndPointProperty, end);
        return brush;
    }

    static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    static Color Darken(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

    void StopLoops()
    {
        foreach (var stop in _stop) stop();
        _stop.Clear();
    }

    /// <summary>Seven equal columns, rows as tall as their tallest child (a month, the weekday names).</summary>
    sealed class UniformGrid7 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid7() => Columns = 7;
    }
}
