using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Productivity;
using Shammy.Views;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity;

/// <summary>What the timer card needs from the productivity module: its live clocks and the real actions.</summary>
internal interface ITimerCardHost
{
    IReadOnlyList<RunningClock> Clocks { get; }
    void TogglePause(RunningClock clock);
    void Stop(RunningClock clock);
    void Extend(RunningClock clock, TimeSpan more);
    void QuickStart(TimeSpan duration);
}

/// <summary>
/// The timer card on the Focus Wall (Wall.dc.html): a tile whose artwork is the ring itself — white, draining on a
/// dark dial — with the digits inside; focused, the name, "4:32 of 5:00", pause/resume (the primary control) and
/// stop. Its detail is a card sheet (<see cref="TimerSheet"/>): the clock as a big palette ring with pause, stop and
/// +1/+5 min, slim rows when several run, round quick starts when nothing does. A finished timer turns the tile's
/// dial the alarm red for a few seconds. Nothing here runs a clock of its own: the module's per-second tick
/// refreshes it while visible.
/// </summary>
internal sealed class TimerCard
{
    static readonly TimeSpan DoneShown = TimeSpan.FromSeconds(8);

    readonly ITimerCardHost _host;
    readonly WallTile _tile = new();
    readonly Ring _ring = new() { Width = 72, Height = 72, Thickness = 6 };
    readonly TrackedText _digits = CardKit.Big("", 18), _rest = CardKit.Label("");
    readonly TextBlock _name, _of;
    readonly StackPanel _buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 10, 0) };
    readonly DispatcherTimer _doneTimer = new(DispatcherPriority.Normal) { Interval = DoneShown };

    string? _done; // label of a timer that just finished, while its alarm state shows
    string _buttonsKey = "";
    TimerSheet? _detail;

    public TimerCard(ITimerCardHost host)
    {
        _host = host;
        _tile.Field = Fields.Timer();
        var dial = new Grid { Width = 72, Height = 72 };
        dial.Children.Add(_ring);
        _digits.HorizontalAlignment = HorizontalAlignment.Center;
        dial.Children.Add(_digits);
        _tile.Art.Children.Add(CardKit.At(dial, HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, 14));
        _tile.Rest.Children.Add(WallTile.BottomLeft(_rest));
        var words = WallTile.Words("", "", out _name, out _of);
        words.HorizontalAlignment = HorizontalAlignment.Left;
        words.VerticalAlignment = VerticalAlignment.Bottom;
        words.Margin = new Thickness(WallTile.Pad, 0, 52, 10);
        _tile.Lit.Children.Add(words);
        _tile.Lit.Children.Add(_buttons);

        _tile.IsVisibleChanged += (_, _) => Refresh();
        _doneTimer.Tick += (_, _) =>
        {
            _doneTimer.Stop();
            _done = null;
            Changed?.Invoke();
            Refresh();
        };
        Refresh();
    }

    /// <summary>Relevance may have changed (a finished timer's alarm state ended).</summary>
    public event Action? Changed;

    public FrameworkElement Compact => _tile;

    public bool IsRelevant => _host.Clocks.Count > 0 || _done is not null;

    /// <summary>Just finished beats everything but a ringing alarm; a running countdown beats a song.</summary>
    public int Live => _done is not null ? 90 : Active is { Paused: false, IsCountdown: true } ? 40 : Active is { Paused: false } ? 30 : Active is not null ? 10 : 0;

    /// <summary>The clock the card is about: the newest running countdown, else the newest countdown, else the stopwatch.</summary>
    RunningClock? Active => _host.Clocks.LastOrDefault(c => c.IsCountdown && !c.Paused)
                            ?? _host.Clocks.LastOrDefault(c => c.IsCountdown)
                            ?? _host.Clocks.LastOrDefault();

    /// <summary>"Tea 4:32" while something runs.</summary>
    public GlanceItem? Glance()
    {
        if (_done is { } done) return new GlanceItem(TimerCardText.Glance(done, "done"), NotchPalette.Alarm, 40);
        if (Active is not { Paused: false } c) return null;
        return new GlanceItem(TimerCardText.Glance(Name(c), c.Display(DateTime.UtcNow)), NotchPalette.Timer, 40);
    }

    /// <summary>The peek: "Timer · Tea · 4:32 · of 5:00 · ends at 10:25", with pause/resume.</summary>
    public WallPeek? Peek()
    {
        var now = DateTime.UtcNow;
        if (Active is not { } c)
            return _done is { } d ? new WallPeek("Time's up", d, "Finished just now", Fields.Alarm(), new WallAction("Dismiss", Dismiss, Stays: true)) : null;
        var line = c.IsCountdown ? $"{TimerCardText.Of(c.Total)} · {Lower(TimerCardText.Ends(c.EndsLocal(now) ?? DateTime.Now, c.Paused, c.Display(now), CardKit.H24))}" : c.Paused ? "Paused" : "Counting up";
        return new WallPeek(c.Paused ? "Timer · paused" : "Timer", $"{Name(c)} · {c.Display(now)}", line, Fields.Timer(),
            new WallAction(c.Paused ? "Resume" : "Pause", () => _host.TogglePause(c), Stays: true));
    }

    /// <summary>A timer ran out: the alarm red for a few seconds.</summary>
    public void ShowDone(string label)
    {
        _done = label.Length > 0 ? label : "Timer";
        _doneTimer.Stop();
        _doneTimer.Start();
        Refresh();
    }

    /// <summary>"Ends at 10:25 AM" → "ends at 10:25 AM" in the middle of a line.</summary>
    static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    static string Name(RunningClock c) => c.Kind switch
    {
        ClockKind.Stopwatch => "Stopwatch",
        ClockKind.Pomodoro => "Pomodoro",
        ClockKind.Focus => "Focus",
        _ => c.Label.Length > 0 ? c.Label : "Timer",
    };

    static double RingValue(RunningClock c, DateTime now) =>
        c.IsCountdown ? TimerCardText.RingLeft(c.Remaining(now), c.Total) : TimerCardText.StopwatchRing(c.Elapsed(now));

    /// <summary>Redraws the visible parts; cheap no-op while nothing is on screen.</summary>
    public void Refresh()
    {
        QuickUse.Notice(_host.Clocks, Chips);
        _detail?.Refresh();
        if (!_tile.IsVisible && _buttonsKey.Length > 0) return;
        var now = DateTime.UtcNow;
        var c = Active;
        string key;
        if (c is null)
        {
            bool done = _done is not null;
            _tile.Field = done ? Fields.Alarm() : Fields.Timer();
            _ring.Fill = done ? CardKit.Alarm : CardKit.Ink;
            _ring.Value = done ? 1 : 0;
            _digits.Text = "0:00";
            _rest.Text = done ? $"{_done} · done" : "Timer";
            _name.Text = done ? _done! : "Timer";
            _of.Text = done ? "Time’s up" : "Nothing running";
            key = done ? "done" : "none";
        }
        else
        {
            _tile.Field = Fields.Timer();
            _ring.Fill = CardKit.Ink;
            _ring.Value = RingValue(c, now);
            var display = c.Display(now);
            _digits.Text = display;
            _digits.FontSize = display.Length > 5 ? 14 : 18;
            _rest.Text = c.Paused ? $"{Name(c)} · paused" : Name(c);
            _name.Text = Name(c);
            _of.Text = c.IsCountdown ? $"{display} {TimerCardText.Of(c.Total)}" : c.Paused ? "Paused" : "Counting up";
            key = $"{c.Id}:{c.Paused}";
        }
        CardKit.Name(_tile, c is null ? (_done is { } d ? $"Timer: {d} done" : "Timer") : $"Timer: {Name(c)}, {c.Display(now)}{(c.IsCountdown ? " " + TimerCardText.Of(c.Total) : "")}{(c.Paused ? ", paused" : "")}");
        if (key == _buttonsKey) return;
        _buttonsKey = key;
        _buttons.Children.Clear();
        foreach (var b in Buttons(c, 28, vertical: true)) _buttons.Children.Add(b);
    }

    /// <summary>Pause/resume (the primary) and stop for a clock; dismiss for a finished one.</summary>
    List<Button> Buttons(RunningClock? c, double size, bool vertical)
    {
        var list = new List<Button>();
        if (c is null)
        {
            if (_done is not null)
                list.Add(CardKit.Primary("Dismiss", CardKit.Icon(CardKit.Paths.Check, CardKit.OnAccent, size * 0.45, 2.6), Dismiss, size));
            return list;
        }
        list.Add(CardKit.Primary(c.Paused ? $"Resume {Name(c)}" : $"Pause {Name(c)}", CardKit.PlayPause(!c.Paused, size * 0.4), () => _host.TogglePause(c), size));
        var stop = CardKit.Quiet($"Stop {Name(c)}", CardKit.Glyph(CardKit.Paths.Stop, CardKit.Ink, size * 0.36), () => _host.Stop(c), size);
        stop.Margin = vertical ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0, 0, 0);
        list.Add(stop);
        return list;
    }

    void Dismiss()
    {
        _doneTimer.Stop();
        _done = null;
        Changed?.Invoke();
        Refresh();
    }

    // ---- Detail ---------------------------------------------------------------------------------

    public FrameworkElement BuildDetail()
    {
        var sheet = new TimerSheet(this);
        _detail = sheet;
        sheet.Loaded += (_, _) => _detail = sheet;
        sheet.Unloaded += (_, _) =>
        {
            if (_detail == sheet) _detail = null;
        };
        return sheet;
    }

    /// <summary>The round quick-start chips' lengths, in minutes.</summary>
    static readonly int[] Chips = { 1, 5, 10, 25, 45 };

    /// <summary>
    /// The opened Timer card (the card family's shape, 600 wide, as tall as what it shows). One clock: a big palette
    /// ring with the time inside in the soft accent, its name and end time, Pause (the one accent button), Stop and
    /// +1 / +5 min; the sheet's own header folds into the middle column, as in the mockup. Two or more: slim glass rows,
    /// each with a small ring, the time, name, end time and its own Pause and Stop, and "New" in the header. Nothing
    /// running: round quick-start chips, the most-used length tinted. A finished timer shows "Time's up" and Dismiss.
    /// </summary>
    sealed class TimerSheet : CardSheet
    {
        const double SheetWidth = 600;

        readonly TimerCard _card;
        readonly FrameworkElement _header;
        readonly ContentControl _content = new() { Focusable = false };
        readonly Button _new;
        readonly List<(RunningClock Clock, ArcRing Ring, TextBlock Time, TextBlock Meta)> _live = new();
        string _structure = "";
        bool _picking;

        public TimerSheet(TimerCard card) : base("Timer", SheetWidth)
        {
            _card = card;
            // The header is the parent of the header's buttons: hidden while one clock runs (its row holds Back).
            _header = (FrameworkElement)Actions.Parent;
            _new = Action(Sheet.Cap("New", "New timer", () =>
            {
                _picking = !_picking;
                _structure = "";
                Refresh();
            }, CardKit.Paths.Plus));
            Body.Children.Add(_content);
            Refresh();
        }

        /// <summary>Rebuilt only when what's running changes; the digits, rings and end times update every tick.</summary>
        public void Refresh()
        {
            var now = DateTime.UtcNow;
            var clocks = Ordered();
            if (clocks.Count < 2) _picking = false;
            var structure = $"{clocks.Count}:{_card._done}:{_picking}|{string.Join(",", clocks.Select(c => $"{c.Id}:{c.Paused}:{c.PomodoroStep}:{c.Display(now).Length > 5}"))}";
            if (structure != _structure)
            {
                _structure = structure;
                Build(clocks, now);
            }
            foreach (var (clock, ring, time, meta) in _live)
            {
                ring.Fraction = RingValue(clock, now);
                time.Text = clock.Display(now);
                meta.Text = Line(clock, now);
            }
        }

        /// <summary>The card's clock first (the one the tile shows), then the rest, newest first.</summary>
        List<RunningClock> Ordered()
        {
            var active = _card.Active;
            var list = new List<RunningClock>();
            if (active is not null) list.Add(active);
            list.AddRange(_card._host.Clocks.Where(c => c != active).Reverse());
            return list;
        }

        /// <summary>"Ends at 1:38 PM", "Paused · 4:32 left", "Focus · round 2 of 4", "Counting up".</summary>
        static string Line(RunningClock c, DateTime now)
        {
            if (c.Kind == ClockKind.Pomodoro && !c.Paused) return $"{TimerCardText.PomodoroLine(c.PomodoroStep)} · {Lower(TimerCardText.Ends(c.EndsLocal(now) ?? DateTime.Now, false, "", CardKit.H24))}";
            if (c.IsCountdown) return TimerCardText.Ends(c.EndsLocal(now) ?? DateTime.Now, c.Paused, c.Display(now), CardKit.H24);
            return c.Paused ? "Paused" : "Counting up";
        }

        void Build(List<RunningClock> clocks, DateTime now)
        {
            _live.Clear();
            bool single = clocks.Count == 1;
            _header.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
            BodyGap = single ? 0 : 14;
            _new.Visibility = clocks.Count >= 2 ? Visibility.Visible : Visibility.Collapsed;
            _new.ToolTip = _picking ? "Hide the quick starts" : "New timer";
            if (clocks.Count == 0)
            {
                bool done = _card._done is not null;
                TitleText.Text = "Timer";
                Status = done ? "time’s up" : "or say “timer 12 min for tea”";
                _content.Content = done ? Sheet.Column(14, Done(), QuickChips()) : QuickChips();
                return;
            }
            if (single)
            {
                _content.Content = One(clocks[0], now);
                return;
            }
            TitleText.Text = "Timers";
            Status = $"{clocks.Count} running";
            var rows = new StackPanel();
            for (int i = 0; i < clocks.Count; i++)
            {
                var row = Slim(clocks[i], i, now);
                if (i > 0) row.Margin = new Thickness(0, 8, 0, 0);
                rows.Children.Add(row);
            }
            // Four rows show; more scroll.
            var list = new ScrollViewer
            {
                Content = rows,
                MaxHeight = 4 * 68 + 3 * 8,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            _content.Content = _picking ? Sheet.Column(14, list, QuickChips()) : list;
        }

        // ---- One clock -----------------------------------------------------------------------------

        FrameworkElement One(RunningClock c, DateTime now)
        {
            var display = c.Display(now);
            var ring = new ArcRing(132, RingValue(c, now)) { Thickness = 8 };
            var time = Sheet.Num(display, display.Length > 5 ? 26 : 34, c.Paused ? CardKit.Ink2 : Ink.Soft);
            time.HorizontalAlignment = HorizontalAlignment.Center;
            var of = Sheet.Meta(c.IsCountdown ? TimerCardText.Of(c.Total) : "elapsed", 11);
            of.HorizontalAlignment = HorizontalAlignment.Center;
            of.Margin = new Thickness(0, 4, 0, 0);
            var inside = Sheet.Column(0, time, of);
            inside.HorizontalAlignment = HorizontalAlignment.Center;
            inside.VerticalAlignment = VerticalAlignment.Center;
            var dial = new Grid { Width = 132, Height = 132, VerticalAlignment = VerticalAlignment.Center };
            dial.Children.Add(ring);
            dial.Children.Add(inside);

            // The header, folded into the middle column: Back and the eyes, then the clock's name and its end.
            var top = Sheet.Row(0, Sheet.Back(() => BackRequested?.Invoke()), Eyes());
            var name = Sheet.Word(Name(c), 24);
            name.Margin = new Thickness(0, 10, 0, 0);
            var meta = Sheet.Meta(Line(c, now));
            meta.Margin = new Thickness(0, 6, 0, 0);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 16, 0) };
            words.Children.Add(top);
            words.Children.Add(name);
            words.Children.Add(meta);
            if (c.Kind == ClockKind.Pomodoro) words.Children.Add(Dots(c.PomodoroStep));

            var stop = Sheet.Round($"Stop {Name(c)}", CardKit.Paths.Stop, () => _card._host.Stop(c), 40, iconSize: 14, filled: true);
            var pause = Sheet.Round(c.Paused ? $"Resume {Name(c)}" : $"Pause {Name(c)}", c.Paused ? CardKit.Paths.Play : CardKit.Paths.Pause,
                () => _card._host.TogglePause(c), 54, accent: true, iconSize: 20, filled: true);
            pause.Margin = new Thickness(8, 0, 0, 0);
            var main = Sheet.Row(0, stop, pause);
            main.HorizontalAlignment = HorizontalAlignment.Right;
            var controls = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            controls.Children.Add(main);
            if (c.IsCountdown)
            {
                var extend = Sheet.Row(6,
                    Sheet.Cap("+1 min", $"Add 1 minute to {Name(c)}", () => _card._host.Extend(c, TimeSpan.FromMinutes(1))),
                    Sheet.Cap("+5 min", $"Add 5 minutes to {Name(c)}", () => _card._host.Extend(c, TimeSpan.FromMinutes(5))));
                extend.HorizontalAlignment = HorizontalAlignment.Right;
                extend.Margin = new Thickness(0, 10, 0, 0);
                controls.Children.Add(extend);
            }

            var grid = new Grid { Margin = new Thickness(4, 2, 4, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(dial);
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            Grid.SetColumn(controls, 2);
            grid.Children.Add(controls);
            _live.Add((c, ring, time, meta));
            CardKit.Name(dial, $"{Name(c)}: {display} {(c.IsCountdown ? TimerCardText.Of(c.Total) : "elapsed")}");
            return grid;
        }

        /// <summary>The pet's eyes, as in every card's header.</summary>
        static FrameworkElement Eyes()
        {
            var eyes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            eyes.Children.Add(new Border { Width = 6, Height = 10, CornerRadius = new CornerRadius(3), Background = CardKit.Ink });
            eyes.Children.Add(new Border { Width = 6, Height = 10, CornerRadius = new CornerRadius(3), Background = CardKit.Ink, Margin = new Thickness(4, 0, 0, 0) });
            return eyes;
        }

        /// <summary>The four focus rounds of a pomodoro: done ones in the palette, the current one outlined.</summary>
        static FrameworkElement Dots(int step)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var dots = TimerCardText.PomodoroDots(step);
            for (int i = 0; i < dots.Length; i++)
            {
                row.Children.Add(new Border
                {
                    Width = 9,
                    Height = 9,
                    CornerRadius = new CornerRadius(4.5),
                    Margin = new Thickness(i > 0 ? 6 : 0, 0, 0, 0),
                    Background = dots[i] == RoundDot.Done ? Ink.A : Brushes.Transparent,
                    BorderBrush = dots[i] switch { RoundDot.Done => Ink.A, RoundDot.Current => Ink.Soft, _ => Sheet.Track },
                    BorderThickness = new Thickness(1.5),
                });
            }
            CardKit.Name(row, TimerCardText.PomodoroLine(step));
            return row;
        }

        // ---- Two or more ---------------------------------------------------------------------------

        FrameworkElement Slim(RunningClock c, int index, DateTime now)
        {
            bool first = index == 0;
            Brush stroke = (index % 3) switch { 0 => Ink.A, 1 => Ink.C, _ => Ink.B };
            var ring = new ArcRing(44, RingValue(c, now)) { Thickness = 5, Stroke = stroke, VerticalAlignment = VerticalAlignment.Center };
            var time = Sheet.Num(c.Display(now), first ? 40 : 32, c.Paused ? CardKit.Ink2 : first ? Ink.Soft : CardKit.Ink);
            time.Margin = new Thickness(16, 0, 0, 0);
            time.MinWidth = first ? 104 : 84;
            var meta = Sheet.Meta(Line(c, now), 12);
            meta.Margin = new Thickness(0, 2, 0, 0);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
            words.Children.Add(Sheet.Word(Name(c), 14));
            words.Children.Add(meta);

            var stop = Sheet.Round($"Stop {Name(c)}", CardKit.Paths.Stop, () => _card._host.Stop(c), 34, iconSize: 12, filled: true);
            double size = first ? 44 : 40;
            var pause = Sheet.Round(c.Paused ? $"Resume {Name(c)}" : $"Pause {Name(c)}", c.Paused ? CardKit.Paths.Play : CardKit.Paths.Pause,
                () => _card._host.TogglePause(c), size, accent: first, iconSize: first ? 16 : 14, filled: true);
            pause.Margin = new Thickness(8, 0, 0, 0);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(ring);
            Grid.SetColumn(time, 1);
            grid.Children.Add(time);
            Grid.SetColumn(words, 2);
            grid.Children.Add(words);
            var buttons = Sheet.Row(0, stop, pause);
            buttons.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(buttons, 3);
            grid.Children.Add(buttons);
            _live.Add((c, ring, time, meta));
            return Sheet.Tile(grid, new Thickness(16, first ? 12 : 10, 14, first ? 12 : 10));
        }

        // ---- Nothing running -------------------------------------------------------------------------

        FrameworkElement Done()
        {
            var ring = new ArcRing(56, 1) { Thickness = 5, VerticalAlignment = VerticalAlignment.Center };
            var check = CardKit.Icon(CardKit.Paths.Check, Ink.Soft, 20, 2.6);
            var dial = new Grid { Width = 56, Height = 56 };
            dial.Children.Add(ring);
            dial.Children.Add(check);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
            words.Children.Add(Sheet.Word(_card._done ?? "Timer", 18));
            var line = Sheet.Meta("Finished just now", 12);
            line.Margin = new Thickness(0, 3, 0, 0);
            words.Children.Add(line);
            var dismiss = Sheet.Cap("Dismiss", "Dismiss", _card.Dismiss, CardKit.Paths.Check, accent: true);
            var row = Sheet.Spread(Sheet.Row(0, dial, words), dismiss);
            row.VerticalAlignment = VerticalAlignment.Center;
            return Sheet.Tile(row, new Thickness(12, 10, 12, 10));
        }

        /// <summary>The round quick starts: 1, 5, 10, 25 and 45 minutes, the one used most in the soft accent.</summary>
        FrameworkElement QuickChips()
        {
            int favourite = QuickUse.Favourite(Chips);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 0) };
            foreach (var m in Chips)
            {
                var chip = Chip(m, m == favourite);
                if (row.Children.Count > 0) chip.Margin = new Thickness(10, 0, 0, 0);
                row.Children.Add(chip);
            }
            return row;
        }

        Pressable Chip(int minutes, bool favourite)
        {
            var number = Sheet.Num(minutes.ToString(System.Globalization.CultureInfo.InvariantCulture), 22, favourite ? Ink.Soft : CardKit.Ink);
            number.HorizontalAlignment = HorizontalAlignment.Center;
            var unit = CardKit.Text("MIN", 9, FontWeights.Bold, secondary: !favourite);
            if (favourite) unit.Foreground = Ink.Soft;
            unit.HorizontalAlignment = HorizontalAlignment.Center;
            unit.Margin = new Thickness(0, 2, 0, 0);
            var face = Sheet.Column(0, number, unit);
            face.HorizontalAlignment = HorizontalAlignment.Center;
            face.VerticalAlignment = VerticalAlignment.Center;
            var chip = new Pressable(favourite ? Ink.Tint : Sheet.GlassFill, favourite ? Ink.Tint : Sheet.CapFill, favourite ? Ink.TintEdge : Sheet.GlassEdge)
            {
                Width = 58,
                Height = 58,
                CornerRadius = new CornerRadius(29),
                Child = face,
                ToolTip = $"Start a {minutes} minute timer",
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, $"Start a {minutes} minute timer");
            chip.Click += () =>
            {
                _picking = false;
                _card._host.QuickStart(TimeSpan.FromMinutes(minutes));
            };
            return chip;
        }
    }

    /// <summary>
    /// How often each quick-start length was used (a chip, or a timer of that length started any other way), so the
    /// chip used most can wear the accent. Kept in a small file next to the settings; 25 (a focus block) until then.
    /// </summary>
    static class QuickUse
    {
        static readonly string Store = System.IO.Path.Combine(Infrastructure.Paths.Root, "timer-lengths.json");
        static readonly HashSet<int> Seen = new();
        static Dictionary<int, int>? _counts;

        static Dictionary<int, int> Counts
        {
            get
            {
                if (_counts is not null) return _counts;
                try
                {
                    _counts = System.IO.File.Exists(Store)
                        ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, int>>(System.IO.File.ReadAllText(Store)) ?? new()
                        : new();
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                {
                    _counts = new();
                }
                return _counts;
            }
        }

        public static int Favourite(int[] lengths)
        {
            var best = lengths.Select(m => (Minutes: m, Uses: Counts.GetValueOrDefault(m))).OrderByDescending(x => x.Uses).First();
            return best.Uses > 0 ? best.Minutes : 25;
        }

        static void Count(int minutes)
        {
            Counts[minutes] = Counts.GetValueOrDefault(minutes) + 1;
            try
            {
                System.IO.File.WriteAllText(Store, System.Text.Json.JsonSerializer.Serialize(Counts));
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                // Only a hint for which chip is tinted; nothing to do.
            }
        }

        /// <summary>Counts each new plain timer of a chip's length once (from a chip, by voice or typed).</summary>
        public static void Notice(IEnumerable<RunningClock> clocks, int[] lengths)
        {
            foreach (var c in clocks)
            {
                if (!Seen.Add(c.Id) || c.Kind != ClockKind.Timer) continue;
                int minutes = (int)Math.Round(c.Total.TotalMinutes);
                if (Math.Abs(c.Total.TotalMinutes - minutes) < 0.01 && lengths.Contains(minutes)) Count(minutes);
            }
        }
    }

    // ---- Test pipe --------------------------------------------------------------------------------

    public string DescribeCompact()
    {
        var now = DateTime.UtcNow;
        if (Active is not { } c)
            return _done is { } d ? $"timer: done label={d} ring=1.00 colour=alarm buttons=dismiss" : "timer: nothing running (card not relevant)";
        return $"timer: label={Name(c)} digits={c.Display(now)} ring={RingValue(c, now):0.00} paused={c.Paused} buttons={(c.Paused ? "resume" : "pause")},stop " +
               $"running={_host.Clocks.Count} focused={_tile.IsLit} visible={_tile.IsVisible}";
    }

    public string DescribeDetail()
    {
        var now = DateTime.UtcNow;
        var c = Active;
        string Row(RunningClock k) => k.Kind == ClockKind.Pomodoro
            ? $"{Name(k)} {k.Display(now)} {TimerCardText.PomodoroLine(k.PomodoroStep)} dots={string.Join("", TimerCardText.PomodoroDots(k.PomodoroStep).Select(d => d switch { RoundDot.Done => "x", RoundDot.Current => "o", _ => "." }))}"
            : $"{Name(k)} {k.Display(now)}{(k.Paused ? " paused" : "")}";
        var active = c is null ? (_done is { } d ? $"done({d})" : "none")
            : $"{Name(c)} {c.Display(now)} {(c.IsCountdown ? TimerCardText.Of(c.Total) : "elapsed")} {(c.IsCountdown ? TimerCardText.Ends(c.EndsLocal(now) ?? DateTime.Now, c.Paused, c.Display(now)) : "")}".TrimEnd();
        var others = _host.Clocks.Where(k => k != c).Reverse().Take(4).Select(Row).ToList();
        return $"timer detail: active=[{active}] alsoRunning=[{(others.Count == 0 ? "-" : string.Join(" | ", others))}] " +
               $"quickAdd={string.Join(",", TimerCardText.QuickMinutes.Select(m => m + "m"))} extend=+1,+5,+10";
    }
}
