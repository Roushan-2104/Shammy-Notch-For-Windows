using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The open notch as a Focus Wall (Wall.dc.html): a top row of the pet's eyes (home), tracked chips and a search
/// field, and under it either the rail of artwork tiles (see ExpandedView.Rail.cs) or one view opened in place —
/// a card's detail morphing out of its tile, the Shelf, Alerts, or an older module's tab. The notch grows to fit
/// (see <see cref="HeightChanged"/>).
/// </summary>
public partial class ExpandedView : UserControl
{
    /// <summary>The open notch: 1012 wide; the rail makes it 278 tall, a 248-tall view 326.</summary>
    public const double OpenWidth = WallRail.MaxOpenWidth, HomeHeight = 12 + 36 + 6 + RailHeight + 14, EmptyHeight = 12 + 36 + 64 + 14,
        DetailHeight = 12 + 36 + 12 + WallDetail.H + 18;

    /// <summary>Room for a focused tile (140 x 1.15 = 161) and its glow.</summary>
    const double RailHeight = 176;

    /// <summary>The Back + title row above a detail that isn't a Wall detail.</summary>
    const double HeaderBand = 44;

    const string ShelfId = "shelf", AlertsId = "alerts", TabPrefix = "tab:";

    ShammyServices? _s;

    readonly List<NotchCard> _cards = new();
    readonly List<(string Id, string Label, FrameworkElement Panel)> _tabs = new();
    readonly Dictionary<string, RadioButton> _chipButtons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<UIElement, ChipSpec> _chipIds = new();
    RadioButton? _moreChip;
    IReadOnlyList<ChipSpec> _chips = Array.Empty<ChipSpec>();
    TrackedText? _shelfCount;
    string? _openId;
    int _alertsNewAtOpen;

    public ExpandedView()
    {
        InitializeComponent();
        _nowPlaying = BuildNowPlaying();

        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                // Closed: the next open starts on the rail, focused on whatever is most live then.
                if (_openId is not null) GoHome(animate: false);
                _focusedId = null;
                StopMotion();
                _nowTimer?.Stop();
                return;
            }
            RefreshHomeTop();
            // Opening (by hover or by a command): no chip is lit until the pointer actually moves over it.
            ClearTabHover();
            DisarmHover();
            Refresh();
            // Each open starts on the most live tile (relevance and liveness changed while it was closed).
            if (_openId is null)
            {
                _focusedId = null;
                RebuildRail(animate: false);
            }
        };
        PreviewKeyDown += OnKey;
        // The hover-opened notch never takes focus, so a click into a text box (Notes' add box, the clipboard
        // search) has to ask for it, or the typing goes to the app underneath.
        // Any other click hands the notch the keyboard (Tab, arrows, Enter, Space, Esc) until Esc or a click elsewhere.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (TextInputAt(e.OriginalSource as DependencyObject) is { } input) TextInputWanted?.Invoke(input);
            else KeyboardWanted?.Invoke();
        };
        // The guide's "?" is a plain border: a tab stop that Enter or Space opens.
        Ui.FocusKit.Focusable(GuideButton);
        Ui.FocusKit.Activates(GuideButton, () => OpenView("guide"));
        RebuildChips();
        RebuildRail(animate: false);
    }

    /// <summary>Raised while the pointer has to leave the notch for something it started (a file dragged off the shelf,
    /// a menu), so the notch stays open.</summary>
    internal event Action<bool>? ShelfDragging;

    /// <summary>A notification in the Alerts list was clicked.</summary>
    internal event Action<Notifications.ShammyNotification>? NotificationOpened;

    /// <summary>"Remind me in 1 hour" on an alert's right-click menu.</summary>
    internal event Action<Notifications.ShammyNotification>? NotificationRemindRequested;

    /// <summary>The search field was clicked: open the typing palette.</summary>
    internal event Action? SearchRequested;

    /// <summary>The view's height changed (rail ⇄ an opened view); the notch reshapes to match.</summary>
    internal event Action? HeightChanged;

    /// <summary>The user clicked into a text box, or opened a view with one: the notch should take keyboard focus.</summary>
    internal event Action<Control>? TextInputWanted;

    /// <summary>The open notch was clicked (not into a text box): it should take the keyboard (see NotchWindow's keyboard mode).</summary>
    internal event Action? KeyboardWanted;

    /// <summary>Esc on the home rail: close the notch and hand the keyboard back.</summary>
    internal event Action? CloseRequested;

    /// <summary>A view opened or went home: whatever had keyboard focus may be gone.</summary>
    internal event Action? ViewChanged;

    /// <summary>The focused tile changed (the hover peek shows it).</summary>
    internal event Action? FocusChanged;

    /// <summary>Extra actions on a shelf file's right-click menu (registered by modules).</summary>
    internal List<ShelfAction> ShelfActions { get; set; } = new();

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_openId is not null) GoHome(animate: true);
            else CloseRequested?.Invoke();
            e.Handled = true;
            return;
        }
        // Arrows and Enter drive the rail, unless someone is typing or a control in the view has the keyboard
        // (Tab reached a button or a slider: Enter presses it, the arrows move it).
        if (_openId is not null || Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        if (Keyboard.FocusedElement is Visual focused && focused != this && IsAncestorOf(focused)) return;
        switch (e.Key)
        {
            case Key.Left:
                MoveFocus(-1);
                e.Handled = true;
                break;
            case Key.Right:
                MoveFocus(1);
                e.Handled = true;
                break;
            case Key.Enter or Key.Space when FocusedCard is { } card && card != _nowPlaying:
                OpenByClick(card.Id);
                e.Handled = true;
                break;
        }
    }

    // ---- Text input -----------------------------------------------------------------------------

    /// <summary>The text box (or password box) <paramref name="d"/> is in, if any.</summary>
    Control? TextInputAt(DependencyObject? d)
    {
        for (; d is not null && d != this; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is TextBoxBase or PasswordBox) return IsTypable((Control)d) ? (Control)d : null;
        return null;
    }

    static bool IsTypable(Control c) => c.IsVisible && c.IsEnabled && c.Focusable && c is not TextBoxBase { IsReadOnly: true };

    /// <summary>The first text box in the open view, in tree order.</summary>
    Control? FirstTextInput(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBoxBase or PasswordBox && IsTypable((Control)child)) return (Control)child;
            if (FirstTextInput(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Test hook: ask for the keyboard for the open view's first text box, as a click into it would.</summary>
    internal bool RequestFirstTextInput()
    {
        if (_openId is null || FirstTextInput(DetailFrame) is not { } input) return false;
        TextInputWanted?.Invoke(input);
        return true;
    }

    /// <summary>
    /// Opened by a click on its chip or tile: the user is at the notch, so a view with a text box gets the
    /// keyboard straight away (a hover or a command never does; clicking into the box does).
    /// </summary>
    void OpenByClick(string id)
    {
        if (!OpenView(id)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_openId is not null && IsVisible && FirstTextInput(DetailFrame) is { } input) TextInputWanted?.Invoke(input);
        }, DispatcherPriority.Loaded);
    }

    internal void Attach(ShammyServices services)
    {
        _s = services;
        services.Shelf.Items.CollectionChanged += (_, _) => RefreshShelf();
        services.Notifications.History.CollectionChanged += (_, _) => Refresh();
        // A call starting or ending hides or reveals the alerts' text in place, even with the view open.
        PrivacyState.Changed += _ => Dispatcher.BeginInvoke(RebuildAlertRows);
        services.Media.Changed += () => Dispatcher.BeginInvoke(RefreshNowPlaying);
        services.Settings.Changed += name =>
        {
            if (name is not (nameof(Infrastructure.AppSettings.DockEnabled) or nameof(Infrastructure.AppSettings.NowLineEnabled)
                or nameof(Infrastructure.AppSettings.CatTheme) or "")) return;
            Dispatcher.BeginInvoke(() =>
            {
                _dockKey = "";
                RefreshHomeTop();
                RebuildRail(animate: false);
            });
        };
        RefreshShelf();
        RefreshNowPlaying();
    }

    // ---- Opening views ------------------------------------------------------------------------

    internal void ShowShelf() => OpenView(ShelfId);
    internal void ShowAlerts() => OpenView(AlertsId);
    internal void ShowHome() => GoHome(animate: IsVisible);

    /// <summary>
    /// An older module's tab, opened by the module (ModuleContext.OpenTab). Once the module has registered a
    /// card with the tab's title, the card has taken the tab's place, so its detail opens instead.
    /// </summary>
    internal void ShowTab(FrameworkElement panel)
    {
        foreach (var (id, label, p) in _tabs)
        {
            if (p != panel) continue;
            var card = _cards.FirstOrDefault(c => string.Equals(c.Title, label, StringComparison.OrdinalIgnoreCase));
            OpenView(card?.Id ?? id);
            return;
        }
    }

    /// <summary>Whether <paramref name="query"/> (a view id or a chip label) names something that can open.</summary>
    internal bool CanOpen(string query) => Resolve(query) is not null || IsHome(query);

    static bool IsHome(string query) => string.Equals(query.Trim(), "home", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open a view in place by id ("music", "shelf", "alerts", "tab:weather") or chip label ("Weather",
    /// "Alerts"); "home" goes back to the rail. Returns false for an unknown name.
    /// </summary>
    internal bool OpenView(string query)
    {
        if (IsHome(query))
        {
            ShowHome();
            return true;
        }
        if (Resolve(query) is not { } view) return false;

        // Where the tile is now, for the morph (only when the rail is what's showing).
        Rect? from = _openId is null && IsVisible && view.Card is { } c ? TileRectInDetail(c) : null;

        // Leaving the Alerts view: what it listed has been seen.
        if (_openId == AlertsId && view.Id != AlertsId) _s?.Notifications.MarkAllSeen();
        _openId = view.Id;
        ClearTabHover();
        DetailContent.Content = null;
        LegacyHost.Visibility = Visibility.Collapsed;
        foreach (UIElement child in LegacyHost.Children) child.Visibility = Visibility.Collapsed;

        FrameworkElement? content = null;
        bool ownsHeader = false;
        switch (view.Kind)
        {
            case ViewKind.Card:
                try
                {
                    content = view.Card!.Detail();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Building the {view.Card!.Id} card's detail failed", ex);
                }
                ownsHeader = view.Card!.OwnsHeader;
                // The detail opened from the rail keeps its tile focused for when Back returns.
                if (RailIndex(view.Card.Id) >= 0) _focusedId = view.Card.Id;
                break;
            case ViewKind.Tab:
                view.Panel!.Visibility = Visibility.Visible;
                LegacyHost.Visibility = Visibility.Visible;
                break;
            case ViewKind.Shelf:
                content = BuildShelfDetail();
                break;
            case ViewKind.Alerts:
                // "N new" counts what arrived since the view was last open; from now on those count as seen.
                _alertsNewAtOpen = _s?.Notifications.UnreadCount ?? 0;
                content = BuildAlertsDetail();
                _s?.Notifications.MarkAllSeen();
                break;
        }

        bool wall = content is WallDetail;
        DetailContent.Content = content;
        DetailLine.Visibility = wall ? Visibility.Visible : Visibility.Collapsed;
        bool header = !wall && !ownsHeader;
        DetailHeader.Visibility = header ? Visibility.Visible : Visibility.Collapsed;
        DetailTitle.Text = view.Title;
        double contentHeight = content is { Height: > 0 and var h } && !double.IsNaN(h) ? Math.Max(h, WallDetail.H - (header ? HeaderBand : 0)) : WallDetail.H - (header ? HeaderBand : 0);
        DetailContent.Margin = new Thickness(0, header ? HeaderBand : 0, 0, 0);
        DetailContent.Width = WallDetail.W;
        DetailFrame.Height = contentHeight + (header ? HeaderBand : 0);
        // A clip left from the previous view (opened chip to chip) would cut this one at the old height.
        DetailFrame.Clip = wall ? RoundedFrameClip() : null;
        if (wall) AddBack((WallDetail)content!);

        Rail.Visibility = EmptyText.Visibility = Visibility.Collapsed;
        RefreshHomeTop();
        DetailFrame.Visibility = Visibility.Visible;
        SyncChips();
        Refresh();
        SetBodySize(OpenWidth, DetailHeight - WallDetail.H + DetailFrame.Height);
        if (IsVisible)
        {
            if (wall && from is { } rect) Morph((WallDetail)content!, rect);
            else AnimateIn(DetailFrame);
        }
        ViewChanged?.Invoke();
        return true;
    }

    /// <summary>Back on a Wall detail's art panel, top-left, over the artwork.</summary>
    void AddBack(WallDetail detail)
    {
        var back = CardKit.BackButton(() => GoHome(animate: true));
        back.ToolTip = "Back (Esc)";
        CardKit.At(back, HorizontalAlignment.Left, VerticalAlignment.Top, 12, 12);
        Panel.SetZIndex(back, 10);
        detail.Art.Children.Add(back);
    }

    void GoHome(bool animate)
    {
        bool wasOpen = _openId is not null;
        if (_openId == AlertsId) _s?.Notifications.MarkAllSeen();
        string? returning = _openId;
        _openId = null;
        // Let go of the detail so it (and anything it subscribed to) can be collected.
        DetailContent.Content = null;
        DetailFrame.Visibility = Visibility.Collapsed;
        DetailFrame.Clip = null;
        Rail.Visibility = Visibility.Visible;
        RefreshHomeTop();
        SyncChips();
        DisarmHover();
        RebuildRail(animate: false);
        // Back on the rail with the tile that was open focused; it settles into place.
        if (returning is not null && RailIndex(returning) is int i and >= 0)
        {
            SetFocus(i, animate: false);
            if (animate && wasOpen) Settle(i);
        }
        if (wasOpen) ViewChanged?.Invoke();
    }

    enum ViewKind { Card, Tab, Shelf, Alerts }

    sealed record View(string Id, string Title, ViewKind Kind, NotchCard? Card = null, FrameworkElement? Panel = null);

    View? Resolve(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        bool Is(string s) => string.Equals(s, query, StringComparison.OrdinalIgnoreCase);

        foreach (var c in _cards)
            if (Is(c.Id)) return CardView(c);
        if (Is(ShelfId)) return new View(ShelfId, "Shelf", ViewKind.Shelf);
        if (Is(AlertsId) || Is("notifications")) return new View(AlertsId, "Alerts", ViewKind.Alerts);
        foreach (var (id, label, panel) in _tabs)
            if (Is(id)) return new View(id, label, ViewKind.Tab, Panel: panel);
        // By label: the chip's label first (a card may have taken a tab's chip), then any card's title.
        foreach (var chip in _chips)
            if (Is(chip.Label)) return Resolve(chip.ViewId);
        foreach (var c in _cards)
            if (Is(c.Title)) return CardView(c);
        foreach (var (id, label, panel) in _tabs)
            if (Is(label)) return new View(id, label, ViewKind.Tab, Panel: panel);
        return null;
    }

    static View CardView(NotchCard c) => new(c.Id, c.Title, ViewKind.Card, Card: c);

    void SetBodySize(double width, double height)
    {
        if (Width == width && Height == height) return;
        Width = width;
        Height = height;
        HeightChanged?.Invoke();
    }

    void OnHomeClicked(object sender, RoutedEventArgs e) => ShowHome();
    void OnBackClicked(object sender, RoutedEventArgs e) => ShowHome();
    void OnSearchClicked(object sender, MouseButtonEventArgs e) => SearchRequested?.Invoke();

    void OnGuideClicked(object sender, MouseButtonEventArgs e) => OpenView("guide");

    // ---- Chips --------------------------------------------------------------------------------

    void ClearTabHover()
    {
        foreach (var chip in _chipButtons.Values) TabHover.Clear(chip);
        if (_moreChip is not null) TabHover.Clear(_moreChip);
    }

    /// <summary>Chips for the built-in views, older tabs and cards that asked for one (see <see cref="NotchChips"/>).</summary>
    void RebuildChips()
    {
        var legacy = new List<LegacyView> { new("Shelf", ShelfId), new("Alerts", AlertsId) };
        legacy.AddRange(_tabs.Select(t => new LegacyView(t.Label, t.Id)));
        var chips = NotchChips.Build(legacy, _cards.Select(c => new ChipCard(c.Id, c.Title, c.Chip)));
        if (chips.SequenceEqual(_chips)) return;
        _chips = chips;

        ChipStrip.Children.Clear();
        _chipButtons.Clear();
        _chipIds.Clear();
        _shelfCount = null;
        foreach (var spec in chips)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("NotchChip"),
                GroupName = "WallChips",
                Content = ChipContent(spec),
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, spec.Label);
            string id = spec.ViewId;
            chip.Click += (_, _) => OpenByClick(id);
            TabHover.Track(chip);
            ChipStrip.Children.Add(chip);
            _chipButtons[id] = chip;
            _chipIds[chip] = spec;
        }
        ChipStrip.Children.Add(MoreChip);
        ChipStrip.More = MoreChip;
        SyncChips();
        RefreshShelf();
    }

    /// <summary>The chip's tracked label; the Shelf's adds how many files it holds, in the quiet grey.</summary>
    object ChipContent(ChipSpec spec)
    {
        var label = ChipLabel(spec.Label);
        if (spec.ViewId != ShelfId) return label;
        _shelfCount = ChipLabel("");
        _shelfCount.Foreground = CardKit.Ink2;
        _shelfCount.Margin = new Thickness(5, 0, 0, 0);
        _shelfCount.Visibility = Visibility.Collapsed;
        return CardKit.Row(0, label, _shelfCount);
    }

    static TrackedText ChipLabel(string text)
    {
        var t = CardKit.Label(text, null, 11);
        t.FontWeight = FontWeights.SemiBold;
        t.FontFamily = CardKit.Face(FontWeights.SemiBold);
        t.Tracking = 0.1;
        // Follow the chip's foreground (grey, white when hovered or open).
        t.SetBinding(TrackedText.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground))
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(RadioButton), 1),
        });
        return t;
    }

    /// <summary>The overflow chip: lists the chips that don't fit in the row (ChipPanel decides which).</summary>
    RadioButton MoreChip => _moreChip ??= CreateMoreChip();

    RadioButton CreateMoreChip()
    {
        var more = new RadioButton { Style = (Style)FindResource("NotchChip"), GroupName = "WallMore", Content = ChipLabel("More"), ToolTip = "More views" };
        System.Windows.Automation.AutomationProperties.SetName(more, "More views");
        more.Click += (_, _) => ShowMoreMenu(more);
        TabHover.Track(more);
        return more;
    }

    void ShowMoreMenu(RadioButton more)
    {
        SyncChips();
        var menu = new ContextMenu { PlacementTarget = more, Placement = PlacementMode.Bottom };
        foreach (var chip in ChipStrip.Hidden)
        {
            if (!_chipIds.TryGetValue(chip, out var spec)) continue;
            var item = new MenuItem { Header = spec.Label, IsChecked = string.Equals(spec.ViewId, _openId, StringComparison.OrdinalIgnoreCase) };
            string id = spec.ViewId;
            item.Click += (_, _) => OpenByClick(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        // Keep the notch open while the menu is up (the pointer leaves the notch to reach it).
        ShelfDragging?.Invoke(true);
        menu.Closed += (_, _) => ShelfDragging?.Invoke(false);
        menu.IsOpen = true;
    }

    /// <summary>The chips that didn't fit the row, for the test pipe.</summary>
    internal IEnumerable<string> HiddenChips => ChipStrip.Hidden.Select(c => _chipIds.TryGetValue(c, out var s) ? s.Label : "?");

    /// <summary>Light the chip of the open view (none on the rail or for a card without a chip).</summary>
    void SyncChips()
    {
        foreach (var (id, chip) in _chipButtons)
            chip.IsChecked = _openId is not null && string.Equals(id, _openId, StringComparison.OrdinalIgnoreCase);
        // "More" lights when the open view's own chip is one of those tucked under it.
        MoreChip.IsChecked = _openId is not null && ChipStrip.Hidden.Any(c => _chipIds.TryGetValue(c, out var s)
            && string.Equals(s.ViewId, _openId, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Registration -------------------------------------------------------------------------

    /// <summary>Add an older module's tab: a chip that opens its panel under a Back + title row.</summary>
    internal void AddTab(string glyph, string tooltip, FrameworkElement panel)
    {
        string id = TabPrefix + tooltip.ToLowerInvariant();
        panel.Visibility = Visibility.Collapsed;
        LegacyHost.Children.Add(panel);
        _tabs.Add((id, tooltip, panel));
        RebuildChips();
    }

    /// <summary>The cards registered so far, in rail order.</summary>
    internal IReadOnlyList<NotchCard> Cards => _cards.OrderBy(c => c.Order).ToList();

    /// <summary>Registers a card (a card with the same id replaces the earlier one).</summary>
    internal void AddCard(NotchCard card)
    {
        if (card.Id == "guide") GuideButton.Visibility = Visibility.Visible;
        foreach (var old in _cards.Where(c => c.Id == card.Id).ToList())
        {
            ForgetSlot(old);
            _cards.Remove(old);
        }
        _cards.Add(card);
        CardsChanged();
    }

    /// <summary>A card's relevance may have changed: re-lay the rail (and the chips).</summary>
    internal void CardsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(CardsChanged);
            return;
        }
        RebuildChips();
        if (_openId is null) RebuildRail(animate: IsVisible);
    }

    /// <summary>One line for the test pipe: chips, the rail's tiles, the focused one and the open view.</summary>
    internal string DescribeV2() =>
        $"chips=[{string.Join(",", _chips.Select(c => c.Label))}] more=[{string.Join(",", HiddenChips)}] home=[{string.Join(",", ComputeRail())}] " +
        $"focus={FocusedCard?.Id ?? "-"} detail={_openId ?? "-"} cards=[{string.Join(",", Cards.Select(c => c.Id))}] height={Height}";

    // ---- Refresh --------------------------------------------------------------------------------

    internal void Refresh()
    {
        if (_s is null || !IsInitialized) return;
        if (_openId is null) RebuildRail(animate: IsVisible);
        if (_openId == AlertsId) RefreshAlertsHeader();
        RefreshNowPlaying();
    }
}
