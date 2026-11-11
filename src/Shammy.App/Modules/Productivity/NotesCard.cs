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

namespace Shammy.Modules.Productivity;

/// <summary>One line on the Notes card: a note or a reminder, open or ticked off.</summary>
internal sealed record NoteLine(string Id, string Text, bool Done, DateTime? Due, bool Fired)
{
    public bool IsReminder => Due is not null;
}

/// <summary>
/// The Notes card on the Focus Wall: a tile whose big number is how many things are left; focused, the first one
/// (with its reminder time) and a tick to mark it done (the primary control). The detail is Wall's list: every
/// note and reminder as a hairline row — click to tick off, × to delete, right-click to copy — today's calendar
/// events read-only on top, and a box in the corner to add a note. While the screen is shared, only counts show.
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

    public FrameworkElement BuildDetail() => new Detail(this).Root;

    sealed class Detail
    {
        readonly NotesCard _card;
        readonly StackPanel _rows = new();
        readonly TrackedText _count = CardKit.Poster("", 96), _countLabel = CardKit.Label("To do");
        readonly TextBox _input;

        public Detail(NotesCard card)
        {
            _card = card;
            Root = new WallDetail(Fields.List(), "", "Notes");
            _countLabel.Margin = new Thickness(0, 8, 0, 0);
            Root.ArtBottomLeft(_count, _countLabel);
            Root.Body.Children.Add(WallDetail.Scroll(_rows));

            // The add box in the corner: Enter saves.
            _input = new TextBox { Style = Ui.Tokens.StyleRes("BareTextBox"), FontSize = 13, FontFamily = CardKit.Face(FontWeights.Medium), Foreground = CardKit.Ink, CaretBrush = CardKit.Accent };
            var hint = CardKit.Text("Add a note", 13, FontWeights.Medium, secondary: true);
            hint.IsHitTestVisible = false;
            _input.TextChanged += (_, _) => hint.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                var text = _input.Text.Trim();
                if (text.Length == 0) return;
                _card._add(text);
                _input.Clear();
                e.Handled = true;
                Root.Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
            };
            var plus = CardKit.Icon(CardKit.Paths.Plus, CardKit.Ink2, 14, 2.2);
            plus.Margin = new Thickness(0, 0, 8, 0);
            var row = new DockPanel();
            DockPanel.SetDock(plus, Dock.Left);
            row.Children.Add(plus);
            row.Children.Add(new Grid { Children = { hint, _input } });
            var box = new Border
            {
                Width = 220,
                Height = 32,
                CornerRadius = new CornerRadius(10),
                Background = CardKit.PillBg,
                BorderBrush = CardKit.Hairline,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 0, 11, 0),
                Child = row,
                Cursor = Cursors.IBeam,
            };
            box.MouseLeftButtonUp += (_, _) => _input.Focus();
            Root.Corner = box;

            Action<bool> privacy = _ => Root.Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
            Root.Loaded += (_, _) => PrivacyState.Changed += privacy;
            Root.Unloaded += (_, _) => PrivacyState.Changed -= privacy;
            Refresh();
        }

        public WallDetail Root { get; }

        public void Refresh()
        {
            var store = _card._store();
            var now = DateTime.Now;
            var lines = Lines(store);
            int open = lines.Count(l => !l.Done);
            var (doneToday, total) = Progress(store, now);
            Root.Kicker.Text = $"Notes · {open} to do";
            Root.Line.Text = total == 0 ? "Hold Right Alt and say “remind me…”, or add one here" : $"{doneToday} of {total} done today · click one to tick it off";
            _count.Text = open.ToString(Inv);
            _countLabel.Text = open == 0 ? "All done" : "To do";
            _rows.Children.Clear();
            if (Private)
            {
                _rows.Children.Add(Empty(PrivacyText.Hidden));
                return;
            }
            // Today's calendar first (read-only, Join where there's a call link), then notes and reminders.
            foreach (var e in _card.Today(now, 3)) _rows.Children.Add(EventRow(e, now));
            foreach (var line in lines) _rows.Children.Add(Item(line, now));
            if (_rows.Children.Count == 0) _rows.Children.Add(Empty("No notes or reminders yet"));
        }

        static TextBlock Empty(string text)
        {
            var t = CardKit.Text(text, 13, FontWeights.Medium, secondary: true);
            t.Margin = new Thickness(0, 8, 0, 0);
            return t;
        }

        FrameworkElement EventRow(Shammy.Core.Calendar.CalendarOccurrence e, DateTime now)
        {
            var meta = CardKit.Text(DayLinks.EventMeta(e, now), 12, FontWeights.Medium, secondary: true);
            UIElement right = meta;
            if (e.HasLink && _card.Join is { } join)
            {
                var b = CardKit.Pill("Join", $"Join {e.Title}", () => join(e), 24, 11);
                b.Margin = new Thickness(10, 0, 0, 0);
                right = CardKit.Row(0, meta, b);
            }
            var row = WallDetail.ListRow(CardKit.Icon(CardKit.Paths.Bell, CardKit.Ink2, 14), CardKit.Text(e.Title, 13.5, FontWeights.SemiBold), right);
            row.ToolTip = $"{e.Title} · {Shammy.Core.Calendar.CalendarText.Range(e.Start, e.End)}";
            CardKit.Name(row, $"{e.Title}, {DayLinks.EventMeta(e, now)}, from your calendar");
            return row;
        }

        FrameworkElement Item(NoteLine line, DateTime now)
        {
            var text = CardKit.Text(line.Text, 13.5, FontWeights.SemiBold);
            if (line.Done)
            {
                text.Foreground = CardKit.Ink2;
                text.TextDecorations = TextDecorations.Strikethrough;
            }
            var meta = CardKit.Text(line.Due is { } due ? When(due, now) : "", 12, FontWeights.Medium, secondary: true);
            var remove = CardKit.Quiet("Delete", CardKit.Icon(CardKit.Paths.Close, CardKit.Ink, 10, 2.4), () => Delete(line.Id), 22);
            remove.Visibility = Visibility.Hidden;
            remove.Margin = new Thickness(10, 0, 0, 0);
            var row = WallDetail.ListRow(CheckBox(line.Done), text, CardKit.Row(0, meta, remove));
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

        /// <summary>A round-cornered box: a white tick when done (white, not the accent: the accent is for focus).</summary>
        static Border CheckBox(bool done) => new()
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(5),
            BorderBrush = done ? CardKit.Ink : CardKit.Ink2,
            BorderThickness = new Thickness(1.6),
            Background = done ? CardKit.Ink : Brushes.Transparent,
            Child = done ? CardKit.Icon(CardKit.Paths.Check, CardKit.Ground, 11, 3) : null,
        };

        void Toggle(string id)
        {
            _card._toggle(id);
            Root.Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        }

        void Delete(string id)
        {
            _card._delete(id);
            Root.Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
        }
    }
}
