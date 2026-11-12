using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Shammy.Core.Calendar;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Calendar;

/// <summary>
/// The calendar card on the Focus Wall (Wall.dc.html): a tile on the date's deep-teal field — the date, the next
/// meeting's time as a poster and, dimmed, its title; focused, the title, "in 10 min · Meet" and Join (the primary
/// control) when it has a call link. The detail grows the field into the art panel with the day as a big number,
/// names the next meeting with Join in the corner, and shows the day as a timeline bar (white blocks, a now line)
/// over a list of what's left today and coming up. Times follow the user's 12/24-hour clock; a failed refresh says
/// so in the meaning red.
/// </summary>
internal sealed class CalendarCard
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly CalendarModule _module;
    readonly WallTile _tile = new();
    readonly TrackedText _date = CardKit.Label(""), _time = CardKit.Poster("", 40), _rest = CardKit.Label("");
    readonly TextBlock _title, _line;
    readonly ContentControl _join = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 10) };

    public CalendarCard(CalendarModule module)
    {
        _module = module;
        _tile.Field = Fields.Calendar();
        _date.HorizontalAlignment = _time.HorizontalAlignment = HorizontalAlignment.Left;
        _time.Margin = new Thickness(0, 8, 0, 0);
        var top = CardKit.Column(0, _date, _time);
        top.Margin = new Thickness(WallTile.Pad);
        top.VerticalAlignment = VerticalAlignment.Top;
        _tile.Art.Children.Add(top);
        _rest.HorizontalAlignment = HorizontalAlignment.Left;
        _tile.Rest.Children.Add(CardKit.At(_rest, HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad + 14 + 8 + 40 + 10, WallTile.Pad));
        var words = WallTile.Words("", "", out _title, out _line, Ui.Tokens.InkOnArt);
        words.Margin = new Thickness(WallTile.Pad, WallTile.Pad + 14 + 8 + 40 + 8, WallTile.Pad, 0);
        words.VerticalAlignment = VerticalAlignment.Top;
        _tile.Lit.Children.Add(words);
        _tile.Lit.Children.Add(_join);
        Refresh();
    }

    public FrameworkElement Compact => _tile;

    /// <summary>A meeting about to start (or just started with a call to join) wins the focus; later ones are quieter.</summary>
    public int Live
    {
        get
        {
            var now = DateTime.Now;
            if (CalendarAgenda.Next(_module.Items, now) is not { } next) return 0;
            var until = next.Start - now;
            return until <= TimeSpan.FromMinutes(15) ? 80 : until <= TimeSpan.FromHours(1) ? 30 : 0;
        }
    }

    /// <summary>The peek: "In 10 min · 10:30 Standup · Google Meet · 15 min", with Join (or the agenda).</summary>
    public WallPeek? Peek()
    {
        var now = DateTime.Now;
        if (CalendarAgenda.Next(_module.Items, now) is not { } next) return null;
        bool h24 = CardKit.H24;
        var kicker = next.Start <= now ? "Now" : next.Start.Date == now.Date ? Capital(CalendarText.Until(next.Start, next.End, now)) : next.Start.ToString("dddd", Inv);
        var line = string.Join(" · ", new[] { next.Provider, CalendarText.Length(next) }.Where(s => !string.IsNullOrEmpty(s)));
        WallAction? act = next.HasLink ? new WallAction("Join", () => _module.Join(next))
            : new WallAction("Agenda", () => _module.ShowDetail());
        return new WallPeek(kicker, $"{CalendarText.Clock(next.Start, h24)} {next.Title}", line, Fields.Calendar(), act);
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    // ---- Tile ------------------------------------------------------------------------------------

    public void Refresh()
    {
        var now = DateTime.Now;
        var items = _module.Items;
        bool h24 = CardKit.H24;
        _date.Text = now.ToString("ddd d MMM", Inv);
        SetJoin(null);

        if (!_module.Connected && items.Count == 0)
        {
            _time.Text = now.Day.ToString(Inv);
            _rest.Text = "Not connected";
            _title.Text = "Connect a calendar";
            _line.Text = "Google Calendar, read-only";
            SetJoin(CardKit.PrimaryPill("Connect", "Connect Google Calendar", _module.OpenSetup, 28, 12));
            CardKit.Name(_tile, "Calendar: not connected. Connect Google Calendar");
            return;
        }

        var next = CalendarAgenda.Next(items, now);
        if (next is null)
        {
            _time.Text = "Free";
            _rest.Text = "Nothing coming up";
            _title.Text = "Nothing coming up";
            _line.Text = _module.SyncProblem ?? "Your week is clear";
            CardKit.Name(_tile, "Calendar: nothing coming up");
            return;
        }

        bool today = next.Start.Date == now.Date;
        _time.Text = today ? CalendarText.Clock(next.Start, h24) : next.Start.ToString("ddd", Inv);
        _rest.Text = next.Title;
        _title.Text = next.Title;
        var until = CalendarText.Until(next.Start, next.End, now);
        _line.Text = _module.SyncProblem is not null ? "Couldn’t refresh"
            : today ? until + (next.Provider is { } p ? $" · {p}" : "") : CalendarText.ClockAmPm(next.Start, h24);
        _line.Foreground = _module.SyncProblem is not null ? CardKit.Alarm : Ui.Tokens.InkOnArt;
        if (next.HasLink) SetJoin(CardKit.PrimaryPill("Join", $"Join {next.Title} on {next.Provider}", () => _module.Join(next), 28, 12));
        CardKit.Name(_tile, $"Calendar: next {next.Title} at {CalendarText.ClockAmPm(next.Start, h24)}, {until}");
    }

    /// <summary>The primary pill bottom-right; the words beside it keep clear of it.</summary>
    void SetJoin(FrameworkElement? pill)
    {
        _join.Content = pill;
        if (_title.Parent is FrameworkElement words) words.Margin = new Thickness(WallTile.Pad, words.Margin.Top, pill is null ? WallTile.Pad : 84, 0);
    }

    // ---- Detail ----------------------------------------------------------------------------------

    public FrameworkElement BuildDetail()
    {
        var now = DateTime.Now;
        var items = _module.Items;

        // Not connected: the setup form under the notch's Back + title row.
        if (!_module.Connected && items.Count == 0)
            return new Grid { Children = { new CalendarSetup(_module, null, compact: true) { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560 } } };

        bool h24 = CardKit.H24;
        var next = CalendarAgenda.Next(items, now);
        bool nextToday = next is not null && next.Start.Date == now.Date;
        var d = new WallDetail(Fields.Calendar(detail: true),
            nextToday ? $"{now.ToString("ddd d MMM", Inv)} · {CalendarText.Until(next!.Start, next.End, now)}" : now.ToString("dddd d MMM", Inv),
            next?.Title ?? "Nothing coming up",
            next is null ? "Your week is clear"
                : string.Join(" · ", new[] { next.Start.Date == now.Date ? CalendarText.Range(next.Start, next.End, h24) : $"{next.Start.ToString("ddd", Inv)} {CalendarText.Range(next.Start, next.End, h24)}", next.Provider }.Where(s => !string.IsNullOrEmpty(s))));
        if (_module.SyncProblem is { } problem)
        {
            d.Line.Text = problem;
            d.Line.Foreground = CardKit.Alarm;
        }
        else if (_module.FetchedAt is { } f && now - f > TimeSpan.FromMinutes(30))
            d.Line.Text += $" · updated {CalendarText.ClockAmPm(f, h24)}";

        var weekday = CardKit.Label(now.ToString("dddd", Inv));
        var day = CardKit.Poster(now.Day.ToString(Inv), 112);
        day.Margin = new Thickness(0, 6, 0, 0);
        var month = CardKit.Label(now.ToString("MMMM yyyy", Inv));
        month.Margin = new Thickness(0, 6, 0, 0);
        d.ArtBottomLeft(weekday, day, month);

        if (next is { HasLink: true } && nextToday)
            d.Corner = CardKit.PrimaryPill("Join", $"Join {next.Title} on {next.Provider}", () => _module.Join(next), 38, 14);

        var today = CalendarAgenda.OnDay(items, now).Where(o => !o.AllDay).ToList();
        var body = new StackPanel();
        body.Children.Add(new Timeline(today, now, CalendarAgenda.TimelineHours(today, now), h24, next));
        body.Children.Add(Rows(items, now, h24));
        d.Body.Children.Add(body);
        return d;
    }

    /// <summary>What's left today (and today's reminders from Notes), then the next days, two columns of hairline rows.</summary>
    static FrameworkElement Rows(IReadOnlyList<CalendarOccurrence> items, DateTime now, bool h24)
    {
        var rows = new List<(string Time, string Name, string Meta)>();
        foreach (var o in CalendarAgenda.OnDay(items, now).Where(o => o.AllDay)) rows.Add(("All day", o.Title, o.Provider ?? ""));
        foreach (var o in CalendarAgenda.OnDay(items, now).Where(o => !o.AllDay && o.End > now))
            rows.Add((CalendarText.Clock(o.Start, h24), o.Title, string.Join(" · ", new[] { o.Provider, CalendarText.Length(o) }.Where(s => !string.IsNullOrEmpty(s)))));
        if (Connect.Links.Notes is { } notes)
            foreach (var r in Shammy.Core.Connect.DayLinks.RemindersToday(notes.Reminders, r => r.Due, r => r.Done, now, 2))
                rows.Add((r.Due <= now ? "Due" : CalendarText.Clock(r.Due, h24), Shammy.Core.Connect.PrivacyText.Reminder(r.Title, Infrastructure.PrivacyState.Presenting), "Reminder"));
        for (int i = 1; i <= 3 && rows.Count < 4; i++)
        {
            var dayDate = now.Date.AddDays(i);
            foreach (var o in CalendarAgenda.OnDay(items, dayDate).Where(o => o.Start >= dayDate || o.AllDay).Take(4 - rows.Count))
                rows.Add((dayDate.ToString("ddd", Inv), o.Title, o.AllDay ? "All day" : CalendarText.ClockAmPm(o.Start, h24)));
        }
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (time, name, meta) in rows.Take(4))
        {
            var t = CardKit.Text(time, 13, FontWeights.Bold);
            t.Width = 46;
            var row = WallDetail.ListRow(t, CardKit.Text(name, 13, FontWeights.SemiBold), CardKit.Text(meta, 12, FontWeights.Medium, secondary: true), 29);
            row.Margin = new Thickness(0, 0, grid.Children.Count % 2 == 0 ? 12 : 0, 0);
            grid.Children.Add(row);
        }
        if (rows.Count == 0)
        {
            var free = CardKit.Text("Nothing else on today", 13, FontWeights.Medium, secondary: true);
            free.Margin = new Thickness(0, 8, 0, 0);
            return free;
        }
        return grid;
    }

    /// <summary>The day as one bar: events as blocks (the next one white), a white now line, hour labels under it.</summary>
    sealed class Timeline : Grid
    {
        readonly List<CalendarOccurrence> _events;
        readonly DateTime _now;
        readonly int _startHour, _endHour;
        readonly bool _h24;
        readonly CalendarOccurrence? _next;
        readonly Canvas _bar = new() { Height = 20 }, _hours = new() { Height = 14 };

        public Timeline(List<CalendarOccurrence> events, DateTime now, (int Start, int End) hours, bool h24, CalendarOccurrence? next)
        {
            _events = events;
            _now = now;
            (_startHour, _endHour) = hours;
            _h24 = h24;
            _next = next;
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(19) });
            Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = CardKit.PillBg, BorderBrush = CardKit.Hairline, BorderThickness = new Thickness(1) });
            Children.Add(_bar);
            _hours.Margin = new Thickness(0, 5, 0, 0);
            SetRow(_hours, 1);
            Children.Add(_hours);
            SizeChanged += (_, e) =>
            {
                if (e.WidthChanged) Lay(e.NewSize.Width);
            };
        }

        static double Hours(DateTime t, DateTime day) => (t - day.Date).TotalHours;

        void Lay(double width)
        {
            _bar.Children.Clear();
            _hours.Children.Clear();
            if (width <= 0) return;
            double dayStart = _startHour, span = Math.Max(2, _endHour - _startHour);
            double X(double hour) => Math.Clamp((hour - dayStart) / span, 0, 1) * width;

            int step = span > 12 ? 4 : 2;
            for (int h = _startHour; h <= _endHour; h += step)
            {
                var label = CardKit.Text(CalendarText.HourLabel(h, _h24), 11, FontWeights.Medium, secondary: true);
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double lw = label.DesiredSize.Width, x = X(h);
                Canvas.SetLeft(label, h == _startHour ? 0 : h >= _endHour ? x - lw : x - lw / 2);
                _hours.Children.Add(label);
            }

            foreach (var e in _events)
            {
                double s = Hours(e.Start, _now), en = Hours(e.End, _now);
                if (en <= dayStart || s >= dayStart + span) continue;
                double left = X(s), right = X(en);
                var block = new Border
                {
                    Width = Math.Max(5, right - left - 1),
                    Height = 14,
                    CornerRadius = new CornerRadius(4),
                    Background = e == _next ? CardKit.Ink : CardKit.Hex("#4DF5F5F7"),
                    Opacity = e.End <= _now ? 0.5 : 1,
                    ToolTip = $"{e.Title} · {CalendarText.Range(e.Start, e.End, _h24)}",
                };
                Canvas.SetLeft(block, left);
                Canvas.SetTop(block, 3);
                _bar.Children.Add(block);
            }

            double nowH = Hours(_now, _now);
            if (nowH >= dayStart && nowH <= dayStart + span)
            {
                var line = new Border { Width = 2, Height = 28, CornerRadius = new CornerRadius(1), Background = CardKit.Ink, ToolTip = $"Now {CalendarText.ClockAmPm(_now, _h24)}" };
                Canvas.SetLeft(line, X(nowH) - 1);
                Canvas.SetTop(line, -4);
                _bar.Children.Add(line);
            }
        }
    }

    // ---- Test pipe --------------------------------------------------------------------------------

    public string Describe()
    {
        var now = DateTime.Now;
        var next = CalendarAgenda.Next(_module.Items, now);
        return $"calendar tile: date={_date.Text} poster={_time.Text} next={next?.Title ?? "-"} join={(_join.Content is null ? "no" : "yes")} live={Live} focused={_tile.IsLit}";
    }
}
