using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Clipboard;
using Shammy.Views;
using Shammy.Views.Cards;
using Shammy.Views.Screens;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>
/// The clipboard card on the Focus Wall: a tile whose big number is how many items are kept; focused, the latest
/// one and Copy. The detail is a card sheet (<see cref="Detail"/>): a sideways strip of glass tiles, each kind shown
/// as itself — click to copy, with pin, open, save-as-note and remove on hover — search and ⋯ (Pinned / All, Clear
/// with Undo) in the header; one item is one wide row. Shared screens see counts only.
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

    /// <summary>One item or none opens the small island (one wide row, or one line); more open the strip of tiles.</summary>
    public FrameworkElement BuildDetail() => new Detail(this, _history.Items.Count <= 1 ? Detail.NarrowWidth : Detail.WideWidth);

    /// <summary>A one-time code ("482913", "4829 13"): 4 to 8 digits, shown as big light numbers.</summary>
    static bool IsCode(ClipEntry e, out string digits)
    {
        digits = "";
        if (e.Kind != ClipKind.Text) return false;
        var t = e.Text.Trim();
        if (t.Length is < 4 or > 11) return false;
        var d = new string(t.Where(c => c is not (' ' or '-')).ToArray());
        if (d.Length is < 4 or > 8 || !d.All(char.IsAsciiDigit)) return false;
        digits = d;
        return true;
    }

    /// <summary>
    /// The opened Clipboard card (the card family's shape). Two or more items: 720 wide, a row of glass tiles side by
    /// side, newest first, scrolling sideways (the wheel scrolls it); each kind shows itself (text as three lines, a
    /// link with its site, a code as big light digits, an image as the picture, a colour as a swatch), and the newest
    /// wears the accent edge. Search and ⋯ (All / Pinned, Clear with Undo) sit in the header. One item: 460 wide and
    /// one wide row. None: one short line. Click a tile to copy it; on hover, open, save as note, pin and remove.
    /// While the screen is shared, only the count shows.
    /// </summary>
    sealed class Detail : CardSheet
    {
        public const double WideWidth = 720, NarrowWidth = 460;
        const double TileW = 162, TileH = 150;
        const string Dots = "M5 12h.01 M12 12h.01 M19 12h.01";
        static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

        readonly ClipboardCard _card;
        readonly TextBox _search = new();
        readonly Border _searchBox;
        readonly Button _more;
        readonly StackPanel _options = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        readonly ContentControl _content = new() { Focusable = false };
        readonly DispatcherTimer _undoExpiry = new() { Interval = ClipboardModule.UndoTime };
        Action? _undo;
        bool _pinnedOnly, _optionsOpen;

        public Detail(ClipboardCard card, double width) : base("Clipboard", width)
        {
            _card = card;
            BodyGap = 12;
            _searchBox = Sheet.Field(_search, "Search clipboard", CardKit.Paths.Search, 34);
            _searchBox.Width = width >= WideWidth ? 190 : 150;
            _searchBox.MouseLeftButtonUp += (_, _) => _search.Focus();
            _search.TextChanged += (_, _) => Refresh();
            Action(_searchBox);
            _more = Action(Sheet.Round("Clipboard options", Dots, () =>
            {
                _optionsOpen = !_optionsOpen;
                Refresh();
            }, 34, iconSize: 18));

            var body = new StackPanel();
            body.Children.Add(_options);
            body.Children.Add(_content);
            Body.Children.Add(body);
            _undoExpiry.Tick += (_, _) => ShowUndo(null);

            PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && _searchBox.IsVisible)
                {
                    _search.Focus();
                    e.Handled = true;
                }
            };
            Action changed = () => Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
            Action<bool> privacy = _ => changed();
            Loaded += (_, _) =>
            {
                _card._history.Changed += changed.Invoke;
                Infrastructure.PrivacyState.Changed += privacy;
            };
            Unloaded += (_, _) =>
            {
                _card._history.Changed -= changed.Invoke;
                Infrastructure.PrivacyState.Changed -= privacy;
                _undoExpiry.Stop();
            };
            Refresh();
        }

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

        void Refresh()
        {
            var now = DateTime.Now;
            var all = _card._history.Items;
            int pinned = _card._history.PinnedCount;
            if (pinned == 0) _pinnedOnly = false;
            int today = all.Count(e => e.Time.Date == now.Date);
            Status = all.Count == 0 ? null : today > 0 ? $"{today} today" : all.Count == 1 ? "1 kept" : $"{all.Count} kept";

            bool many = all.Count > 1 && !Private;
            _searchBox.Visibility = many || _search.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _more.Visibility = many || _undo is not null ? Visibility.Visible : Visibility.Collapsed;
            _more.ToolTip = _optionsOpen ? "Hide the clipboard options" : "Clipboard options";
            BuildOptions(all.Count, pinned);

            if (Private)
            {
                _content.Content = Line(Shammy.Core.Connect.PrivacyText.Hidden);
                return;
            }
            if (all.Count == 0)
            {
                _content.Content = Line("Nothing copied yet. Copy something and it shows up here.");
                return;
            }
            var newest = all.MaxBy(e => e.Time);
            var query = _search.Text.Trim();
            if (all.Count == 1 && query.Length == 0)
            {
                _content.Content = Wide(all[0], now);
                return;
            }
            var shown = all.Where(e => !_pinnedOnly || e.Pinned).Where(e => Matches(e, query)).OrderByDescending(e => e.Time).ToList();
            if (shown.Count == 0)
            {
                _content.Content = Line("Nothing matches. Try fewer or other words.");
                return;
            }
            var strip = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var e in shown)
            {
                var tile = Tile(e, ReferenceEquals(e, newest), now);
                if (strip.Children.Count > 0) tile.Margin = new Thickness(12, 0, 0, 0);
                strip.Children.Add(tile);
            }
            var scroll = new ScrollViewer
            {
                Content = strip,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            // Older items are to the right: the wheel scrolls sideways.
            scroll.PreviewMouseWheel += (_, e) =>
            {
                scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
                e.Handled = true;
            };
            _content.Content = scroll;
        }

        /// <summary>Behind ⋯: All / Pinned (when something is pinned) and Clear, or Undo for a few seconds after.</summary>
        void BuildOptions(int count, int pinned)
        {
            _options.Children.Clear();
            bool open = (_optionsOpen && count > 1) || _undo is not null;
            _options.Visibility = open && !Private ? Visibility.Visible : Visibility.Collapsed;
            if (!open) return;
            if (pinned > 0)
            {
                var filter = Sheet.Segmented(new[] { "All", $"Pinned · {pinned}" }, _pinnedOnly ? 1 : 0, i =>
                {
                    _pinnedOnly = i == 1;
                    Refresh();
                }, "Show");
                filter.Margin = new Thickness(0, 0, 8, 0);
                _options.Children.Add(filter);
            }
            if (_undo is not null) _options.Children.Add(Sheet.Cap("Undo clear", "Undo clear", Undo, "M9 14 4 9l5-5 M4 9h10a6 6 0 0 1 0 12h-3"));
            else if (count > pinned) _options.Children.Add(Sheet.Cap("Clear", "Clear", Clear, CardKit.Paths.Close));
        }

        static TextBlock Line(string text)
        {
            var t = Sheet.Meta(text, 14);
            t.Margin = new Thickness(4, 0, 0, 0);
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

        // ---- A tile ------------------------------------------------------------------------------------

        /// <summary>"Text · just now", "Link · 4m", "Code · 9m".</summary>
        static string Kicker(ClipEntry e, ClipShape shape, bool code, DateTime now)
        {
            var age = ClipText.Relative(e.Time, now);
            return $"{(code ? "Code" : ClipCardText.KindLabel(shape, e.Files.Length))} · {(age == "now" ? "just now" : age)}";
        }

        FrameworkElement Tile(ClipEntry e, bool newest, DateTime now)
        {
            var shape = ClipCardText.Shape(e);
            bool code = IsCode(e, out var digits);
            bool picture = shape == ClipShape.Image && e.Tag is ClipImage;

            var stack = new DockPanel { LastChildFill = true };
            var foot = Foot(e, out var meta);
            DockPanel.SetDock(foot, Dock.Bottom);
            stack.Children.Add(foot);

            var kicker = CardKit.Label(Kicker(e, shape, code, now), newest ? Ink.Soft : CardKit.Ink2, 10);
            if (picture && e.Tag is ClipImage img)
            {
                // The picture fills the top, its label over it.
                var art = new Grid { Height = 96, Margin = new Thickness(-13, -12, -13, 8) };
                art.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(19, 19, 0, 0),
                    Background = new ImageBrush(img.Thumb) { Stretch = Stretch.UniformToFill },
                });
                RenderOptions.SetBitmapScalingMode(art, BitmapScalingMode.HighQuality);
                kicker.Foreground = Ui.Tokens.Brush("#D9FFFFFF");
                kicker.HorizontalAlignment = HorizontalAlignment.Left;
                kicker.VerticalAlignment = VerticalAlignment.Bottom;
                kicker.Margin = new Thickness(13, 0, 0, 9);
                art.Children.Add(kicker);
                if (e.Pinned) art.Children.Add(PinMark(HorizontalAlignment.Right, new Thickness(0, 10, 12, 0)));
                DockPanel.SetDock(art, Dock.Top);
                stack.Children.Add(art);
                var name = Sheet.Word(e.Source.Contains("Snipping", StringComparison.OrdinalIgnoreCase) ? "Screenshot" : "Image", 13);
                DockPanel.SetDock(name, Dock.Top);
                stack.Children.Add(name);
                meta.Text = ClipText.Dimensions(e.Width, e.Height).Replace("×", " × ");
            }
            else
            {
                var top = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                top.Children.Add(kicker);
                if (e.Pinned) top.Children.Add(PinMark(HorizontalAlignment.Right, new Thickness(0)));
                DockPanel.SetDock(top, Dock.Top);
                stack.Children.Add(top);
                stack.Children.Add(Shown(e, shape, code, digits));
            }

            var tile = new Pressable(newest ? Ink.Tint : Sheet.GlassFill, newest ? Ink.Tint : Sheet.CapFill, newest ? Ink.TintEdge : Sheet.GlassEdge)
            {
                Width = TileW,
                Height = TileH,
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(13, 12, 13, 12),
                Child = stack,
                PressScale = 0.98,
                ClipToBounds = false,
            };
            tile.BorderThickness = new Thickness(newest ? 1.5 : 1);
            Wire(tile, e, foot, meta, shape);
            return tile;
        }

        static FrameworkElement PinMark(HorizontalAlignment h, Thickness margin)
        {
            var pin = CardKit.Icon(CardKit.Paths.Pin, CardKit.Ink, 12, 2.2);
            pin.HorizontalAlignment = h;
            pin.VerticalAlignment = VerticalAlignment.Top;
            pin.Margin = margin;
            CardKit.Name(pin, "Pinned");
            return pin;
        }

        /// <summary>What the item is, shown as itself.</summary>
        static FrameworkElement Shown(ClipEntry e, ClipShape shape, bool code, string digits)
        {
            if (code)
            {
                var n = Sheet.Num(digits, digits.Length > 6 ? 28 : 34);
                n.VerticalAlignment = VerticalAlignment.Top;
                return n;
            }
            switch (shape)
            {
                case ClipShape.Link:
                {
                    var (site, page) = LinkParts(e.Text);
                    var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Tokens.Brush("#1FFFFFFF"), Child = CardKit.Icon(CardKit.Paths.Link, CardKit.Ink, 14, 2.2) };
                    var host = Sheet.Word(site, 12, FontWeights.SemiBold);
                    host.Margin = new Thickness(8, 0, 0, 0);
                    var col = new StackPanel();
                    col.Children.Add(Sheet.Row(0, badge, host));
                    if (page.Length > 0)
                    {
                        var t = Lines(page, 2, 13);
                        t.Margin = new Thickness(0, 8, 0, 0);
                        col.Children.Add(t);
                    }
                    return col;
                }
                case ClipShape.Colour when ClipCardText.ColourOf(e.Text) is { } c:
                {
                    var swatch = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), BorderBrush = Sheet.GlassEdge, BorderThickness = new Thickness(1) };
                    var hex = Sheet.Word(e.Text.Trim(), 13);
                    hex.Margin = new Thickness(0, 8, 0, 0);
                    hex.HorizontalAlignment = HorizontalAlignment.Left;
                    return Sheet.Column(0, swatch, hex);
                }
                case ClipShape.Files:
                {
                    var badge = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Tokens.Brush("#1FFFFFFF"), Child = CardKit.Icon(CardKit.Paths.Folder, CardKit.Ink, 14, 2) };
                    badge.HorizontalAlignment = HorizontalAlignment.Left;
                    var t = Lines(ClipText.FilesTitle(e.Files), 2, 13);
                    t.Margin = new Thickness(0, 8, 0, 0);
                    return Sheet.Column(0, badge, t);
                }
                case ClipShape.Image:
                    return Sheet.Column(0, CardKit.Icon(CardKit.Paths.Camera, CardKit.Ink2, 20, 2), Lines(ClipText.Dimensions(e.Width, e.Height), 1, 13));
                case ClipShape.Code:
                {
                    var t = Lines(e.Text.Replace("\r\n", "\n").Trim('\n'), 4, 11.5, wrap: false);
                    t.FontFamily = Mono;
                    t.FontWeight = FontWeights.Normal;
                    t.Foreground = CardKit.Ink2;
                    return t;
                }
                default:
                    return Lines(ClipText.Preview(e.Text.Length > 600 ? e.Text[..600] : e.Text, 240), 3, 13);
            }
        }

        /// <summary>("wikipedia.org", "Monsoon of South Asia") from a link: the site, and the page in words.</summary>
        static (string Site, string Page) LinkParts(string url)
        {
            var label = ClipCardText.LinkLabel(url);
            int slash = label.IndexOf('/');
            if (slash < 0) return (label, "");
            var site = label[..slash];
            var last = label[(slash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            var words = last.Replace('_', ' ').Replace('-', ' ').Trim();
            return (site, words.Length > 0 ? words : label[(slash + 1)..]);
        }

        /// <summary>Up to <paramref name="lines"/> lines of bold text, the last one trimmed.</summary>
        static TextBlock Lines(string text, int lines, double size, bool wrap = true)
        {
            var t = CardKit.Text(text, size, FontWeights.SemiBold);
            t.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            t.LineHeight = Math.Round(size * 1.35);
            t.MaxHeight = t.LineHeight * lines;
            t.VerticalAlignment = VerticalAlignment.Top;
            return t;
        }

        /// <summary>The tile's foot: where it came from; on hover, its actions.</summary>
        static FrameworkElement Foot(ClipEntry e, out TextBlock meta)
        {
            meta = Sheet.Meta(e.Source, 12);
            var grid = new Grid { Height = 24, Margin = new Thickness(0, 6, 0, 0) };
            grid.Children.Add(meta);
            return grid;
        }

        /// <summary>Hover swaps the foot for the actions; a click copies the item.</summary>
        void Wire(Pressable tile, ClipEntry e, FrameworkElement foot, TextBlock meta, ClipShape shape)
        {
            var actions = HoverActions(e, shape, meta);
            actions.Visibility = Visibility.Collapsed;
            actions.HorizontalAlignment = HorizontalAlignment.Left;
            ((Grid)foot).Children.Add(actions);
            string back = meta.Text;
            tile.MouseEnter += (_, _) =>
            {
                actions.Visibility = Visibility.Visible;
                meta.Visibility = Visibility.Hidden;
            };
            tile.MouseLeave += (_, _) =>
            {
                actions.Visibility = Visibility.Collapsed;
                meta.Visibility = Visibility.Visible;
            };
            tile.Click += () =>
            {
                _card._copy(e);
                Flash(meta, back, actions);
            };
            tile.ToolTip = e.Kind == ClipKind.Text ? (e.Text.Length > 400 ? e.Text[..400] + "…" : e.Text)
                : e.Kind == ClipKind.Files ? string.Join("\n", e.Files.Take(12)) : null;
            CardKit.Name(tile, $"{ClipCardText.KindLabel(shape, e.Files.Length)}: {ShortText(e)}{(e.Pinned ? ", pinned" : "")}. Click to copy");
        }

        StackPanel HoverActions(ClipEntry e, ClipShape shape, TextBlock meta)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            void Act(string name, string path, Action run)
            {
                var b = Sheet.Round(name, path, run, 24, iconSize: 11);
                if (row.Children.Count > 0) b.Margin = new Thickness(4, 0, 0, 0);
                row.Children.Add(b);
            }
            if (shape == ClipShape.Link) Act("Open link", CardKit.Paths.Open, () => _card._open(e.Text.Trim()));
            if (e.Kind == ClipKind.Text && _card.SaveAsNote is { } save) Act("Save as note", CardKit.Paths.Note, () =>
            {
                if (!save(e)) return;
                meta.Text = "Saved as a note";
                Ui.A11y.Announce(meta.Text, meta);
            });
            Act(e.Pinned ? "Unpin" : "Pin", CardKit.Paths.Pin, () => _card._history.SetPinned(e, !e.Pinned));
            Act("Remove", CardKit.Paths.Close, () => _card._history.Remove(e));
            return row;
        }

        static void Flash(TextBlock label, string back, FrameworkElement actions)
        {
            label.Text = "Copied";
            label.Foreground = CardKit.Ink;
            label.Visibility = Visibility.Visible;
            actions.Visibility = Visibility.Collapsed;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                label.Text = back;
                label.Foreground = CardKit.Ink2;
            };
            t.Start();
        }

        // ---- One item ----------------------------------------------------------------------------------

        /// <summary>The only item, as one wide row with the accent edge: what it is, where it came from and when.</summary>
        FrameworkElement Wide(ClipEntry e, DateTime now)
        {
            var shape = ClipCardText.Shape(e);
            var meta = Sheet.Meta(ClipCardText.Footer(e, now), 12);
            var foot = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            foot.Children.Add(meta);
            var text = IsCode(e, out var digits) ? Sheet.Num(digits, 22) : Sheet.Word(ShortText(e), 13, FontWeights.SemiBold);
            var grid = new Grid { MinHeight = 24 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (shape is ClipShape.Image or ClipShape.Colour)
            {
                var lead = Lead(e);
                lead.Margin = new Thickness(0, 0, 10, 0);
                grid.Children.Add(lead);
            }
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(foot, 2);
            grid.Children.Add(foot);
            if (e.Pinned)
            {
                var pin = PinMark(HorizontalAlignment.Right, new Thickness(8, 0, 0, 0));
                pin.VerticalAlignment = VerticalAlignment.Center;
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(pin, 3);
                grid.Children.Add(pin);
            }
            var tile = new Pressable(Ink.Tint, Ink.Tint, Ink.TintEdge)
            {
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(14, 12, 14, 12),
                Child = grid,
                PressScale = 0.99,
            };
            tile.BorderThickness = new Thickness(1.5);
            Wire(tile, e, foot, meta, shape);
            return tile;
        }
    }
}
