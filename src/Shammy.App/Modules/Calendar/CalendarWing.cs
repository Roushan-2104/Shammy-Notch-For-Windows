using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Shammy.Core.Calendar;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules.Connect;
using Shammy.Views.Cards;

namespace Shammy.Modules.Calendar;

/// <summary>
/// The calendar wing (the "glass month"): tap the date in the open island's header and a slab of the island's own
/// glass, as tall as the island, opens on its right with a small dot in the gap between them. On top, the month
/// with its day numbers: today a filled light pill, the chosen day outlined, days gone by dimmer, small dots under
/// days with plans in the plans' colours. Below, the chosen day's plans with their times (finished ones faded, "in 3h"
/// and Join today). Its own small topmost window, never activated (the island keeps the keyboard); the notch places it
/// and holds the island open while the pointer is on it.
/// </summary>
internal sealed class CalendarWing : Window
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static readonly Brush Hover = Ui.Tokens.Brush("#29FFFFFF"), Line = Ui.Tokens.Brush("#17FFFFFF");

    readonly Grid _layout = new();
    readonly Ellipse _dot = new();
    readonly Border _slab = new();
    readonly Grid _inner = new();
    readonly ScaleTransform _pour = new(1, 1), _dotPour = new(1, 1);
    IntPtr _hwnd;
    DateTime _month, _selected;

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
        // A simple dot in the gap, level with the island's top row.
        _dot.Width = _dot.Height = 5;
        _dot.Fill = IslandSurface.Fill;
        _dot.Stroke = IslandSurface.Rim;
        _dot.StrokeThickness = 1;
        _dot.HorizontalAlignment = HorizontalAlignment.Center;
        _dot.VerticalAlignment = VerticalAlignment.Top;
        _dot.Margin = new Thickness(0, 15.5, 0, 0);
        _dot.RenderTransformOrigin = new Point(0.5, 0.5);
        _dot.RenderTransform = _dotPour;
        _layout.Children.Add(_dot);

        _slab.BorderThickness = new Thickness(1);
        _slab.BorderBrush = IslandSurface.Rim;
        _slab.Width = CalendarWingFit.Width;
        _slab.RenderTransformOrigin = new Point(0, 0.5);
        _slab.RenderTransform = _pour;
        _slab.Child = _inner;
        Grid.SetColumn(_slab, 1);
        _layout.Children.Add(_slab);
        Content = _layout;
        // Under frosted glass the slab and the dot are drawn by the glass behind them.
        _slabGlass = new Shell.Glass.GlassPiece("wing.calendar", _slab, () => _slab.CornerRadius.TopLeft);
        _dotGlass = new Shell.Glass.GlassPiece("wing.calendar.dot", _dot, () => _dot.Width / 2);
        Shell.Glass.GlassSurfaces.RunningChanged += GlassFills;
        Closed += (_, _) =>
        {
            Shell.Glass.GlassSurfaces.RunningChanged -= GlassFills;
            Shell.Glass.GlassSurfaces.Forget(_hwnd);
            _slabGlass.Remove();
            _dotGlass.Remove();
        };
        GlassFills();

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

    /// <summary>Its window handle, so the notch can move it together with the island.</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>For the test pipe: "open month=2026-11 selected=2026-11-15 events=2 scale=0.88".</summary>
    public string Describe() => IsOpen
        ? string.Create(Inv, $"open month={_month:yyyy-MM} selected={_selected:yyyy-MM-dd} events={DayItems(_selected).Count} scale={_scale:0.##}")
        : "closed";

    double _scale = 1;

    /// <summary>
    /// Opens the wing on today. <paramref name="height"/> and <paramref name="radius"/> are the island's, in the wing's
    /// own units. The slab is the island's own glass (<see cref="IslandSurface"/>).
    /// </summary>
    public void Pour(double scale, double height, double radius, bool excludeFromCapture)
    {
        _scale = scale;
        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        _selected = today;
        _layout.LayoutTransform = new ScaleTransform(scale, scale);
        _slab.Height = height;
        _slab.CornerRadius = new CornerRadius(radius);
        GlassFills();
        // Nothing inside spills past the rounded corners.
        _inner.Clip = new RectangleGeometry(new Rect(0, 0, CalendarWingFit.Width - 2, height - 2), radius - 1, radius - 1);
        Build();
        SetExcluded(excludeFromCapture);
        IsOpen = true;
        Show();
        Animate(open: true);
        TrackGlass(0.7);
    }

    readonly Shell.Glass.GlassPiece _slabGlass, _dotGlass;

    void GlassFills()
    {
        bool frosted = Shell.Glass.GlassSurfaces.Running;
        _slab.Background = frosted ? Shell.Glass.GlassSurfaces.HitOnly : IslandSurface.Fill;
        _slab.BorderBrush = frosted ? Brushes.Transparent : IslandSurface.Rim;
        _dot.Fill = frosted ? Shell.Glass.GlassSurfaces.HitOnly : IslandSurface.Fill;
        _dot.Stroke = frosted ? Brushes.Transparent : IslandSurface.Rim;
    }

    void TrackGlass(double seconds)
    {
        _slabGlass.Track(seconds);
        _dotGlass.Track(seconds);
    }

    /// <summary>Folds the wing back into the island (at once with <paramref name="animate"/> off).</summary>
    public void Fold(bool animate)
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (!animate || !MotionPrefs.Lively) Hide();
        else Animate(open: false);
        TrackGlass(0.7);
    }

    /// <summary>Kept out of captures exactly when the island is (Stage, a recording, Shammy's own screenshot or freeze).</summary>
    internal void SetExcluded(bool exclude)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    /// <summary>Moves the wing so its dot sits just right of the island's right edge (device pixels).</summary>
    public void PlaceAt(int left, int top)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Shell.Glass.GlassSurfaces.Join(_hwnd);
        TrackGlass(0.3);
    }

    void Animate(bool open)
    {
        if (!MotionPrefs.Lively)
        {
            // A held fold from an earlier Lively close would otherwise outlive these values and keep the wing invisible.
            _dotPour.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _dotPour.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _pour.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _slab.BeginAnimation(OpacityProperty, null);
            _pour.ScaleX = _dotPour.ScaleX = _dotPour.ScaleY = _slab.Opacity = 1;
            return;
        }
        int ms = open ? 360 : 180;
        IEasingFunction ease = open ? new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseIn };
        // The dot beads out of the island first, then the slab swells from beside it.
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
        _dotPour.BeginAnimation(ScaleTransform.ScaleXProperty, neck);
        _dotPour.BeginAnimation(ScaleTransform.ScaleYProperty, neck);
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

    /// <summary>The months whose events are kept (see <see cref="CalendarAgenda.FetchWindow"/>): the arrows stop there, so a
    /// month is never shown as "Nothing planned" just because it was never fetched.</summary>
    static (DateTime First, DateTime Last) Months()
    {
        var (from, to) = CalendarAgenda.FetchWindow(DateTime.Now);
        var last = to.AddDays(-1);
        return (new DateTime(from.Year, from.Month, 1), new DateTime(last.Year, last.Month, 1));
    }

    void Flip(int months)
    {
        var (first, last) = Months();
        var target = _month.AddMonths(months);
        if (target < first || target > last) return;
        _month = target;
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
        _inner.Children.Clear();
        var now = DateTime.Now;
        var body = new DockPanel { Margin = new Thickness(16, 10, 16, 8), LastChildFill = true };
        var month = Month(now, out int rows);
        DockPanel.SetDock(month, System.Windows.Controls.Dock.Top);
        body.Children.Add(month);
        body.Children.Add(Agenda(now, CardKit.H24, rows));
        _inner.Children.Add(body);
    }

    /// <summary>"November 2026  ‹ ›", the weekday letters, and the month as numbers (only the weeks it has).</summary>
    FrameworkElement Month(DateTime now, out int rows)
    {
        var stack = new StackPanel();
        var head = new DockPanel { Height = 24, Margin = new Thickness(2, 0, 0, 1) };
        var (first, last) = Months();
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        arrows.Children.Add(Ends(Round("‹", "Previous month", () => Flip(-1)), _month > first));
        var next = Ends(Round("›", "Next month", () => Flip(1)), _month < last);
        next.Margin = new Thickness(4, 0, 0, 0);
        arrows.Children.Add(next);
        DockPanel.SetDock(arrows, System.Windows.Controls.Dock.Right);
        head.Children.Add(arrows);
        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Sheet.Word(_month.ToString("MMMM", Inv), 15, FontWeights.Bold));
        var year = Sheet.Meta(_month.ToString(" yyyy", Inv), 13);
        year.VerticalAlignment = VerticalAlignment.Bottom;
        year.Margin = new Thickness(4, 0, 0, 1);
        name.Children.Add(year);
        head.Children.Add(name);
        stack.Children.Add(head);

        var heads = new UniformGrid7();
        string[] letters = { "M", "T", "W", "T", "F", "S", "S" };
        for (int i = 0; i < 7; i++)
        {
            var t = CardKit.Label(letters[i], i >= 5 ? Ink.Soft : CardKit.Ink2, 9.5);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.Height = 14;
            heads.Children.Add(t);
        }
        stack.Children.Add(heads);

        var days = CalendarAgenda.MonthGrid(_month.Year, _month.Month);
        // Only the weeks the month reaches into (five, sometimes four or six): the room left goes to the day's plans.
        rows = 0;
        for (int w = 0; w < 6; w++) if (days[w * 7].Month == _month.Month || days[w * 7 + 6].Month == _month.Month) rows = w + 1;
        var grid = new UniformGrid7();
        foreach (var d in days.Take(rows * 7)) grid.Children.Add(Day(d, now));
        stack.Children.Add(grid);
        return stack;
    }

    /// <summary>An arrow at the end of the kept months: faint and inert.</summary>
    static Button Ends(Button b, bool can)
    {
        b.IsEnabled = can;
        b.Opacity = can ? 1 : 0.3;
        return b;
    }

    static Button Round(string glyph, string name, Action click)
    {
        var t = CardKit.Text(glyph, 13, FontWeights.SemiBold);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, -2, 0, 0);
        return CardKit.Tile(name, t, click, 24, 24, 12);
    }

    const double CellW = 32, CellH = 17, RowH = 21;
    static readonly Brush TodayInk = Ui.Tokens.Brush("#141416");

    /// <summary>
    /// One day: its number in a small pill. Today is a filled light pill, the chosen day is outlined, days gone by are
    /// dimmer, and a day with plans has up to two small dots under its number in the plans' colours.
    /// </summary>
    FrameworkElement Day(DateTime d, DateTime now)
    {
        if (d.Month != _month.Month) return new FrameworkElement { Height = RowH };
        bool today = d == now.Date, chosen = d == _selected;
        var items = DayItems(d);

        var pill = new Border { Width = CellW, Height = CellH, CornerRadius = new CornerRadius(CellH / 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        var number = CardKit.Text(d.Day.ToString(Inv), 11, today ? FontWeights.Bold : FontWeights.Medium);
        number.HorizontalAlignment = HorizontalAlignment.Center;
        number.VerticalAlignment = VerticalAlignment.Center;
        number.Margin = new Thickness(0, -1, 0, 0);
        pill.Child = number;

        void Rest()
        {
            pill.BorderThickness = new Thickness(0);
            if (today)
            {
                pill.Background = CardKit.Ink;
                number.Foreground = TodayInk;
            }
            else
            {
                pill.Background = null;
                number.Foreground = CardKit.Ink;
                number.Opacity = d < now.Date ? 0.42 : 1;
                if (chosen)
                {
                    pill.BorderBrush = CardKit.Ink;
                    pill.BorderThickness = new Thickness(1.2);
                }
            }
        }
        Rest();

        var face = new Grid { Width = CellW + 4, Height = RowH };
        face.Children.Add(pill);
        if (items.Count > 0 && !today)
        {
            var dots = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0) };
            foreach (var kind in items.Select(KindBrush).Distinct().Take(2))
                dots.Children.Add(new Ellipse { Width = 3.5, Height = 3.5, Fill = kind, Margin = new Thickness(1, 0, 1, 0), Opacity = d < now.Date ? 0.5 : 1 });
            face.Children.Add(dots);
        }

        var name = d.ToString("dddd d MMMM", Inv) + (items.Count == 0 ? "" : items.Count == 1 ? ", 1 event" : $", {items.Count} events");
        var b = CardKit.Bare(name, face, () => Select(d), CellW + 4, RowH);
        b.HorizontalAlignment = HorizontalAlignment.Center;
        b.MouseEnter += (_, _) =>
        {
            // The pill is the hover; the button's own hover would draw a second shape behind it.
            ((Border)b.Content).Background = null;
            if (!today) pill.Background = Hover;
        };
        b.MouseLeave += (_, _) => Rest();
        return b;
    }

    /// <summary>
    /// Under the month: the chosen day's plans with their times, the ones that are over faded, Join on a call that's
    /// on today. As many as fit (two, or three in a five-week month); "+2 more" says when some don't.
    /// </summary>
    FrameworkElement Agenda(DateTime now, bool h24, int rows)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
        stack.Children.Add(new Rectangle { Height = 1, Fill = Line, Margin = new Thickness(-2, 4, 0, 4) });
        var items = DayItems(_selected);
        bool isToday = _selected == now.Date;
        bool hidden = PrivacyState.Presenting;
        int fits = CalendarWingFit.PlansThatFit(rows);

        var label = (isToday ? "Today · " : "") + _selected.ToString(isToday ? "dddd d" : "dddd d MMMM", Inv);
        if (items.Count > fits) label += $"  ·  +{items.Count - fits} more";
        var title = Sheet.Meta(label, 11.5);
        title.Margin = new Thickness(0, 0, 0, 1);
        stack.Children.Add(title);

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
            row.Children.Add(Sheet.Word("Your Google Calendar's plans show here", 12, FontWeights.Medium));
            stack.Children.Add(row);
        }
        else if (items.Count == 0)
        {
            stack.Children.Add(Sheet.Word(isToday ? "Nothing planned · a free day" : "Nothing planned", 12.5, FontWeights.Medium));
        }
        else
        {
            var meeting = isToday ? items.FirstOrDefault(o => !o.AllDay && o.HasLink && o.End > now) : null;
            foreach (var o in items.Take(fits)) stack.Children.Add(EventRow(o, now, h24, hidden, isToday, o == meeting));
        }
        return stack;
    }

    FrameworkElement EventRow(CalendarOccurrence o, DateTime now, bool h24, bool hidden, bool isToday, bool join)
    {
        var row = new DockPanel { Height = 24, LastChildFill = true, Opacity = isToday && !o.AllDay && o.End <= now ? 0.45 : 1 };
        row.Children.Add(Docked(new Rectangle { Width = 3, Height = 16, RadiusX = 1.5, RadiusY = 1.5, Fill = KindBrush(o), Margin = new Thickness(0, 0, 9, 0) }, System.Windows.Controls.Dock.Left));
        if (join)
        {
            var pill = CardKit.PrimaryPill("Join", "Join " + (hidden ? "meeting" : o.Title), () =>
            {
                Links.Calendar?.Join(o);
                FoldWanted?.Invoke();
            }, 20, 11);
            pill.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(Docked(pill, System.Windows.Controls.Dock.Right));
        }
        string when = o.AllDay ? "all day" : CalendarText.Clock(o.Start, h24) + (h24 ? "" : " " + CalendarText.AmPm(o.Start).ToLowerInvariant());
        if (isToday && !o.AllDay && o.End > now)
            when = CalendarText.Until(o.Start, o.End, now).Replace(" h", "h").Replace(" min", "m");
        var meta = Sheet.Meta(when, 11);
        meta.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(Docked(meta, System.Windows.Controls.Dock.Right));
        var title = Sheet.Word(hidden ? "Busy" : o.Title, 12.5, FontWeights.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(title);
        return row;
    }
    static T Docked<T>(T e, System.Windows.Controls.Dock side) where T : UIElement
    {
        DockPanel.SetDock(e, side);
        return e;
    }



    /// <summary>Seven equal columns.</summary>
    sealed class UniformGrid7 : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGrid7() => Columns = 7;
    }
}
