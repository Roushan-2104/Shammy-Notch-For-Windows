using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Clipboard;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>
/// The clipboard card on the Focus Wall: a tile whose big number is how many items are kept; focused, the latest
/// one and Copy. The detail is Wall's list: every item as a hairline row (a thumbnail, swatch or kind mark, the
/// text, where it came from and when) — click to copy, with pin, open, save-as-note and remove on hover — a search
/// field in the corner, and Pinned / All / Clear (with Undo) under the title. Shared screens see counts only.
/// </summary>
internal sealed class ClipboardCard
{
    readonly ClipHistory _history;
    readonly Action<ClipEntry> _copy;
    readonly Action<string> _open;
    readonly Func<Action?> _clear;
    readonly WallTile _tile = new();
    readonly TrackedText _count = CardKit.Poster("", 40), _countLabel = CardKit.Label("Items"), _rest = CardKit.Label("Clipboard");
    readonly TextBlock _latest, _latestMeta;
    readonly ContentControl _copyLatest = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 10, 0) };

    /// <param name="clear">Clears the unpinned history and returns how to undo it (null when nothing was cleared).</param>
    public ClipboardCard(ClipHistory history, Action<ClipEntry> copy, Action<string> open, Func<Action?> clear)
    {
        _history = history;
        _copy = copy;
        _open = open;
        _clear = clear;
        _tile.Field = Fields.List();
        _countLabel.Margin = new Thickness(0, 6, 0, 0);
        var top = CardKit.Column(0, _count, _countLabel);
        top.Margin = new Thickness(WallTile.Pad);
        top.VerticalAlignment = VerticalAlignment.Top;
        top.HorizontalAlignment = HorizontalAlignment.Left;
        _tile.Art.Children.Add(top);
        _tile.Rest.Children.Add(WallTile.BottomLeft(_rest));
        var words = WallTile.Words("", "", out _latest, out _latestMeta);
        _tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
        _tile.Lit.Children.Add(_copyLatest);
        _history.Changed += () =>
        {
            if (_tile.IsVisible) RefreshTile();
        };
        // Relative times: rebuild whenever it comes into view rather than ticking.
        _tile.IsVisibleChanged += (_, _) =>
        {
            if (_tile.IsVisible) RefreshTile();
        };
        // A screen share starting or ending hides or reveals the items in place.
        Infrastructure.PrivacyState.Changed += _ => _tile.Dispatcher.BeginInvoke(RefreshTile);
        RefreshTile();
    }

    static bool Private => Infrastructure.PrivacyState.Presenting;

    public FrameworkElement Compact => _tile;

    /// <summary>"Save as note" on a text item in the detail; returns whether it was saved.</summary>
    public Func<ClipEntry, bool>? SaveAsNote { get; init; }

    // ---- Tile ------------------------------------------------------------------------------------

    void RefreshTile()
    {
        var items = _history.Items.OrderByDescending(e => e.Time).ToList();
        _count.Text = items.Count.ToString(CultureInfo.InvariantCulture);
        _countLabel.Text = items.Count == 1 ? "Item" : "Items";
        _copyLatest.Content = null;
        if (items.Count == 0)
        {
            _latest.Text = "Nothing copied yet";
            _latestMeta.Text = "Copy something and it shows up here";
            CardKit.Name(_tile, "Clipboard, empty. Open clipboard");
            return;
        }
        if (Private)
        {
            _latest.Text = Shammy.Core.Connect.PrivacyText.ClipboardSummary(items.Count);
            _latestMeta.Text = Shammy.Core.Connect.PrivacyText.Hidden;
            CardKit.Name(_tile, $"Clipboard, {items.Count} items, hidden while sharing. Open clipboard");
            return;
        }
        var latest = items[0];
        _latest.Text = ShortText(latest);
        _latestMeta.Text = ClipCardText.Footer(latest, DateTime.Now);
        _copyLatest.Content = CardKit.Primary("Copy the latest item", CardKit.Icon(CardKit.Paths.Copy, CardKit.OnAccent, 12, 2.4), () => _copy(latest), 28);
        CardKit.Name(_tile, $"Clipboard, {items.Count} items. Latest: {ShortText(latest)}. Open clipboard");
    }

    static string ShortText(ClipEntry e) => ClipCardText.Shape(e) switch
    {
        ClipShape.Image => $"Image · {ClipText.Dimensions(e.Width, e.Height)}",
        ClipShape.Files => ClipText.FilesTitle(e.Files),
        ClipShape.Link => ClipCardText.LinkLabel(e.Text),
        _ => ClipText.FirstLine(e.Text, 80),
    };

    /// <summary>A row's lead: the image itself, the colour, or a quiet mark for the kind.</summary>
    static FrameworkElement Lead(ClipEntry e)
    {
        var shape = ClipCardText.Shape(e);
        if (shape == ClipShape.Image && e.Tag is ClipImage img) return ClipImages.Tile(img.Thumb, 28, 20, 4);
        if (shape == ClipShape.Colour && ClipCardText.ColourOf(e.Text) is { } c)
            return new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), BorderBrush = CardKit.Hairline, BorderThickness = new Thickness(1) };
        var path = shape switch
        {
            ClipShape.Link => CardKit.Paths.Link,
            ClipShape.Files => CardKit.Paths.Folder,
            ClipShape.Image => CardKit.Paths.Camera,
            _ => CardKit.Paths.Note,
        };
        return new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(5), Background = CardKit.PillBg, Child = CardKit.Icon(path, CardKit.Ink2, 13, 2) };
    }

    // ---- Detail ----------------------------------------------------------------------------------

    public FrameworkElement BuildDetail() => new Detail(this).Root;

    sealed class Detail
    {
        readonly ClipboardCard _card;
        readonly StackPanel _rows = new();
        readonly TrackedText _count = CardKit.Poster("", 96), _countLabel = CardKit.Label("Items");
        readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal };
        readonly DispatcherTimer _undoExpiry = new() { Interval = ClipboardModule.UndoTime };
        readonly TextBox _search;
        Action? _undo;
        bool _pinnedOnly;

        public Detail(ClipboardCard card)
        {
            _card = card;
            Root = new WallDetail(Fields.List(), "", "Clipboard");
            _countLabel.Margin = new Thickness(0, 8, 0, 0);
            Root.ArtBottomLeft(_count, _countLabel);
            _filters.Margin = new Thickness(0, -2, 0, 0);
            Root.LineContent = _filters;
            Root.Body.Children.Add(WallDetail.Scroll(_rows));
            _undoExpiry.Tick += (_, _) => ShowUndo(null);

            _search = new TextBox { Style = Ui.Tokens.StyleRes("BareTextBox"), FontSize = 13, FontFamily = CardKit.Face(FontWeights.Medium), Foreground = CardKit.Ink, CaretBrush = CardKit.Accent };
            var hint = CardKit.Text("Search clipboard", 13, FontWeights.Medium, secondary: true);
            hint.IsHitTestVisible = false;
            _search.TextChanged += (_, _) =>
            {
                hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Refresh();
            };
            var lens = CardKit.Icon(CardKit.Paths.Search, CardKit.Ink2, 14, 2.2);
            lens.Margin = new Thickness(0, 0, 8, 0);
            var row = new DockPanel();
            DockPanel.SetDock(lens, Dock.Left);
            row.Children.Add(lens);
            row.Children.Add(new Grid { Children = { hint, _search } });
            var box = new Border
            {
                Width = 212,
                Height = 32,
                CornerRadius = new CornerRadius(10),
                Background = CardKit.PillBg,
                BorderBrush = CardKit.Hairline,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 0, 11, 0),
                Child = row,
                Cursor = Cursors.IBeam,
            };
            box.MouseLeftButtonUp += (_, _) => _search.Focus();
            Root.Corner = box;

            Root.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    _search.Focus();
                    e.Handled = true;
                }
            };
            Action changed = () => Root.Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
            Action<bool> privacy = _ => changed();
            Root.Loaded += (_, _) =>
            {
                _card._history.Changed += changed.Invoke;
                Infrastructure.PrivacyState.Changed += privacy;
            };
            Root.Unloaded += (_, _) =>
            {
                _card._history.Changed -= changed.Invoke;
                Infrastructure.PrivacyState.Changed -= privacy;
                _undoExpiry.Stop();
            };
            Refresh();
        }

        public WallDetail Root { get; }

        void Clear() => ShowUndo(_card._clear());

        void Undo()
        {
            var undo = _undo;
            ShowUndo(null);
            undo?.Invoke();
        }

        /// <summary>Offer Undo in Clear's place for a few seconds (null puts Clear back).</summary>
        void ShowUndo(Action? undo)
        {
            _undo = undo;
            _undoExpiry.Stop();
            if (undo is not null) _undoExpiry.Start();
            Refresh();
        }

        /// <summary>A small text filter under the title: white with an underline when on, like the chips.</summary>
        static FrameworkElement Filter(string text, bool on, Action click)
        {
            var label = CardKit.Text(text, 12.5, FontWeights.SemiBold);
            label.Foreground = on ? CardKit.Ink : CardKit.Ink2;
            if (on) label.TextDecorations = TextDecorations.Underline;
            var b = new Border { Child = label, Background = Brushes.Transparent, Cursor = Cursors.Hand, Padding = new Thickness(0, 0, 14, 0) };
            b.MouseEnter += (_, _) => label.Foreground = CardKit.Ink;
            b.MouseLeave += (_, _) => label.Foreground = on ? CardKit.Ink : CardKit.Ink2;
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;
            b.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            CardKit.Name(b, text);
            return b;
        }

        void Refresh()
        {
            var now = DateTime.Now;
            var all = _card._history.Items;
            int pinned = _card._history.PinnedCount;
            if (pinned == 0) _pinnedOnly = false;
            Root.Kicker.Text = $"Clipboard · {(all.Count == 1 ? "1 item" : $"{all.Count} items")} · last 24 hours";
            _count.Text = all.Count.ToString(CultureInfo.InvariantCulture);
            _countLabel.Text = all.Count == 1 ? "Item" : "Items";

            _filters.Children.Clear();
            _filters.Children.Add(Filter("All", !_pinnedOnly, () => { _pinnedOnly = false; Refresh(); }));
            if (pinned > 0) _filters.Children.Add(Filter($"Pinned · {pinned}", _pinnedOnly, () => { _pinnedOnly = true; Refresh(); }));
            if (_undo is not null) _filters.Children.Add(Filter("Undo clear", false, Undo));
            else if (all.Count > pinned) _filters.Children.Add(Filter("Clear", false, Clear));

            var query = _search.Text.Trim();
            var shown = all.Where(e => !_pinnedOnly || e.Pinned).Where(e => Matches(e, query)).OrderByDescending(e => e.Pinned).ThenByDescending(e => e.Time).ToList();
            _rows.Children.Clear();
            if (Private)
            {
                _rows.Children.Add(Empty(Shammy.Core.Connect.PrivacyText.Hidden));
                return;
            }
            foreach (var e in shown) _rows.Children.Add(Row(e, now));
            if (shown.Count == 0) _rows.Children.Add(Empty(all.Count == 0 ? "Copy something and it shows up here" : "Nothing matches"));
        }

        static TextBlock Empty(string text)
        {
            var t = CardKit.Text(text, 13, FontWeights.Medium, secondary: true);
            t.Margin = new Thickness(0, 8, 0, 0);
            return t;
        }

        static bool Matches(ClipEntry e, string query)
        {
            if (query.Length == 0) return true;
            return e.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Files.Any(f => f.Contains(query, StringComparison.OrdinalIgnoreCase))
                || (e.Kind == ClipKind.Image && "image".Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        FrameworkElement Row(ClipEntry e, DateTime now)
        {
            var shape = ClipCardText.Shape(e);
            var meta = CardKit.Text(ClipCardText.Footer(e, now), 12, FontWeights.Medium, secondary: true);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 0) };
            void Act(string name, string path, Action run)
            {
                var b = CardKit.Quiet(name, CardKit.Icon(path, CardKit.Ink, 11, 2.2), run, 24);
                b.Margin = new Thickness(4, 0, 0, 0);
                actions.Children.Add(b);
            }
            if (shape == ClipShape.Link) Act("Open link", CardKit.Paths.Open, () => _card._open(e.Text.Trim()));
            if (e.Kind == ClipKind.Text && _card.SaveAsNote is { } save) Act("Save as note", CardKit.Paths.Note, () =>
            {
                if (save(e)) meta.Text = "Saved as a note";
            });
            Act(e.Pinned ? "Unpin" : "Pin", CardKit.Paths.Pin, () => _card._history.SetPinned(e, !e.Pinned));
            Act("Remove", CardKit.Paths.Close, () => _card._history.Remove(e));
            var pin = CardKit.Icon(CardKit.Paths.Pin, CardKit.Ink, 12, 2.2);
            pin.Margin = new Thickness(8, 0, 0, 0);
            pin.Visibility = e.Pinned ? Visibility.Visible : Visibility.Collapsed;
            var right = CardKit.Row(0, meta, pin, actions);
            var name = CardKit.Text(ShortText(e), 13.5, FontWeights.SemiBold);
            if (shape == ClipShape.Code) name.FontFamily = new FontFamily("Cascadia Mono, Consolas");
            var row = WallDetail.ListRow(Lead(e), name, right, 34);
            row.Cursor = Cursors.Hand;
            row.ToolTip = e.Kind == ClipKind.Text ? (e.Text.Length > 400 ? e.Text[..400] + "…" : e.Text)
                : e.Kind == ClipKind.Files ? string.Join("\n", e.Files.Take(12)) : null;
            row.MouseEnter += (_, _) => actions.Visibility = Visibility.Visible;
            row.MouseLeave += (_, _) => actions.Visibility = Visibility.Collapsed;
            row.MouseLeftButtonUp += (_, ev) =>
            {
                if (ev.Handled) return;
                _card._copy(e);
                Flash(meta, ClipCardText.Footer(e, now));
            };
            CardKit.Name(row, $"{ClipCardText.KindLabel(shape, e.Files.Length)}: {ShortText(e)}{(e.Pinned ? ", pinned" : "")}. Click to copy");
            return row;
        }

        static void Flash(TextBlock label, string back)
        {
            label.Text = "Copied";
            label.Foreground = CardKit.Ink;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                label.Text = back;
                label.Foreground = CardKit.Ink2;
            };
            t.Start();
        }
    }
}
