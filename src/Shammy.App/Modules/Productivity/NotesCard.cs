using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Connect;
using Shammy.Core.Productivity;
using Shammy.Infrastructure;
using Shammy.Views;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.Productivity;

/// <summary>One line on the Notes card: a note or a reminder, open or ticked off.</summary>
internal sealed record NoteLine(string Id, string Text, bool Done, DateTime? Due, bool Fired)
{
    public bool IsReminder => Due is not null;
}

/// <summary>
/// The Notes card on the Focus Wall: a tile whose big number is how many things are left; focused, the first one
/// (with its reminder time) and a tick to mark it done (the primary control). The detail is a card sheet
/// (<see cref="Detail"/>): an add box, today's calendar events read-only on top, then every note and reminder as a
/// row with a round check and a due chip. While the screen is shared, only counts show.
/// </summary>
internal sealed class NotesCard
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    readonly Func<ProductivityStore> _store;
    readonly Action<string> _toggle, _delete, _add;
    readonly WallTile _tile = new();
    readonly TrackedText _count = CardKit.Poster("", 40), _countLabel = CardKit.Label("To do"), _rest = CardKit.Label("Notes");
    readonly TextBlock _first, _second;
    readonly ContentControl _tick = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0) };

    public NotesCard(Func<ProductivityStore> store, Action<string> toggle, Action<string> delete, Action<string> add)
    {
        _store = store;
        _toggle = toggle;
        _delete = delete;
        _add = add;
        _tile.Field = Fields.List();
        _countLabel.Margin = new Thickness(0, 6, 0, 0);
        var top = CardKit.Column(0, _count, _countLabel);
        top.Margin = new Thickness(WallTile.Pad);
        top.VerticalAlignment = VerticalAlignment.Top;
        top.HorizontalAlignment = HorizontalAlignment.Left;
        _tile.Art.Children.Add(top);
        _tile.Rest.Children.Add(WallTile.BottomLeft(_rest));
        var words = WallTile.Words("", "", out _first, out _second);
        _tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        _tile.Lit.Children.Add(_tick);
        // Times read "Today"/"Tomorrow": rebuild when it comes into view rather than ticking.
        _tile.IsVisibleChanged += (_, _) =>
        {
            if (_tile.IsVisible) Refresh();
        };
        // A screen share starting or ending hides or reveals the items in place.
        PrivacyState.Changed += _ => _tile.Dispatcher.BeginInvoke(Refresh);
        Refresh();
    }

    static bool Private => PrivacyState.Presenting;

    /// <summary>The calendar's occurrences, for today's upcoming events in the detail (read-only rows).</summary>
    public Func<IReadOnlyList<Shammy.Core.Calendar.CalendarOccurrence>>? Events { get; init; }

    /// <summary>Joins an event's call (only events with a recognised meeting link get a Join button).</summary>
    public Action<Shammy.Core.Calendar.CalendarOccurrence>? Join { get; init; }

    List<Shammy.Core.Calendar.CalendarOccurrence> Today(DateTime now, int max) =>
        Events is null ? new() : DayLinks.UpcomingToday(Events(), now, max);

    public FrameworkElement Compact => _tile;

    /// <summary>A reminder that has gone off and isn't done wants attention.</summary>
    public int Live => _store().Reminders.Any(r => r.Fired && !r.Done) ? 70 : 0;

    /// <summary>Open items first (reminders by time, then notes newest first), then ticked-off ones, newest first.</summary>
    internal static List<NoteLine> Lines(ProductivityStore s)
    {
        var open = s.Reminders.Where(r => !r.Done).OrderBy(r => r.Fired).ThenBy(r => r.Due)
            .Select(r => new NoteLine(r.Id, r.Title, false, r.Due, r.Fired))
            .Concat(s.Notes.Where(n => !n.Done).OrderByDescending(n => n.Created).Select(n => new NoteLine(n.Id, n.Text, false, null, false)));
        var done = s.Reminders.Where(r => r.Done).Select(r => (r.DoneAt!.Value, new NoteLine(r.Id, r.Title, true, r.Due, r.Fired)))
            .Concat(s.Notes.Where(n => n.Done).Select(n => (n.DoneAt!.Value, new NoteLine(n.Id, n.Text, true, null, false))))
            .OrderByDescending(x => x.Item1).Select(x => x.Item2);
        return open.Concat(done).ToList();
    }

    /// <summary>Today's progress: everything still open plus what was ticked off today.</summary>
    internal static (int Done, int Total) Progress(ProductivityStore s, DateTime now)
    {
        int open = s.Reminders.Count(r => !r.Done) + s.Notes.Count(n => !n.Done);
        int doneToday = s.Reminders.Count(r => r.DoneAt?.Date == now.Date) + s.Notes.Count(n => n.DoneAt?.Date == now.Date);
        return (doneToday, open + doneToday);
    }

    static string When(DateTime due, DateTime now) =>
        due.Date == now.Date ? Shammy.Core.Cards.ClockFormat.Time(due, CardKit.H24) : TimeFormat.When(due, now, CardKit.H24);

    /// <summary>The peek: "Notes · 3 to do · Send the invoice · 6:40 PM", with Done.</summary>
    public WallPeek? Peek()
    {
        var open = Lines(_store()).Where(l => !l.Done).ToList();
        if (open.Count == 0) return null;
        var now = DateTime.Now;
        if (Private) return new WallPeek("Notes", PrivacyText.NotesSummary(_store().Notes.Count(n => !n.Done), _store().Reminders.Count(r => !r.Done)), PrivacyText.Hidden, Fields.List(), null);
        var first = open[0];
        var line = first.Due is { } d ? When(d, now) : open.Count > 1 ? "Then: " + open[1].Text : "The last one";
        return new WallPeek($"Notes · {open.Count} to do", first.Text, line, Fields.List(), new WallAction("Done", () => _toggle(first.Id), Stays: true));
    }

    // ---- Tile ------------------------------------------------------------------------------------

    public void Refresh()
    {
        var store = _store();
        var now = DateTime.Now;
        var open = Lines(store).Where(l => !l.Done).ToList();
        _count.Text = open.Count.ToString(Inv);
        _countLabel.Text = open.Count == 0 ? "All done" : "To do";
        _tick.Content = null;
        if (Private)
        {
            _first.Text = PrivacyText.NotesSummary(store.Notes.Count(n => !n.Done), store.Reminders.Count(r => !r.Done));
            _second.Text = PrivacyText.Hidden;
            CardKit.Name(_tile, $"Notes, {open.Count} open items, hidden while sharing. Open notes");
            return;
        }
        if (open.Count == 0)
        {
            _first.Text = "Nothing left to do";
            _second.Text = "Try “note: buy milk”";
        }
        else
        {
            var first = open[0];
            _first.Text = first.Text;
            _second.Text = first.Due is { } due ? When(due, now) : open.Count > 1 ? open[1].Text : "The last one";
            _tick.Content = CardKit.Primary($"Mark “{first.Text}” done", CardKit.Icon(CardKit.Paths.Check, CardKit.OnAccent, 12, 2.8), () => _toggle(first.Id), 28);
        }
        CardKit.Name(_tile, $"Notes, {open.Count} open items" + (open.Count > 0 ? ": " + string.Join("; ", open.Take(2).Select(l => l.Text)) : "") + ". Open notes");
    }

    // ---- Detail ----------------------------------------------------------------------------------

    public FrameworkElement BuildDetail() => new Detail(this);

    /// <summary>
    /// The opened Notes card (the card family's shape, 600 wide, as tall as its list): the header shows the day's
    /// progress as a small palette ring and "3 of 12 done today"; under it a full-width add box (Enter saves), then
    /// today's calendar events (read-only, Join where there's a call link) and every note and reminder as a row: a
    /// round check (the accent when done), the text in bold and a due chip (the accent tint when due today or
    /// overdue). Click a row to tick it off, × on hover to delete, right-click to copy. About four and a half rows
    /// show; more scroll. While the screen is shared, only the counts show.
    /// </summary>
    sealed class Detail : CardSheet
    {
        const double SheetWidth = 600, RowHeight = 41;

        readonly NotesCard _card;
        readonly StackPanel _rows = new();
        readonly ContentControl _list = new() { Focusable = false };
        readonly ArcRing _ring = new(28, 0) { Thickness = 4 };
        readonly TextBlock _progress = Sheet.Meta("");
        readonly TextBox _input = new();

        public Detail(NotesCard card) : base("Notes", SheetWidth)
        {
            _card = card;
            BodyGap = 12;
            Action(_ring);
            Action(_progress);

            // The add box: Enter saves.
            System.Windows.Automation.AutomationProperties.SetName(_input, "Add a note");
            _input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                var text = _input.Text.Trim();
                if (text.Length == 0) return;
                _card._add(text);
                _input.Clear();
                e.Handled = true;
                Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
            };
            var field = Sheet.Field(_input, "Add a note, or say “note buy oat milk”", CardKit.Paths.Plus);
            field.MouseLeftButtonUp += (_, _) => _input.Focus();

            var body = new StackPanel();
            body.Children.Add(field);
            _list.Margin = new Thickness(0, 8, 0, 0);
            body.Children.Add(_list);
            Body.Children.Add(body);

            Action<bool> privacy = _ => Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
            Loaded += (_, _) => PrivacyState.Changed += privacy;
            Unloaded += (_, _) => PrivacyState.Changed -= privacy;
            Refresh();
        }

        public void Refresh()
        {
            var store = _card._store();
            var now = DateTime.Now;
            var lines = Lines(store);
            var (doneToday, total) = Progress(store, now);

            // "3 of 12 done today", the done count in light numbers, beside a ring of the same.
            _ring.Visibility = _progress.Visibility = total == 0 ? Visibility.Collapsed : Visibility.Visible;
            _ring.Fraction = total == 0 ? 0 : (double)doneToday / total;
            _progress.Inlines.Clear();
            _progress.Inlines.Add(new System.Windows.Documents.Run(doneToday.ToString(Inv))
            {
                FontFamily = Ui.Tokens.FontRes("ShammyDisplayFont"),
                FontWeight = FontWeights.Light,
                FontSize = 18,
                Foreground = Ink.Soft,
            });
            _progress.Inlines.Add(new System.Windows.Documents.Run($" of {total} done today"));

            _rows.Children.Clear();
            if (Private)
            {
                _list.Content = Line(PrivacyText.Hidden);
                return;
            }
            // Today's calendar first (read-only, Join where there's a call link), then notes and reminders.
            foreach (var e in _card.Today(now, 3)) _rows.Children.Add(EventRow(e, now, _rows.Children.Count == 0));
            foreach (var line in lines) _rows.Children.Add(Item(line, now, _rows.Children.Count == 0));
            if (_rows.Children.Count == 0)
            {
                _list.Content = Line("Nothing to do yet. Notes and reminders you add show up here.");
                return;
            }
            _list.Content = new ScrollViewer
            {
                Content = _rows,
                MaxHeight = RowHeight * 4.5,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
        }

        static TextBlock Line(string text)
        {
            var t = Sheet.Meta(text, 13);
            t.Margin = new Thickness(4, 4, 0, 0);
            return t;
        }

        /// <summary>A list row: a hairline above every row but the first.</summary>
        static Border Frame(UIElement child, bool first) => new()
        {
            Child = child,
            Padding = new Thickness(4, 8, 4, 8),
            MinHeight = RowHeight,
            Background = Brushes.Transparent,
            BorderBrush = Sheet.RowLine,
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
        };

        static Grid Columns(UIElement lead, UIElement text, UIElement right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(lead);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
            return grid;
        }

        FrameworkElement EventRow(Shammy.Core.Calendar.CalendarOccurrence e, DateTime now, bool first)
        {
            var bell = new Border { Width = 22, Height = 22, Child = CardKit.Icon(CardKit.Paths.Bell, CardKit.Ink2, 15, 2), VerticalAlignment = VerticalAlignment.Center };
            var title = Sheet.Word(e.Title, 14);
            title.Margin = new Thickness(12, 0, 12, 0);
            var right = Sheet.Row(0, Sheet.Chip(DayLinks.EventMeta(e, now), quiet: true));
            if (e.HasLink && _card.Join is { } join)
            {
                var b = Sheet.Cap("Join", $"Join {e.Title}", () => join(e), height: 26);
                b.Margin = new Thickness(8, 0, 0, 0);
                right.Children.Add(b);
            }
            right.VerticalAlignment = VerticalAlignment.Center;
            var row = Frame(Columns(bell, title, right), first);
            row.ToolTip = $"{e.Title} · {Shammy.Core.Calendar.CalendarText.Range(e.Start, e.End)}";
            CardKit.Name(row, $"{e.Title}, {DayLinks.EventMeta(e, now)}, from your calendar");
            return row;
        }

        FrameworkElement Item(NoteLine line, DateTime now, bool first)
        {
            var text = Sheet.Word(line.Text, 14);
            text.Margin = new Thickness(12, 0, 12, 0);
            if (line.Done) text.TextDecorations = TextDecorations.Strikethrough;
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (line.Due is { } due)
            {
                // Due today, overdue or gone off: the accent tint; later: quiet glass.
                bool soon = !line.Done && (line.Fired || due.Date <= now.Date);
                right.Children.Add(Sheet.Chip(TimeFormat.When(due, now, CardKit.H24), quiet: !soon));
            }
            var remove = Sheet.Round("Delete", CardKit.Paths.Close, () => Delete(line.Id), 24, iconSize: 10);
            remove.Visibility = Visibility.Hidden;
            remove.Margin = new Thickness(8, 0, 0, 0);
            right.Children.Add(remove);
            var row = Frame(Columns(Check(line), text, right), first);
            if (line.Done) row.Opacity = 0.5;
            row.Cursor = Cursors.Hand;
            row.ToolTip = line.Text.Length > 50 ? line.Text : null;
            row.MouseEnter += (_, _) => remove.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => remove.Visibility = Visibility.Hidden;
            row.MouseLeftButtonUp += (_, e) =>
            {
                if (e.Handled) return;
                Toggle(line.Id);
            };
            var copy = new MenuItem { Header = "Copy" };
            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(line.Text);
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    // Clipboard busy; nothing to do.
                }
            };
            var del = new MenuItem { Header = "Delete" };
            del.Click += (_, _) => Delete(line.Id);
            row.ContextMenu = new ContextMenu { Items = { copy, del } };
            CardKit.Name(row, line.Text + (line.Due is { } d ? $", reminder {When(d, now)}" : "") + (line.Done ? ", done" : ", not done"));
            return row;
        }

        /// <summary>The round check: an empty ring, or filled with the accent and a tick when done.</summary>
        Pressable Check(NoteLine line)
        {
            var check = line.Done
                ? new Pressable(CardKit.Accent, CardKit.AccentHover, CardKit.Accent) { Child = CardKit.Icon(CardKit.Paths.Check, CardKit.OnAccent, 12, 3.4) }
                : new Pressable(Brushes.Transparent, Sheet.CapFill, Ui.Tokens.Brush("#73EBEBF5"));
            check.Width = check.Height = 22;
            check.CornerRadius = new CornerRadius(11);
            check.BorderThickness = new Thickness(2);
            check.VerticalAlignment = VerticalAlignment.Center;
            check.ToolTip = line.Done ? "Mark not done" : "Mark done";
            System.Windows.Automation.AutomationProperties.SetName(check, line.Done ? $"Mark “{line.Text}” not done" : $"Mark “{line.Text}” done");
            check.Click += () => Toggle(line.Id);
            return check;
        }

        void Toggle(string id)
        {
            _card._toggle(id);
            Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        }

        void Delete(string id)
        {
            _card._delete(id);
            Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        }
    }
}
