using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Shammy.Core.Calendar;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.Calendar;

/// <summary>
/// The calendar card on the Focus Wall (Wall.dc.html): a tile on the date's deep-teal field — the date, the next
/// meeting's time as a poster and, dimmed, its title; focused, the title, "in 10 min · Meet" and Join (the primary
/// control) when it has a call link. The detail (the card family's shape) leads with the next meeting as a hero tile
/// with a countdown and Join, then what's left today and coming up as slim lines; before anything is connected it
/// is the connect form. Times follow the user's 12/24-hour clock; on the tile a failed refresh says so in the
/// meaning red.
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

    /// <summary>The opened card: today's agenda when connected (620 wide), else the connect form (560 wide).</summary>
    public FrameworkElement BuildDetail() =>
        !_module.Connected && _module.Items.Count == 0 ? new ConnectSheet(_module) : new AgendaSheet(_module);

    /// <summary>
    /// "Today · 3 left": the next meeting as the hero tile (when it starts, in light numbers in the accent, its
    /// title, time and app, and Join as the accent capsule when it has a call link), then the rest of today and the
    /// next days as slim lines: the time in light numbers, a palette dot, the title in bold. Nothing coming up is
    /// one short line. A failed refresh says so in a quiet line at the bottom.
    /// </summary>
    sealed class AgendaSheet : CardSheet
    {
        const double SheetWidth = 620;
        const int LinesShown = 4;

        readonly CalendarModule _module;

        public AgendaSheet(CalendarModule module) : base("Today", SheetWidth)
        {
            _module = module;
            BodyGap = 12;
            Refresh();
            // "Starts in 12 min" only has to be right while it's looked at.
            CardKit.TickWhileVisible(this, TimeSpan.FromSeconds(30), Refresh);
        }

        void Refresh()
        {
            var now = DateTime.Now;
            var items = _module.Items;
            bool h24 = CardKit.H24;
            var next = CalendarAgenda.Next(items, now);
            int left = CalendarAgenda.OnDay(items, now).Count(o => !o.AllDay && o.End > now);
            var status = new List<string> { left > 0 ? $"{left} left" : "free" };
            if (_module.SyncProblem is not null) status.Add("couldn’t refresh");
            Status = string.Join(" · ", status);

            var col = new StackPanel();
            if (next is null)
            {
                var line = Sheet.Meta("", 14);
                line.Inlines.Add(CardKit.Run("Nothing coming up. ", FontWeights.Bold, CardKit.Ink));
                line.Inlines.Add(new System.Windows.Documents.Run("Your week is clear."));
                line.Margin = new Thickness(6, 0, 0, 0);
                col.Children.Add(line);
            }
            else col.Children.Add(Hero(next, now, h24));

            var lines = Lines(items, next, now, h24);
            if (lines.Count > 0)
            {
                var list = new StackPanel { Margin = new Thickness(6, 12, 6, 0) };
                Brush[] dots = { Ink.B, Ink.C, Ink.A };
                for (int i = 0; i < lines.Count; i++)
                {
                    var row = Line(lines[i].Time, lines[i].Name, lines[i].Meta, dots[i % dots.Length]);
                    if (i > 0) row.Margin = new Thickness(0, 8, 0, 0);
                    list.Children.Add(row);
                }
                col.Children.Add(list);
            }

            string? foot = _module.SyncProblem
                ?? (_module.FetchedAt is { } f && now - f > TimeSpan.FromMinutes(30) ? $"Updated {CalendarText.ClockAmPm(f, h24)}" : null);
            if (foot is not null)
            {
                var quiet = Sheet.Meta(foot, 12);
                quiet.Margin = new Thickness(6, 12, 0, 0);
                quiet.ToolTip = foot;
                col.Children.Add(quiet);
            }
            Body.Children.Clear();
            Body.Children.Add(col);
            CardKit.Name(this, next is null ? "Calendar: nothing coming up" : $"Calendar: next {next.Title} at {CalendarText.ClockAmPm(next.Start, h24)}, {CalendarText.Until(next.Start, next.End, now)}");
        }

        /// <summary>The next meeting: when (light, in the accent), what, and Join.</summary>
        FrameworkElement Hero(CalendarOccurrence next, DateTime now, bool h24)
        {
            bool today = next.Start.Date == now.Date || next.Start <= now;
            string label, number, unit;
            if (next.Start <= now)
            {
                int mins = (int)Math.Ceiling((next.End - now).TotalMinutes - 1e-9);
                (label, number, unit) = mins < 60 ? ("Now · ends in", mins.ToString(Inv), "min") : ("Now · ends at", CalendarText.Clock(next.End, h24), CalendarText.AmPm(next.End, h24));
            }
            else if (today && next.Start - now < TimeSpan.FromMinutes(60))
                (label, number, unit) = ("Starts in", Math.Max(1, (int)Math.Ceiling((next.Start - now).TotalMinutes - 1e-9)).ToString(Inv), "min");
            else
                (label, number, unit) = (today ? "Starts at" : next.Start.Date == now.Date.AddDays(1) ? "Tomorrow" : next.Start.ToString("dddd", Inv),
                    CalendarText.Clock(next.Start, h24), CalendarText.AmPm(next.Start, h24));

            var when = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            when.Children.Add(Sheet.Meta(label, 12));
            var num = Sheet.Num("", 44, Ink.Soft);
            num.TextTrimming = TextTrimming.None;
            num.Inlines.Add(new System.Windows.Documents.Run(number));
            if (unit.Length > 0) num.Inlines.Add(new System.Windows.Documents.Run(" " + unit) { FontSize = 18 });
            num.Margin = new Thickness(0, 4, 0, 0);
            when.Children.Add(num);

            var what = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
            what.Children.Add(Sheet.Word(next.Title, 18));
            string range = next.Start.Date == now.Date ? CalendarText.Range(next.Start, next.End, h24) : $"{next.Start.ToString("ddd", Inv)} {CalendarText.Range(next.Start, next.End, h24)}";
            var meta = Sheet.Meta(string.Join(" · ", new[] { range, next.Provider }.Where(s => !string.IsNullOrEmpty(s))));
            meta.Margin = new Thickness(0, 3, 0, 0);
            what.Children.Add(meta);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(when);
            Grid.SetColumn(what, 1);
            grid.Children.Add(what);
            if (next.HasLink && today)
            {
                var join = Sheet.Cap("Join", $"Join {next.Title} on {next.Provider}", () => _module.Join(next), CardKit.Paths.Video, accent: true, height: 40);
                Grid.SetColumn(join, 2);
                grid.Children.Add(join);
            }
            return Sheet.Tile(grid, new Thickness(16, 14, 16, 14));
        }

        /// <summary>
        /// What else is on: today's all-day events, the rest of today, today's reminders from Notes, then the next
        /// days, at most four lines.
        /// </summary>
        static List<(string Time, string Name, string Meta)> Lines(IReadOnlyList<CalendarOccurrence> items, CalendarOccurrence? next, DateTime now, bool h24)
        {
            var rows = new List<(string Time, string Name, string Meta)>();
            foreach (var o in CalendarAgenda.OnDay(items, now).Where(o => o.AllDay)) rows.Add(("All day", o.Title, o.Provider ?? ""));
            foreach (var o in CalendarAgenda.OnDay(items, now).Where(o => !o.AllDay && o.End > now && o != next))
                rows.Add((CalendarText.ClockAmPm(o.Start, h24), o.Title, string.Join(" · ", new[] { o.Provider, CalendarText.Length(o) }.Where(s => !string.IsNullOrEmpty(s)))));
            if (Connect.Links.Notes is { } notes)
                foreach (var r in Shammy.Core.Connect.DayLinks.RemindersToday(notes.Reminders, r => r.Due, r => r.Done, now, 2))
                    rows.Add((r.Due <= now ? "Due" : CalendarText.ClockAmPm(r.Due, h24), Shammy.Core.Connect.PrivacyText.Reminder(r.Title, Infrastructure.PrivacyState.Presenting), "Reminder"));
            for (int i = 1; i <= 3 && rows.Count < LinesShown; i++)
            {
                var dayDate = now.Date.AddDays(i);
                foreach (var o in CalendarAgenda.OnDay(items, dayDate).Where(o => (o.Start >= dayDate || o.AllDay) && o != next).Take(LinesShown - rows.Count))
                    rows.Add((dayDate.ToString("ddd", Inv), o.Title, o.AllDay ? "All day" : CalendarText.ClockAmPm(o.Start, h24)));
            }
            return rows.Take(LinesShown).ToList();
        }

        /// <summary>A slim line: the time in light numbers, a palette dot, the title in bold, a quiet note.</summary>
        static FrameworkElement Line(string time, string name, string meta, Brush dot)
        {
            var grid = new Grid { MinHeight = 22 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(Sheet.Num(time, 18));
            var mark = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = dot, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(mark, 1);
            grid.Children.Add(mark);
            var title = Sheet.Word(name, 14);
            title.MaxWidth = 330;
            Grid.SetColumn(title, 2);
            grid.Children.Add(title);
            if (meta.Length > 0)
            {
                var note = Sheet.Meta(meta, 12);
                note.Margin = new Thickness(10, 0, 0, 0);
                Grid.SetColumn(note, 3);
                grid.Children.Add(note);
            }
            grid.ToolTip = string.Join(" · ", new[] { time, name, meta }.Where(s => s.Length > 0));
            return grid;
        }
    }

    /// <summary>
    /// "Connect Google Calendar" before anything is connected: "How?" shows Google's steps in place; one field
    /// for the secret address and Connect (the accent), "Stays on this PC, encrypted" under the field. Enter
    /// connects; a problem replaces that line.
    /// </summary>
    sealed class ConnectSheet : CardSheet
    {
        const double SheetWidth = 560;
        const string Safe = "Stays on this PC, encrypted. Shammy only reads it.";

        readonly CalendarModule _module;
        readonly TextBox _box = new() { MaxLength = 2048 };
        readonly TextBlock _status = Sheet.Meta(Safe, 12), _help;
        readonly Button _connect;
        bool _busy;

        public ConnectSheet(CalendarModule module) : base("Connect Google Calendar", SheetWidth)
        {
            _module = module;
            BodyGap = 12;
            Action(Sheet.Cap("How?", "How to find the address", ToggleHelp));

            _help = Sheet.Meta("In Google Calendar: Settings › your calendar › Integrate calendar › copy the “Secret address in iCal format”.", 12);
            _help.TextTrimming = TextTrimming.None;
            _help.TextWrapping = TextWrapping.Wrap;
            _help.Margin = new Thickness(2, 0, 0, 12);
            _help.Visibility = Visibility.Collapsed;

            System.Windows.Automation.AutomationProperties.SetName(_box, "Secret iCal address");
            _box.KeyDown += (_, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                e.Handled = true;
                _ = ConnectAsync();
            };
            var field = Sheet.Field(_box, "Paste the secret iCal address", CardKit.Paths.Link, 38);
            _connect = Sheet.Cap("Connect", "Connect Google Calendar", () => _ = ConnectAsync(), accent: true, height: 38);
            _connect.Margin = new Thickness(8, 0, 0, 0);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(field);
            Grid.SetColumn(_connect, 1);
            row.Children.Add(_connect);

            _status.Margin = new Thickness(14, 8, 0, 0);
            var col = new StackPanel();
            col.Children.Add(_help);
            col.Children.Add(row);
            col.Children.Add(_status);
            Body.Children.Add(col);
        }

        void ToggleHelp() => _help.Visibility = _help.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        async Task ConnectAsync()
        {
            if (_busy) return;
            _busy = true;
            _status.Foreground = CardKit.Ink2;
            _status.Text = "Checking…";
            _connect.Opacity = 0.6;
            var error = await _module.ConnectAsync(_box.Text);
            _busy = false;
            _connect.Opacity = 1;
            if (error is null)
            {
                _status.Text = "Connected";
                _box.Clear();
                return;
            }
            _status.Text = error;
            _status.ToolTip = error;
            _status.Foreground = Ink.Soft;
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
