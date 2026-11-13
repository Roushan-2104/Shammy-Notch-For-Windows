using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Infrastructure;
using Shammy.Modules;
using Shammy.Views.Cards;

namespace Shammy.Views;

/// <summary>
/// The open island (layout "Now + moment row"): a top bar of the pet's eyes (home), the right-now sentence, the
/// search pill and the guide's "?"; under it the home — one big now moment, three numbers and six moment-aware
/// tools (see ExpandedView.Home.cs and ExpandedView.Tools.cs) — or one view opened in place: a card's detail, the
/// Shelf, Alerts, or an older module's tab, with its title and Back. The island grows to fit (see <see cref="HeightChanged"/>).
/// </summary>
public partial class ExpandedView : UserControl
{
    /// <summary>
    /// The home is 760 × 270 (the eyes row, a 120 px moment, 66 px of tools); an opened 924 × 248 view makes the
    /// island 1012 × 326.
    /// </summary>
    public const double HomeWidth = 760, OpenWidth = Core.Notch.WallRail.MaxOpenWidth, HomeHeight = 12 + 36 + 10 + 120 + 12 + 66 + 14,
        DetailHeight = 12 + 36 + 12 + WallDetail.H + 18;

    /// <summary>The Back + title row above a detail that isn't a Wall detail.</summary>
    const double HeaderBand = 44;

    const string ShelfId = "shelf", AlertsId = "alerts", TabPrefix = "tab:";

    ShammyServices? _s;

    readonly List<NotchCard> _cards = new();
    readonly List<(string Id, string Label, FrameworkElement Panel)> _tabs = new();
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
                // Closed: the next open starts on the home again, worked out afresh.
                if (_openId is not null) GoHome(animate: false);
                _focusedId = null;
                StopMotion();
                StopHome();
                return;
            }
            Refresh();
            if (_openId is null)
            {
                _focusedId = null;
                ChoosePeek();
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
        // The guide's "?" and the search pill are plain borders: tab stops that Enter or Space opens.
        Ui.FocusKit.Focusable(GuideButton);
        Ui.FocusKit.Activates(GuideButton, () => OpenView("guide"));
        Ui.FocusKit.Focusable(SearchPill);
        Ui.FocusKit.Activates(SearchPill, () => SearchRequested?.Invoke());
        BuildHome();
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

    /// <summary>The view's height changed (home ⇄ an opened view); the notch reshapes to match.</summary>
    internal event Action? HeightChanged;

    /// <summary>The user clicked into a text box, or opened a view with one: the notch should take keyboard focus.</summary>
    internal event Action<Control>? TextInputWanted;

    /// <summary>The open notch was clicked (not into a text box): it should take the keyboard (see NotchWindow's keyboard mode).</summary>
    internal event Action? KeyboardWanted;

    /// <summary>Esc on the home: close the notch and hand the keyboard back.</summary>
    internal event Action? CloseRequested;

    /// <summary>A view opened or went home: whatever had keyboard focus may be gone.</summary>
    internal event Action? ViewChanged;

    /// <summary>The card the hover peek shows changed.</summary>
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
        // On the home the arrows walk the tools and Enter opens the moment, unless someone is typing; a focused
        // control (Tab reached a button) handles its own Enter and Space.
        if (_openId is not null || Keyboard.FocusedElement is TextBoxBase or PasswordBox) return;
        int tool = ToolIndexOf(Keyboard.FocusedElement as DependencyObject);
        bool inside = Keyboard.FocusedElement is Visual focused && focused != this && IsAncestorOf(focused);
        switch (e.Key)
        {
            case Key.Left or Key.Right when tool >= 0 || !inside:
                FocusTool(tool < 0 ? (e.Key == Key.Right ? 0 : -1) : tool + (e.Key == Key.Right ? 1 : -1));
                e.Handled = true;
                break;
            case Key.Enter or Key.Space when !inside:
                OpenHero();
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
    /// Opened by a click (the moment, a tool): the user is at the notch, so a view with a text box gets the
    /// keyboard straight away (a hover or a command never does; clicking into the box does).
    /// </summary>
    bool OpenByClick(string id)
    {
        if (!OpenView(id)) return false;
        Dispatcher.BeginInvoke(() =>
        {
            if (_openId is not null && IsVisible && FirstTextInput(DetailFrame) is { } input) TextInputWanted?.Invoke(input);
        }, DispatcherPriority.Loaded);
        return true;
    }

    internal void Attach(ShammyServices services)
    {
        _s = services;
        services.Shelf.Items.CollectionChanged += (_, _) => RefreshShelf();
        services.Notifications.History.CollectionChanged += (_, _) => Refresh();
        // A call starting or ending hides or reveals the alerts' text in place, even with the view open.
        PrivacyState.Changed += _ => Dispatcher.BeginInvoke(RebuildAlertRows);
        services.Media.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _openId is null) RefreshHome();
        });
        services.Settings.Changed += name =>
        {
            if (name is not (nameof(AppSettings.NowLineEnabled) or nameof(AppSettings.CatTheme) or "")) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible && _openId is null) RefreshHome();
            });
        };
        Theme.PaletteTheme.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _openId is null) RefreshHome(rebuildHero: true);
        });
        RefreshShelf();
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

    /// <summary>Whether <paramref name="query"/> (a view id or a title) names something that can open.</summary>
    internal bool CanOpen(string query) => Resolve(query) is not null || IsHome(query);

    static bool IsHome(string query) => string.Equals(query.Trim(), "home", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Open a view in place by id ("music", "shelf", "alerts", "tab:weather") or title ("Weather", "Alerts");
    /// "home" goes back to the home. Returns false for an unknown name.
    /// </summary>
    internal bool OpenView(string query)
    {
        if (IsHome(query))
        {
            ShowHome();
            return true;
        }
        if (Resolve(query) is not { } view) return false;

        // Leaving the Alerts view: what it listed has been seen.
        if (_openId == AlertsId && view.Id != AlertsId) _s?.Notifications.MarkAllSeen();
        _openId = view.Id;
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
        // A clip left from the previous view (opened one after another) would cut this one at the old height.
        DetailFrame.Clip = wall ? RoundedFrameClip() : null;
        if (wall) AddBack((WallDetail)content!);

        StopHome();
        HomePanel.Visibility = Visibility.Collapsed;
        DetailFrame.Visibility = Visibility.Visible;
        RefreshSentence();
        if (_openId == AlertsId) RefreshAlertsHeader();
        SetBodySize(OpenWidth, DetailHeight - WallDetail.H + DetailFrame.Height);
        if (IsVisible) AnimateIn(DetailFrame);
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
        _openId = null;
        // Let go of the detail so it (and anything it subscribed to) can be collected.
        DetailContent.Content = null;
        DetailFrame.Visibility = Visibility.Collapsed;
        DetailFrame.Clip = null;
        HomePanel.Visibility = Visibility.Visible;
        RefreshHome();
        if (animate && wasOpen && IsVisible) AnimateIn(HomePanel);
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
        // By title: a card first (a card may have taken an older tab's place), then a tab's label.
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

    // ---- Registration -------------------------------------------------------------------------

    /// <summary>Add an older module's tab: a panel opened by its id or label under a Back + title row.</summary>
    internal void AddTab(string glyph, string tooltip, FrameworkElement panel)
    {
        string id = TabPrefix + tooltip.ToLowerInvariant();
        panel.Visibility = Visibility.Collapsed;
        LegacyHost.Children.Add(panel);
        _tabs.Add((id, tooltip, panel));
    }

    /// <summary>The cards registered so far, by Order.</summary>
    internal IReadOnlyList<NotchCard> Cards => _cards.OrderBy(c => c.Order).ToList();

    /// <summary>The registered card with this id (the last one registered wins), or null.</summary>
    NotchCard? CardById(string id) => _cards.LastOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Registers a card (a card with the same id replaces the earlier one).</summary>
    internal void AddCard(NotchCard card)
    {
        if (card.Id == "guide") GuideButton.Visibility = Visibility.Visible;
        foreach (var old in _cards.Where(c => c.Id == card.Id).ToList())
        {
            CardKit.Detach(old.Compact);
            _cards.Remove(old);
        }
        _cards.Add(card);
        CardsChanged();
    }

    /// <summary>A card's relevance may have changed: the peek's choice and the home follow.</summary>
    internal void CardsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(CardsChanged);
            return;
        }
        if (_openId is not null) return;
        ChoosePeek();
        if (IsVisible) RefreshHome();
    }

    /// <summary>One line for the test pipe: the home's moment and tools, the peek's card and the open view.</summary>
    internal string DescribeV2() =>
        $"home=[{string.Join(",", ComputeRail())}] hero={_heroKind} tools=[{string.Join(",", _toolIds)}] " +
        $"focus={FocusedCard?.Id ?? "-"} detail={_openId ?? "-"} cards=[{string.Join(",", Cards.Select(c => c.Id))}] height={Height}";

    // ---- Refresh --------------------------------------------------------------------------------

    internal void Refresh()
    {
        if (_s is null || !IsInitialized) return;
        if (_openId is null) RefreshHome();
        else RefreshSentence();
        if (_openId == AlertsId) RefreshAlertsHeader();
    }
}
