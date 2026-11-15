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
/// The calendar wing (the "dot month"): tap the date in the open island's header and a slab the island's own height
/// opens on its right, joined by a short pinched neck; the island never moves. On top, the chosen day as a big light
/// number beside the month drawn as dots, one circle size throughout: a dot opens into a numbered circle when you rest
/// on it or pick it, today is the palette's colour and breathes, days with events are ringed in the event's colour,
/// days gone by dim. Across the bottom, the chosen day's events (with "in 3h 37m" and Join today) and the day as a
/// thin line with a now needle. Its own small topmost window, never activated (the island keeps the keyboard); the
/// notch places it and holds the island open while the pointer is on it.
/// </summary>
internal sealed class CalendarWing : Window
{
    const double Dot = 10, Open = 20, NeckHeight = 96;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Brush Past = Ui.Tokens.Brush("#24FFFFFF"), Future = Ui.Tokens.Brush("#57FFFFFF"), Hover = Ui.Tokens.Brush("#29FFFFFF"),
        Line = Ui.Tokens.Brush("#12FFFFFF");

    readonly Grid _layout = new();
    readonly Path _neck = new();
    readonly Border _slab = new();
    readonly Grid _inner = new();
    readonly ScaleTransform _pour = new(1, 1), _neckPour = new(1, 1);
    readonly List<Action> _stop = new();
    IntPtr _hwnd;
    DateTime _month, _selected;
    double _height = 262;

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

        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CalendarWingFit.Neck) });
        _layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var w = CalendarWingFit.Neck + 2;
        double h = NeckHeight, m = h * 0.31;
        _neck.Data = Geometry.Parse(string.Create(Inv,
            $"M0,0 C{w * 0.44:0.#},{m:0.#} {w * 0.56:0.#},{m:0.#} {w:0.#},0 L{w:0.#},{h:0.#} C{w * 0.56:0.#},{h - m:0.#} {w * 0.44:0.#},{h - m:0.#} 0,{h:0.#} Z"));
        _neck.Margin = new Thickness(-1, 0, -1, 0);
        _neck.VerticalAlignment = VerticalAlignment.Center;
        _neck.RenderTransformOrigin = new Point(0, 0.5);
        _neck.RenderTransform = _neckPour;
        _layout.Children.Add(_neck);

        _slab.BorderThickness = new Thickness(1);
        _slab.BorderBrush = Ui.Tokens.Brush("#1CFFFFFF");
        _slab.Width = CalendarWingFit.Width;
        _slab.RenderTransformOrigin = new Point(0, 0.5);
        _slab.RenderTransform = _pour;
        _slab.Child = _inner;
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
            if (IsOpen) Build();
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

    /// <summary>The wing wants to fold away (Join was pressed, or Connect opens its card in the island).</summary>
    public event Action? FoldWanted;

    public bool IsOpen { get; private set; }

    /// <summary>For the test pipe: "open month=2026-11 selected=2026-11-15 events=2 scale=0.88".</summary>
    public string Describe() => IsOpen
        ? string.Create(Inv, $"open month={_month:yyyy-MM} selected={_selected:yyyy-MM-dd} events={DayItems(_selected).Count} scale={_scale:0.##}")
        : "closed";

    double _scale = 1;

    /// <summary>
    /// Opens the wing on today. <paramref name="height"/> and <paramref name="radius"/> are the island's, in the wing's
    /// own units; <paramref name="fill"/> is the island's colour (the slab and the neck are the same stuff).
    /// </summary>
    public void Pour(double scale, double height, double radius, Brush fill, bool excludeFromCapture)
    {
        _scale = scale;
        _height = height;
        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;
        _layout.LayoutTransform = new ScaleTransform(scale, scale);
        _slab.Height = height;
        _slab.CornerRadius = new CornerRadius(radius);
        _slab.Background = fill;
        _neck.Fill = fill;
        // The glows stay inside the rounded corners.
        _inner.Clip = new RectangleGeometry(new Rect(0, 0, CalendarWingFit.Width - 2, height - 2), radius - 1, radius - 1);
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
        if (!animate || !MotionPrefs.Lively) Hide();
        else Animate(open: false);
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
            _pour.ScaleX = _neckPour.ScaleX = _slab.Opacity = 1;
            return;
        }
        int ms = open ? 360 : 180;
        IEasingFunction ease = open ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseIn };
        // The neck beads out of the island first, then the slab swells from it.
        var neck = Lean(new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 140 : ms)));
        var widen = Lean(new DoubleAnimation(open ? 0.1 : 1, open ? 1 : 0.1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease, BeginTime = TimeSpan.FromMilliseconds(open ? 80 : 0) });
        var fade = Lean(new DoubleAnimation(open ? 0 : 1, open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 220 : ms)) { BeginTime = TimeSpan.FromMilliseconds(open ? 80 : 0) });
        if (!open) fade.Completed += (_, _) =>
        {
            if (!IsOpen) Hide();
        };
        if (open)
        {
            _pour.ScaleX = 0.1;
            _slab.Opacity = 0;
        }
        _neckPour.BeginAnimation(ScaleTransform.ScaleXProperty, neck);
        _pour.BeginAnimation(ScaleTransform.ScaleXProperty, widen);
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
        Select(day);
        return Describe();
    }

    /// <summary>Test pipe: another month, as the arrows do.</summary>
    internal string FlipBy(int months)
    {
        Flip(months);
        return Describe();
    }

    void Select(DateTime day)
    {
        _selected = day.Date;
        _month = new DateTime(day.Year, day.Month, 1);
        Build();
    }

    void Flip(int months)
    {
        _month = _month.AddMonths(months);
        // The big number follows: today in this month, else the 1st.
        var today = DateTime.Today;
        if (_selected.Year != _month.Year || _selected.Month != _month.Month)
            _selected = _month.Year == today.Year && _month.Month == today.Month ? today : _month;
        Build();
    }

    // ---- Building ----------------------------------------------------------------------------------

    static List<CalendarOccurrence> DayItems(DateTime day) => CalendarAgenda.OnDay(CalendarModule.Agenda, day);

    /// <summary>Meetings with a call link take the palette's first colour, all-day events its third, the rest its second.</summary>
    static SolidColorBrush KindBrush(CalendarOccurrence o) => o.HasLink ? Ink.A : o.AllDay ? Ink.C : Ink.B;

    void Build()
    {
        StopLoops();
        _inner.Children.Clear();
        var now = DateTime.Now;
        bool h24 = CardKit.H24;

        // Soft palette light inside the glass, low on the left (cheap radial fills, no blur).
        var c = Ink.A.Color;
        _inner.Children.Add(new Ellipse
        {
            Width = 280, Height = 220, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(-90, 0, 0, -100), Opacity = 0.5, IsHitTestVisible = false,
            Fill = new RadialGradientBrush(Color.FromArgb(0xFF, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B)),
        });

        var body = new Grid { Margin = new Thickness(18, 15, 16, 13) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition());
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        top.Children.Add(TheDay(now));
        var month = Month(now);
        Grid.SetColumn(month, 1);
        top.Children.Add(month);
        body.Children.Add(top);
        var bottom = Bottom(now, h24);
        Grid.SetRow(bottom, 2);
        body.Children.Add(bottom);
        _inner.Children.Add(body);
    }

    /// <summary>The chosen day, poster-sized: "15", "TODAY · SUNDAY", "NOVEMBER 2026".</summary>
    FrameworkElement TheDay(DateTime now)
    {
        bool today = _selected == now.Date;
        var stack = new StackPanel();
        var big = Sheet.Num(_selected.Day.ToString(Inv), 74, today ? Ink.A : null);
        big.LineHeight = 64;
        big.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        stack.Children.Add(big);
        var name = CardKit.Label((today ? "Today · " : "") + _selected.ToString("dddd", Inv), CardKit.Ink, 11);
        name.Margin = new Thickness(2, 8, 0, 0);
        stack.Children.Add(name);
        var year = CardKit.Label(_selected.ToString("MMMM yyyy", Inv), CardKit.Ink2, 9.5);
        year.Margin = new Thickness(2, 2, 0, 0);
        stack.Children.Add(year);
        return stack;
    }

    /// <summary>"‹ NOV 2026 ›", the weekday letters, and the month as circles.</summary>
    FrameworkElement Month(DateTime now)
    {
        var stack = new StackPanel();
        var nav = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        nav.Children.Add(Round("‹", "Previous month", () => Flip(-1), HorizontalAlignment.Left));
        var label = CardKit.Label(_month.ToString("MMM yyyy", Inv), CardKit.Ink, 10.5);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        nav.Children.Add(label);
        nav.Children.Add(Round("›", "Next month", () => Flip(1), HorizontalAlignment.Right));
        stack.Children.Add(nav);

        var heads = new UniformGrid7();
        string[] letters = { "M", "T", "W", "T", "F", "S", "S" };
        for (int i = 0; i < 7; i++)
        {
            var t = CardKit.Label(letters[i], i >= 5 ? Ink.Soft : CardKit.Ink2, 9);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Height = 14;
            heads.Children.Add(t);
        }
        stack.Children.Add(heads);

        var grid = new UniformGrid7();
        foreach (var d in CalendarAgenda.MonthGrid(_month.Year, _month.Month)) grid.Children.Add(Day(d, now));
        stack.Children.Add(grid);
        return stack;
    }

    static Button Round(string glyph, string name, Action click, HorizontalAlignment side)
    {
        var t = CardKit.Text(glyph, 12, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, -2, 0, 0);
        var b = CardKit.Tile(name, t, click, 22, 22, 11);
        b.HorizontalAlignment = side;
        return b;
    }

    /// <summary>One day: a 10 px dot that opens into a 20 px numbered circle on hover, when chosen, and today.</summary>
    FrameworkElement Day(DateTime d, DateTime now)
    {
        bool inMonth = d.Month == _month.Month;
        if (!inMonth) return new FrameworkElement { Height = Open + 2 };
        bool today = d == now.Date;
        bool chosen = d == _selected;
        var items = DayItems(d);
        var ring = items.Count > 0 ? KindBrush(items[0]) : null;

        var circle = new Border { Width = Dot, Height = Dot, CornerRadius = new CornerRadius(Open / 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var number = CardKit.Text(d.Day.ToString(Inv), 10, today ? FontWeights.Bold : FontWeights.SemiBold);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        number.Margin = new Thickness(0, -1, 0, 0);
        circle.Child = number;

        void Rest()
        {
            bool open = today || chosen;
            circle.Width = circle.Height = open ? Open : Dot;
            number.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            circle.BorderThickness = new Thickness(0);
            circle.BorderBrush = null;
            if (today)
            {
                var a = Ink.A.Color;
                circle.Background = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.35, 0.3), Center = new Point(0.4, 0.35), RadiusX = 0.85, RadiusY = 0.85,
                    GradientStops = { new GradientStop(Lighten(a, 0.6), 0), new GradientStop(a, 0.5), new GradientStop(Darken(a, 0.3), 1) },
                };
                number.Foreground = CardKit.OnAccent;
            }
            else if (chosen)
            {
                circle.Background = Ui.Tokens.Brush("#1AFFFFFF");
                circle.BorderBrush = CardKit.Ink;
                circle.BorderThickness = new Thickness(1.5);
                number.Foreground = CardKit.Ink;
            }
            else if (ring is not null)
            {
                circle.Background = null;
                circle.BorderBrush = ring;
                circle.BorderThickness = new Thickness(2);
            }
            else circle.Background = d < now.Date ? Past : Future;
        }
        Rest();

        var face = new Grid { Width = Open + 4, Height = Open + 2 };
        if (today) face.Children.Add(Breath(Ink.A.Color));
        else if (chosen && ring is not null)
        {
            // The chosen day keeps a faint halo of its event's colour.
            var r = ring.Color;
            face.Children.Add(new Ellipse { Width = Open + 6, Height = Open + 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Color.FromArgb(0x55, r.R, r.G, r.B)) });
        }
        face.Children.Add(circle);

        var name = d.ToString("dddd d MMMM", Inv) + (items.Count == 0 ? "" : items.Count == 1 ? ", 1 event" : $", {items.Count} events");
        var b = CardKit.Bare(name, face, () => Select(d), Open + 4, Open + 2);
        b.HorizontalAlignment = HorizontalAlignment.Center;
        b.MouseEnter += (_, _) =>
        {
            // The opening circle is the hover; the button's own hover pill would draw a second one behind it.
            ((Border)b.Content).Background = null;
            if (today || chosen) return;
            circle.Width = circle.Height = Open;
            circle.BorderThickness = new Thickness(0);
            circle.Background = Hover;
            number.Foreground = CardKit.Ink;
            number.Visibility = Visibility.Visible;
        };
        b.MouseLeave += (_, _) => Rest();
        return b;
    }

    /// <summary>Today's soft glow, breathing slowly while the wing is out (a few frames a second).</summary>
    FrameworkElement Breath(Color a)
    {
        var glow = new Ellipse
        {
            Width = Open + 10, Height = Open + 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Fill = new RadialGradientBrush(Color.FromArgb(0x8C, a.R, a.G, a.B), Color.FromArgb(0, a.R, a.G, a.B)),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1), IsHitTestVisible = false,
        };
        if (!MotionPrefs.Lively) return glow;
        var scale = (ScaleTransform)glow.RenderTransform;
        var breathe = new DoubleAnimation(0.85, 1.2, TimeSpan.FromSeconds(1.6)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Timeline.SetDesiredFrameRate(breathe, 10);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        _stop.Add(() =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        });
        return glow;
    }

    /// <summary>Across the bottom: the chosen day's events (two at most), then the day as a thin line with a now needle.</summary>
    FrameworkElement Bottom(DateTime now, bool h24)
    {
        var stack = new StackPanel();
        var items = DayItems(_selected);
        bool isToday = _selected == now.Date;
        bool hidden = PrivacyState.Presenting;

        if (items.Count == 0 && Links.Calendar is { Connected: false })
        {
            var row = new DockPanel { Height = 26 };
            var connect = CardKit.PrimaryPill("Connect", "Connect Google Calendar", () =>
            {
                FoldWanted?.Invoke();
                Links.Calendar?.OpenSetup();
            }, 22, 11);
            DockPanel.SetDock(connect, System.Windows.Controls.Dock.Right);
            row.Children.Add(connect);
            row.Children.Add(Sheet.Meta("Your Google Calendar's events show here", 11.5));
            stack.Children.Add(row);
        }
        else if (items.Count == 0)
        {
            var free = Sheet.Meta(isToday ? "Nothing planned · a free day" : "Nothing planned", 11.5);
            free.Height = 22;
            stack.Children.Add(free);
        }
        else
        {
            var meeting = isToday ? items.FirstOrDefault(o => !o.AllDay && o.HasLink && o.End > now) : null;
            foreach (var o in items.Take(2)) stack.Children.Add(EventRow(o, now, h24, hidden, isToday, o == meeting));
        }

        // The day as a line: each timed event a short bar, and a needle at now (today only).
        var (startH, endH) = CalendarAgenda.TimelineHours(items, _selected);
        double span = (endH - startH) * 60.0, width = CalendarWingFit.Width - 36;
        double X(DateTime t) => Math.Clamp(((t - _selected).TotalMinutes - startH * 60) / span, 0, 1) * width;
        var line = new Canvas { Height = 6, Margin = new Thickness(0, 6, 0, 0), Background = null };
        line.Children.Add(new Rectangle { Width = width, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Line });
        foreach (var o in items.Where(o => !o.AllDay))
        {
            double x0 = X(o.Start), x1 = Math.Max(X(o.End), x0 + 8);
            line.Children.Add(Place(new Rectangle { Width = x1 - x0, Height = 6, RadiusX = 3, RadiusY = 3, Fill = KindBrush(o) }, x0, 0));
        }
        if (isToday && now.Hour >= startH && now.Hour < endH)
            line.Children.Add(Place(new Rectangle { Width = 2, Height = 14, RadiusX = 1, RadiusY = 1, Fill = CardKit.Ink }, X(now) - 1, -4));
        stack.Children.Add(line);
        return stack;
    }

    FrameworkElement EventRow(CalendarOccurrence o, DateTime now, bool h24, bool hidden, bool isToday, bool join)
    {
        var row = new DockPanel { Height = 24, LastChildFill = true };
        row.Children.Add(Docked(new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = KindBrush(o), Margin = new Thickness(0, 0, 8, 0) }, System.Windows.Controls.Dock.Left));
        if (join)
        {
            var pill = CardKit.PrimaryPill("Join", "Join " + (hidden ? "meeting" : o.Title), () =>
            {
                Links.Calendar?.Join(o);
                FoldWanted?.Invoke();
            }, 20, 11);
            pill.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(Docked(pill, System.Windows.Controls.Dock.Right));
        }
        if (isToday && !o.AllDay && o.End > now)
            row.Children.Add(Docked(Sheet.Meta(CalendarText.Until(o.Start, o.End, now).Replace(" h ", "h ").Replace(" min", "m"), 11), System.Windows.Controls.Dock.Right));
        var words = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var title = Sheet.Word(hidden ? "Busy" : o.Title, 12.5, FontWeights.SemiBold);
        title.MaxWidth = 170;
        words.Children.Add(title);
        var when = Sheet.Meta(o.AllDay ? "all day" : CalendarText.Clock(o.Start, h24) + (h24 ? "" : " " + CalendarText.AmPm(o.Start).ToLowerInvariant()), 11);
        when.Margin = new Thickness(7, 1, 0, 0);
        words.Children.Add(when);
        row.Children.Add(words);
        return row;
    }

    static T Docked<T>(T e, System.Windows.Controls.Dock side) where T : UIElement
    {
        DockPanel.SetDock(e, side);
        return e;
    }

    static UIElement Place(UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        return e;
    }

    static Color Lighten(Color c, double t) => Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));

    static Color Darken(Color c, double t) => Color.FromRgb((byte)(c.R * (1 - t)), (byte)(c.G * (1 - t)), (byte)(c.B * (1 - t)));

    void StopLoops()
    {
        foreach (var stop in _stop) stop();
        _stop.Clear();
    }

    /// <summary>Seven equal columns.</summary>
    sealed class UniformGrid7 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid7() => Columns = 7;
    }
}
