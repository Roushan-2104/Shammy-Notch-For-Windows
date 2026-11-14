using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Shammy.Core.CircleMemory;
using Shammy.Infrastructure;
using Shammy.Views;
using Shammy.Views.Cards;

namespace Shammy.Modules.CircleMemory;

/// <summary>What the Circles card needs from the circle memory.</summary>
internal interface ICircleHost
{
    /// <summary>Newest first.</summary>
    IReadOnlyList<RememberedCircle> Circles { get; }

    bool Paused { get; set; }

    void Delete(RememberedCircle circle);

    void ClearAll();

    /// <summary>The kept picture (JPEG bytes), or null.</summary>
    byte[]? Picture(RememberedCircle circle);

    /// <summary>Shows the circle's answer again as the island's moment (or copies it).</summary>
    void Reopen(RememberedCircle circle);

    void Copy(RememberedCircle circle);

    /// <summary>A picture circle's kept picture as a PNG file to drop ("Circle 14 Nov 4.12 PM.png"), or null.</summary>
    string? DragFile(RememberedCircle circle);

    /// <summary>Keeps the notch open while a drag or a menu is up (pair true with false).</summary>
    void HoldOpen(bool on);

    /// <summary>Words to open the card searching for (from a command or the pipe), taken once.</summary>
    string? TakeFilter();
}

/// <summary>
/// The Circles card (Memory.dc.html): a chip-only card whose detail is a 660-wide sheet. The header says how many
/// circles the last 30 days hold and keeps Pause memory and Clear all behind ⋯. Under it: a search box ("Find a
/// circle, in your own words"), the filter capsules, and the circles as glass rows (a 48 px picture or tag, the
/// bold what, the quiet answer, a when chip). Click a row to see its answer again, drag it out to drop the answer
/// (or a picture circle's picture) anywhere, right-click to copy or delete. While the screen is shared it hides.
/// </summary>
internal sealed class CirclesCard
{
    readonly ICircleHost _host;
    readonly WallTile _tile = new() { Field = Fields.List() };
    readonly TrackedText _count = CardKit.Poster("", 40);
    readonly TextBlock _first, _firstLine;
    Detail? _detail;

    public CirclesCard(ICircleHost host)
    {
        _host = host;
        var label = CardKit.Label("Last 30 days");
        label.Margin = new Thickness(0, 6, 0, 0);
        _tile.Art.Children.Add(CardKit.At(CardKit.Column(0, _count, label), HorizontalAlignment.Left, VerticalAlignment.Top, WallTile.Pad, WallTile.Pad));
        _tile.Rest.Children.Add(WallTile.BottomLeft(CardKit.Label("Circles")));
        var words = WallTile.Words("Circles", "", out _first, out _firstLine);
        _tile.Lit.Children.Add(CardKit.At(words, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, WallTile.Pad, 0, WallTile.Pad, 10));
    }

    public FrameworkElement Compact => _tile;

    /// <summary>After any change (a new circle, a delete, pause): the tile and an open card follow.</summary>
    public void Refresh()
    {
        bool hidden = PrivacyState.Presenting;
        int n = _host.Circles.Count;
        _count.Text = hidden ? "–" : n.ToString(CultureInfo.InvariantCulture);
        _first.Text = hidden ? "Hidden while sharing" : n == 0 ? "Nothing circled yet" : "Find a circle";
        _firstLine.Text = hidden ? "" : _host.Paused ? "Memory paused" : "Say “my circles”";
        CardKit.Name(_tile, hidden ? "Circles, hidden while your screen is shared" : $"Circles, {n} in the last 30 days. Open circles");
        _detail?.Refresh();
    }

    public FrameworkElement BuildDetail()
    {
        var d = new Detail(this, _host.TakeFilter());
        _detail = d;
        d.Unloaded += (_, _) =>
        {
            if (_detail == d) _detail = null;
        };
        return d;
    }

    /// <summary>One line for the test pipe about the open card, or "closed".</summary>
    public string DescribeDetail() => _detail?.Describe() ?? "closed";

    sealed class Detail : CardSheet
    {
        const double SheetWidth = 660, RowHeight = 66, RowGap = 8, Thumb = 48;
        const int MaxRows = 60;
        const string Dots = "M5 12h.01 M12 12h.01 M19 12h.01";

        readonly CirclesCard _card;
        readonly ICircleHost _host;
        readonly TextBox _input = new();
        readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        readonly ContentControl _list = new() { Focusable = false, Margin = new Thickness(0, 12, 0, 0) };
        readonly Border _confirm;
        readonly TextBlock _confirmText = Sheet.Meta("", 13);
        readonly StackPanel _search = new();
        readonly Border _field;
        readonly TextBlock _hidden = Sheet.Meta("Hidden while your screen is shared. Nothing is remembered until sharing ends.", 13);
        readonly Button _more;
        readonly Dictionary<string, ImageSource?> _pictures = new();
        CircleGroup _group = CircleGroup.All;
        RememberedCircle? _pressed;
        Point _pressedAt;
        bool _dragging;
        int _shown;
        DateTime _day = DateTime.Today;

        public Detail(CirclesCard card, string? filter) : base("Circles", SheetWidth)
        {
            _card = card;
            _host = card._host;
            BodyGap = 14;
            _more = Action(Sheet.Round("Circle memory options", Dots, OpenOptions, 32, iconSize: 18));

            _input.Text = filter ?? "";
            AutomationProperties.SetName(_input, "Find a circle");
            _input.TextChanged += (_, _) => Fill();
            _input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || _input.Text.Length == 0) return;
                _input.Clear();
                e.Handled = true;
            };
            var field = _field = Sheet.Field(_input, "Find a circle, in your own words", CardKit.Paths.Search, 38);
            field.MouseLeftButtonUp += (_, _) => _input.Focus();
            BuildFilters();

            var sure = Sheet.Cap("Sure?", "Yes, clear every circle", () =>
            {
                HideConfirm();
                _host.ClearAll();
            }, accent: true, height: 32);
            var cancel = Sheet.Cap("Cancel", "Keep the circles", HideConfirm, height: 32);
            _confirmText.TextWrapping = TextWrapping.Wrap;
            _confirmText.TextTrimming = TextTrimming.None;
            _confirmText.Margin = new Thickness(0, 0, 12, 0);
            _confirm = Sheet.Tile(Sheet.Spread(_confirmText, Sheet.Row(8, cancel, sure)), new Thickness(16, 10, 10, 10), 20);
            _confirm.Visibility = Visibility.Collapsed;
            _confirm.MinHeight = 38 + 12 + 32;

            _search.Children.Add(_confirm);
            _search.Children.Add(field);
            _search.Children.Add(_filters);
            _search.Children.Add(_list);
            _hidden.Margin = new Thickness(4, 2, 0, 2);
            _hidden.TextWrapping = TextWrapping.Wrap;
            _hidden.TextTrimming = TextTrimming.None;
            var body = new Grid();
            body.Children.Add(_search);
            body.Children.Add(_hidden);
            Body.Children.Add(body);

            // Sharing can start or stop while the card is open.
            void OnPrivacy(bool _) => Dispatcher.BeginInvoke(Refresh);
            Loaded += (_, _) =>
            {
                PrivacyState.Changed -= OnPrivacy;
                PrivacyState.Changed += OnPrivacy;
            };
            Unloaded += (_, _) => PrivacyState.Changed -= OnPrivacy;
            // "Today 2:14 PM" turns into "Yesterday" at midnight; only matters while it's looked at.
            CardKit.TickWhileVisible(this, TimeSpan.FromMinutes(1), () =>
            {
                if (DateTime.Today == _day) return;
                _day = DateTime.Today;
                Refresh();
            });
            Refresh();
        }

        public void Refresh()
        {
            bool hidden = PrivacyState.Presenting;
            _search.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            _hidden.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
            _more.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
            RefreshStatus();
            if (!hidden) Fill();
            else _list.Content = null;
        }

        void RefreshStatus()
        {
            if (PrivacyState.Presenting)
            {
                Status = "hidden while sharing";
                return;
            }
            int n = _host.Circles.Count;
            Status = $"{n} in the last {CircleLog.KeepDays} days" + (_host.Paused ? " · paused" : "");
        }

        // ---- Filters ---------------------------------------------------------------------------------

        void BuildFilters()
        {
            _filters.Children.Clear();
            foreach (var (group, label) in CircleKinds.Filters)
            {
                var cap = FilterCap(label, group == _group, () =>
                {
                    _group = group;
                    BuildFilters();
                    Fill();
                });
                if (_filters.Children.Count > 0) cap.Margin = new Thickness(6, 0, 0, 0);
                _filters.Children.Add(cap);
            }
        }

        /// <summary>A capsule that is one of a set: the chosen one lifted in the accent's tint.</summary>
        static Button FilterCap(string text, bool on, Action pick)
        {
            var label = CardKit.Text(text, 13, FontWeights.SemiBold);
            label.Foreground = on ? CardKit.Ink : CardKit.Ink2;
            label.VerticalAlignment = VerticalAlignment.Center;
            var face = new Border
            {
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(13, 0, 13, 0),
                Background = on ? Ink.Tint : Sheet.CapFill,
                BorderBrush = on ? Ink.TintEdge : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Child = label,
                SnapsToDevicePixels = true,
            };
            var button = new Button { Content = face, Template = BareTemplate, Cursor = Cursors.Hand, ToolTip = text };
            AutomationProperties.SetName(button, $"Show {text.ToLowerInvariant()}");
            AutomationProperties.SetItemStatus(button, on ? "Chosen" : "");
            Ui.FocusKit.Button(button);
            if (!on)
            {
                button.MouseEnter += (_, _) => face.Background = Sheet.CapHover;
                button.MouseLeave += (_, _) => face.Background = Sheet.CapFill;
            }
            button.Click += (_, e) =>
            {
                pick();
                e.Handled = true;
            };
            return button;
        }

        static readonly ControlTemplate BareTemplate = MakeBare();

        static ControlTemplate MakeBare()
        {
            var t = new ControlTemplate(typeof(ButtonBase)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
            t.Seal();
            return t;
        }

        // ---- The list --------------------------------------------------------------------------------

        void Fill()
        {
            if (PrivacyState.Presenting) return;
            var now = DateTime.Now;
            var all = _host.Circles;
            var inGroup = _group == CircleGroup.All ? all : all.Where(c => CircleKinds.GroupOf(c.Kind) == _group).ToList();
            var words = _input.Text.Trim();
            IReadOnlyList<RememberedCircle> found = words.Length == 0
                ? inGroup
                : CircleSearch.Find(inGroup, CircleSearch.ParseFilter(words, now), requireAll: true);

            if (found.Count == 0)
            {
                _shown = 0;
                string text = all.Count == 0
                    ? _host.Paused ? "Memory is paused. Resume it from ⋯ to remember what you circle."
                        : "Nothing circled yet. Circle anything with the Copilot key and it’s kept here, on this PC, for 30 days."
                    : words.Length > 0 ? $"No circle matches “{words}”."
                    : "No circles of this kind yet.";
                var empty = Sheet.Meta(text, 13);
                empty.TextWrapping = TextWrapping.Wrap;
                empty.TextTrimming = TextTrimming.None;
                empty.Margin = new Thickness(4, 2, 0, 2);
                _list.Content = empty;
                return;
            }

            var rows = new StackPanel();
            foreach (var c in found.Take(MaxRows))
            {
                var row = Row(c, now);
                if (rows.Children.Count > 0) row.Margin = new Thickness(0, RowGap, 0, 0);
                rows.Children.Add(row);
            }
            _shown = Math.Min(found.Count, MaxRows);
            if (found.Count > MaxRows)
            {
                var more = Sheet.Meta($"The newest {MaxRows} of {found.Count}. Search to find older ones.", 12);
                more.Margin = new Thickness(4, RowGap + 2, 0, 0);
                rows.Children.Add(more);
            }
            // About five rows show; more scroll (the sheet fades the edge where more hide).
            _list.Content = new ScrollViewer
            {
                Content = rows,
                // Five rows and a glimpse of the sixth, so the sheet stays under its tallest (CardSheet.MaxFit).
                MaxHeight = 5 * RowHeight + 5 * RowGap + 4,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                PanningMode = PanningMode.VerticalOnly,
            };
        }

        FrameworkElement Row(RememberedCircle c, DateTime now)
        {
            var thumb = Picture(c);
            var what = Sheet.Word(c.What, 14);
            var answer = Sheet.Meta(c.Answer, 13);
            answer.Margin = new Thickness(0, 2, 0, 0);
            var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
            words.Children.Add(what);
            if (c.Answer.Length > 0) words.Children.Add(answer);
            var when = Sheet.Chip(CircleWhen.Chip(c.At, now, CardKit.H24), quiet: true);

            var grid = new Grid { MinHeight = Thumb };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Thumb) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(thumb);
            Grid.SetColumn(words, 1);
            grid.Children.Add(words);
            Grid.SetColumn(when, 2);
            grid.Children.Add(when);

            var tile = Sheet.Tile(grid, new Thickness(10, 8, 12, 8), 18);
            tile.Cursor = Cursors.Hand;
            tile.Focusable = true;
            tile.FocusVisualStyle = null;
            tile.ToolTip = (string.IsNullOrWhiteSpace(c.App) ? "" : $"Circled in {c.App} · ") + "Click to see it again · drag it out · right-click for more";
            CardKit.Name(tile, $"{c.What}. {c.Answer}. {CircleWhen.Chip(c.At, now, CardKit.H24)}");
            tile.MouseEnter += (_, _) => tile.Background = Sheet.CapFill;
            tile.MouseLeave += (_, _) => tile.Background = Sheet.GlassFill;
            tile.GotKeyboardFocus += (_, _) => tile.BorderBrush = Ink.TintEdge;
            tile.LostKeyboardFocus += (_, _) => tile.BorderBrush = Sheet.GlassEdge;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                _pressed = c;
                _pressedAt = e.GetPosition(this);
            };
            tile.MouseMove += (_, e) => DragOut(tile, e, c);
            tile.MouseLeftButtonUp += (_, e) =>
            {
                bool click = ReferenceEquals(_pressed, c) && !_dragging;
                _pressed = null;
                if (!click) return;
                e.Handled = true;
                _host.Reopen(c);
            };
            tile.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                RowMenu(tile, c);
            };
            tile.KeyDown += (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Enter or Key.Space:
                        e.Handled = true;
                        _host.Reopen(c);
                        break;
                    case Key.Apps:
                        e.Handled = true;
                        RowMenu(tile, c);
                        break;
                    case Key.Delete:
                        e.Handled = true;
                        _host.Delete(c);
                        break;
                }
            };
            return tile;
        }

        /// <summary>The row's 48 px square: the kept picture for a picture or product, else a short tag in a tint.</summary>
        FrameworkElement Picture(RememberedCircle c)
        {
            var group = CircleKinds.GroupOf(c.Kind);
            bool showPicture = c.HasPicture && (group is CircleGroup.Pictures or CircleGroup.Products || c.Words.Trim().Length == 0);
            if (showPicture && PictureOf(c) is { } image)
            {
                return new Border
                {
                    Width = Thumb,
                    Height = Thumb,
                    CornerRadius = new CornerRadius(12),
                    Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
                    BorderBrush = Sheet.GlassEdge,
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            var colour = group switch
            {
                CircleGroup.Times => Ink.C.Color,
                CircleGroup.Translations => Ink.A.Color,
                _ => Ink.B.Color,
            };
            var tagText = CircleKinds.Tag(c.Kind, c.Words);
            var tag = CardKit.Text(tagText, tagText.Length > 3 ? 11 : 13, FontWeights.Bold);
            tag.Foreground = new SolidColorBrush(Mix(colour, Colors.White, 0.62));
            tag.HorizontalAlignment = HorizontalAlignment.Center;
            tag.VerticalAlignment = VerticalAlignment.Center;
            tag.TextTrimming = TextTrimming.None;
            return new Border
            {
                Width = Thumb,
                Height = Thumb,
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x2E, colour.R, colour.G, colour.B)),
                Child = tag,
                VerticalAlignment = VerticalAlignment.Center,
                ClipToBounds = true,
            };
        }

        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

        ImageSource? PictureOf(RememberedCircle c)
        {
            if (_pictures.TryGetValue(c.Id, out var cached)) return cached;
            var image = CircleMemoryStore.ToImage(_host.Picture(c), (int)(Thumb * 2));
            _pictures[c.Id] = image;
            return image;
        }

        // ---- Drag, menus -----------------------------------------------------------------------------

        /// <summary>
        /// Dragging a row drops its answer as text anywhere that takes text; a picture circle also offers its picture
        /// as a PNG file ("Circle 14 Nov 4.12 PM.png"). The notch stays open meanwhile.
        /// </summary>
        void DragOut(FrameworkElement tile, MouseEventArgs e, RememberedCircle c)
        {
            if (!ReferenceEquals(_pressed, c) || e.LeftButton != MouseButtonState.Pressed || _dragging) return;
            var d = e.GetPosition(this) - _pressedAt;
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _pressed = null;
            var data = new DataObject();
            var text = c.AnswerText;
            if (text.Length > 0)
            {
                data.SetData(DataFormats.UnicodeText, text);
                data.SetData(DataFormats.Text, text);
            }
            if (CircleKinds.GroupOf(c.Kind) == CircleGroup.Pictures && c.HasPicture && _host.DragFile(c) is { } file)
                data.SetData(DataFormats.FileDrop, new[] { file });
            if (data.GetFormats().Length == 0) return;
            _dragging = true;
            _host.HoldOpen(true);
            try
            {
                DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Log.Warn("Dragging a circle out failed", ex);
            }
            finally
            {
                _dragging = false;
                _host.HoldOpen(false);
            }
        }

        void RowMenu(FrameworkElement target, RememberedCircle c) => ShowMenu(target, add =>
        {
            add("Copy answer", () => _host.Copy(c));
            add("Delete", () => _host.Delete(c));
        });

        void OpenOptions() => ShowMenu(_more, add =>
        {
            add(_host.Paused ? "Resume memory" : "Pause memory", () =>
            {
                _host.Paused = !_host.Paused;
                Refresh();
            });
            if (_host.Circles.Count > 0) add("Clear all…", ShowConfirm);
        });

        /// <summary>A right-click style menu that keeps the notch open while it's up.</summary>
        void ShowMenu(FrameworkElement target, Action<Action<string, Action>> fill)
        {
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
            fill((header, run) =>
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => run();
                menu.Items.Add(item);
            });
            _host.HoldOpen(true);
            menu.Closed += (_, _) => _host.HoldOpen(false);
            menu.IsOpen = true;
        }

        void ShowConfirm()
        {
            int n = _host.Circles.Count;
            _confirmText.Text = n == 1 ? "Forget the 1 circle on this PC? It can’t be undone." : $"Forget all {n} circles on this PC? It can’t be undone.";
            // "Sure?" takes the place of the search box and filters while it's asked.
            _confirm.Visibility = Visibility.Visible;
            _field.Visibility = _filters.Visibility = Visibility.Collapsed;
        }

        void HideConfirm()
        {
            _confirm.Visibility = Visibility.Collapsed;
            _field.Visibility = _filters.Visibility = Visibility.Visible;
        }

        public string Describe() =>
            PrivacyState.Presenting ? "open hidden"
            : $"open filter={_group} search=\"{_input.Text}\" rows={_shown} status=\"{Status}\" confirm={_confirm.Visibility == Visibility.Visible}";
    }
}
