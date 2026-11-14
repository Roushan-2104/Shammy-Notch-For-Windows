using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Core.Activities;
using Shammy.Core.Music;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Interop;
using Shammy.Modules;
using Shammy.Motion;
using Shammy.Notifications;
using Shammy.Views;

namespace Shammy.Shell;

public enum NotchMode { Hidden, Idle, Compact, Notification, Card, Expanded, Drop, Listening, Circling, Thinking, Typing, Answer, Result, Peek }

/// <summary>
/// The notch, drawn as a floating island. One fixed-width transparent window pinned to the top-centre of its
/// monitor; the island floats a few pixels below the top edge (the gap is part of the window, so the pointer at
/// the very top still reaches it) and morphs between states with springs, and each state's content fades and
/// scales in on top, clipped to the shape, so content is revealed by the shape rather than resized with it.
/// The window only grows taller as needed and never moves, so there's nothing to jitter.
/// </summary>
public partial class NotchWindow : Window
{
    // Wide enough for the open notch (1012) plus its hit margin and shadow on both sides.
    public const double WindowWidth = 1100;
    const double Cx = WindowWidth / 2;

    readonly ShammyServices _services;
    readonly SpringValue _w = new(186), _h = new(IslandGeometry.ClosedHeight), _r = new(IslandGeometry.ClosedHeight / 2), _shadow = new(0, 0.002);
    readonly SpringValue _bubbleOpacity = new(0, 0.002), _bubbleScale = new(0.4, 0.001);
    readonly List<Layer> _layers = new();
    readonly Layer _compactA, _compactB, _expanded, _drop, _searchBar, _answer, _result, _notification, _card;
    (double W, double H) _cardSize;
    bool _cardOpen, _cardInteractive, _cardBreaksFullscreen;
    readonly DispatcherTimer _hoverIn, _hoverOut, _dragOut;
    Layer _compactActive;
    IntPtr _hwnd;

    NotchMode _mode = NotchMode.Idle;
    (double W, double H, double R) _spec = (186, IslandGeometry.ClosedHeight, IslandGeometry.ClosedHeight / 2);
    double _compactWidth = 290;

    /// <summary>Height of the palette rows under the typing bar; the bar grows down by this much.</summary>
    double _paletteHeight;
    double _lastW = -1, _lastH = -1, _lastR = -1;

    Activity? _activity, _secondary;
    NotchMode? _searchMode;
    string? _heldActionId;
    bool _hoverExpanded, _dragging, _fullscreen, _interactive, _held, _hoveringNotification, _visitedWhileHeld;
    /// <summary>A view a command asked to open, waiting for its confirmation card (or the search bar) to close.</summary>
    Action? _pendingOpen;

    /// <summary>Other work waiting for the search bar or a command's confirmation card to close (see <see cref="AfterSearch"/>).</summary>
    readonly List<Action> _afterSearch = new();
    Color? _moduleGlow;
    string? _flashedId;
    object? _flashedPayload;
    readonly DispatcherTimer _holdTimeout;
    int _holdOpen;

    // Typing into a text box in the open notch (see BeginDetailTyping): the window was made interactive for it,
    // and gives the foreground back to the window that had it afterwards.
    bool _detailTyping;
    IntPtr _typingReturnTo;

    // v2 resting notch: eyes + rotating glance when idle, the peek on a resting pointer (see HoverIntent).
    readonly Layer _rest, _peek;
    readonly RestView _restView;
    readonly PeekView _peekView;
    readonly GlanceFeed _glances;
    readonly HoverIntent _intent = new();
    readonly WiggleDetector _wiggle = new();
    readonly System.Diagnostics.Stopwatch _hoverClock = System.Diagnostics.Stopwatch.StartNew();
    bool _peeking, _wasPlaying;
    string _lastTrack = "";
    (Point At, TimeSpan T)? _press;

    // The pet wears every activity (see ActivityView): the compact notch keeps the pet in the middle in costume,
    // with the action circle and the second activity's dot beside it (ActivityChrome).
    readonly Layer _petCompact;
    readonly ActivityView _activityView;
    readonly ActivityChrome _chrome;
    readonly DispatcherTimer _petTick;
    IReadOnlyList<Activity> _ranked = Array.Empty<Activity>();
    PetLive? _petOwner, _petDot;
    string? _petPreferred;
    bool _petShowing;

    internal NotchWindow(ShammyServices services)
    {
        _services = services;
        InitializeComponent();
        Shape.CenterX = ShadowShape.CenterX = Edge.CenterX = Cx;
        Edge.Enabled = services.Settings.ShammyGlow;
        // Before anything borrows the island's fill (the action circle beside the pet).
        InitIsland();
        // The palette (its first colour is the accent); a change from Settings or the pipe applies at once.
        Theme.PaletteTheme.Choose(services.Settings.Palette);
        services.Settings.Changed += name =>
        {
            if (name != nameof(AppSettings.Palette)) return;
            if (Dispatcher.CheckAccess()) Theme.PaletteTheme.Choose(services.Settings.Palette);
            else Dispatcher.BeginInvoke(() => Theme.PaletteTheme.Choose(services.Settings.Palette));
        };

        _compactA = AddLayer(CompactA);
        _compactB = AddLayer(CompactB);
        _expanded = AddLayer(Expanded);
        _drop = AddLayer(DropZone);
        _searchBar = AddLayer(SearchBar);
        SearchBar.PaletteHeightChanged += h =>
        {
            _paletteHeight = h;
            if (_mode == NotchMode.Typing) Transition(NotchMode.Typing);
        };
        _answer = AddLayer(Answer);
        _result = AddLayer(Result);
        _notification = AddLayer(NotificationCard);
        _card = AddLayer(CardHost);
        _compactActive = _compactA;

        _glances = new GlanceFeed(services);
        _restView = new RestView(services, _glances);
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        _peekView = new PeekView(services, _glances);
        ContentHost.Children.Insert(0, _peekView);
        ContentHost.Children.Insert(0, _restView);
        if (!services.Settings.PetEnabled) _restView.SetPetEnabled(false);
        _rest = AddLayer(_restView);
        _peek = AddLayer(_peekView);
        _peekView.GlanceClicked += OnPeekGlanceClicked;

        _activityView = new ActivityView(services);
        ContentHost.Children.Insert(0, _activityView);
        _petCompact = AddLayer(_activityView);
        _chrome = new ActivityChrome(Root, Shape, Shape.Fill);
        _chrome.CircleClicked += RunCircle;
        _chrome.DotClicked += id =>
        {
            _petPreferred = id;
            Recompute();
        };
        // Live values (a call's timer, a countdown, a download) refresh once a second while the pet wears them.
        _petTick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshPet(), Dispatcher) { IsEnabled = false };

        // While the pointer is on the notch, sample it often enough to tell resting from passing through.
        _hoverIn = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, OnHoverTick, Dispatcher) { IsEnabled = false };
        _hoverOut = new DispatcherTimer(TimeSpan.FromMilliseconds(320), DispatcherPriority.Input, OnHoverOut, Dispatcher) { IsEnabled = false };
        _dragOut = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Input, OnDragOut, Dispatcher) { IsEnabled = false };
        _holdTimeout = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Input, (timer, _) =>
        {
            if (!_visitedWhileHeld && !Root.IsMouseOver) ReleaseHold();
            else ((DispatcherTimer)timer!).Stop();
        }, Dispatcher) { IsEnabled = false };

        Expanded.Attach(services);
        // Opening a view in place makes the open notch taller (and Back shorter again).
        MenusAboveNotch.Install();
        Expanded.HeightChanged += () =>
        {
            if (_mode == NotchMode.Expanded) Transition(NotchMode.Expanded);
        };
        Expanded.TextInputWanted += BeginDetailTyping;
        // One click on the open notch hands it the keyboard; Esc at home closes it and hands the keyboard back.
        Expanded.KeyboardWanted += BeginKeyboard;
        Expanded.CloseRequested += CloseFromKeyboard;
        Expanded.IsVisibleChanged += (_, _) =>
        {
            if (!Expanded.IsVisible) EndKeyboard(giveBack: true);
        };
        // Leaving the view (Back, Esc, another chip) drops the text box; once focus has settled, see if typing is over.
        Expanded.ViewChanged += () => Dispatcher.BeginInvoke(CheckDetailTyping, DispatcherPriority.Background);
        Expanded.ShelfDragging += dragging =>
        {
            _holdOpen += dragging ? 1 : -1;
            if (!dragging && !Root.IsMouseOver) _hoverOut.Start();
        };
        Result.CompactLayout = services.Settings.CompactResults;

        Root.MouseEnter += OnRootMouseEnter;
        Root.MouseLeave += OnRootMouseLeave;
        Root.DragEnter += OnDragEnter;
        Root.DragOver += OnDragEnter;
        Root.DragLeave += (_, _) => _dragOut.Start();
        Root.Drop += OnDrop;
        Bubble.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _secondary?.Click?.Invoke();
        };
        // The tab under the notch: a click tucks the notch away instead of opening it.
        foreach (var tab in new UIElement[] { TuckTab, TuckHit })
        {
            tab.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                TuckAway();
            };
            tab.MouseLeftButtonDown += (_, e) => e.Handled = true;
        }
        // Pressing on the notch means a click (or a drag-swipe) is coming; don't let a pending hover expand it away first.
        Root.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _hoverIn.Stop();
            _press = _mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Peek ? (e.GetPosition(Root), _hoverClock.Elapsed) : null;
        };
        Root.PreviewMouseLeftButtonUp += OnPressReleased;
        Root.MouseMove += (_, e) =>
        {
            if (_hoverIn.IsEnabled) SampleHover();
            // Petting: a little wiggle over the resting pet makes it happy.
            if (_mode == NotchMode.Idle && _restView.PetEnabled && _wiggle.Add(e.GetPosition(Root).X, _hoverClock.Elapsed))
            {
                _restView.Mood.Petted();
                _intent.Reset();
            }
        };
        // The pill has already run its click (it handles the event first); a done action then goes away.
        // Clicking the resting notch or the peek opens the notch straight away.
        Root.MouseLeftButtonUp += (_, _) =>
        {
            if (_mode == NotchMode.Compact && _petShowing) PetBodyClicked();
            else if (_mode == NotchMode.Compact && IsAction(_activity)) ActionClicked?.Invoke(_activity!.Id);
            else if (_mode is NotchMode.Idle or NotchMode.Peek || (_mode == NotchMode.Compact && _activity is { Click: null })) OpenFromHover();
        };
        // Moving from the side bubble (or the action circle) onto the pill (both inside Root) raises no new MouseEnter.
        Bubble.MouseLeave += (_, _) =>
        {
            if (Root.IsMouseOver && !_hoverExpanded) StartHoverIn();
        };
        _chrome.PointerLeft += () =>
        {
            if (Root.IsMouseOver && !_hoverExpanded && !_chrome.IsMouseOver) StartHoverIn();
        };
        Deactivated += (_, _) =>
        {
            // The user clicked another window while typing in the notch: they're done, and that window keeps focus.
            if (_detailTyping) EndDetailTyping(giveBack: false);
            EndKeyboard(giveBack: false);
            if (_cardOpen && _cardInteractive && _searchMode is null) CloseCard();
            else if (_interactive) InteractiveDeactivated?.Invoke();
        };
        Root.MouseWheel += (_, e) =>
        {
            if (_mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Notification or NotchMode.Peek && GesturesOn)
            {
                // Shift+wheel is the usual horizontal scroll on a mouse without a tilt wheel: wheel down = right = next.
                const int VK_SHIFT = 0x10;
                if (Native.GetAsyncKeyState(VK_SHIFT) < 0) NotchHorizontalWheel?.Invoke(-e.Delta);
                else NotchWheel?.Invoke(e.Delta);
                e.Handled = true;
            }
        };

        SourceInitialized += OnSourceInitialized;
        // The window starts in the resting mode without ever having moved into it, so nothing asked the resting
        // content to show: without this the notch stayed empty after start-up until the first event.
        Loaded += (_, _) =>
        {
            if (_mode == NotchMode.Idle && !_rest.Active) Transition(NotchMode.Idle);
        };
        Animator.Frame += OnFrame;
        Reposition();
        ApplyVisuals(force: true);
        UpdateAmbience();
    }

    public event Action? InteractiveDeactivated;
    public event Action<string[]>? FilesDropped;

    /// <summary>The pointer entered/left a notification on display (so the host can pause its timer).</summary>
    public event Action<bool>? NotificationHovered;

    /// <summary>Vertical wheel over the idle/compact notch.</summary>
    public event Action<int>? NotchWheel;

    /// <summary>Horizontal wheel or two-finger swipe over the notch (positive = right).</summary>
    public event Action<int>? NotchHorizontalWheel;

    public NotchMode Mode => _mode;
    internal SearchBarView SearchBarView => SearchBar;
    internal ResultView ResultView => Result;
    internal AnswerView AnswerView => Answer;
    internal ExpandedView ExpandedView => Expanded;
    internal NotificationView NotificationView => NotificationCard;

    /// <summary>The pointer is resting on the message that is up (it is being held on screen).</summary>
    internal bool HoveringNotification => _hoveringNotification;

    // The one or two cards that peek out under a message when more wait beneath it.
    readonly SpringValue _stackA = new(0, 0.002), _stackB = new(0, 0.002);

    // Tucked away: the small tab under the notch slides it out of sight for a few seconds, so whatever an app has
    // under it can be clicked. A short line stays where it was.
    const long WS_EX_TRANSPARENT = 0x00000020;
    readonly SpringValue _tuck = new(0, 0.002), _tabShown = new(0, 0.002);
    readonly TranslateTransform _rootShift = new(), _lineShift = new();
    DispatcherTimer? _untuck, _blockCheck;
    bool _tucked, _hoverBlocked;
    int _tucks;
    int _stackDepth;
    DateTime? _greetedAt;

    sealed class Layer
    {
        public Layer(FrameworkElement el)
        {
            El = el;
            el.RenderTransformOrigin = new Point(0.5, 0);
            el.RenderTransform = Scale;
            el.Opacity = 0;
            el.Visibility = Visibility.Collapsed;
            Canvas.SetTop(el, 0);
        }

        public readonly FrameworkElement El;
        public readonly ScaleTransform Scale = new(0.94, 0.94);
        public readonly SpringValue Opacity = new(0, 0.002);
        public readonly SpringValue Size = new(0.94, 0.0005);
        /// <summary>
        /// 1 at rest; dips and comes back when the content changes in place, so it dims through the change. (It used
        /// to blur as well; a blur effect is re-rasterised on every repaint of the layered window, so it's gone.)
        /// </summary>
        public readonly SpringValue Swap = new(1, 0.002);
        public bool Active;
    }

    Layer AddLayer(FrameworkElement el)
    {
        var layer = new Layer(el);
        _layers.Add(layer);
        return layer;
    }

    void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Root.RenderTransform = _rootShift;
        TuckLine.RenderTransform = _lineShift;
        ApplyScale();
        // The primary monitor's scale changed (or the window moved to a monitor with another): WPF rescales
        // the window, and the centre has to be worked out again at the new size.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Background);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_MOUSEACTIVATE && !_interactive)
        {
            handled = true;
            return new IntPtr(Native.MA_NOACTIVATE);
        }
        const int WM_MOUSEHWHEEL = 0x020E;
        if (msg == WM_MOUSEHWHEEL && Root.IsMouseOver && GesturesOn)
        {
            NotchHorizontalWheel?.Invoke((short)((wParam.ToInt64() >> 16) & 0xFFFF));
            handled = true;
            return new IntPtr(1);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Centre on the top of the primary monitor's usable area, so the notch works with the taskbar on
    /// any side: with a top taskbar it hangs from the taskbar's bottom edge instead of covering it.
    /// Call again whenever the work area or displays change.
    /// </summary>
    public void Reposition()
    {
        UpdatePointerFollow();
        if (_hwnd != IntPtr.Zero && PlaceInPixels()) return;
        var area = SystemParameters.WorkArea;
        if (_services.Settings.PinToScreenEdge)
        {
            Left = (SystemParameters.PrimaryScreenWidth - WindowWidth * _scale) / 2;
            Top = 0;
        }
        else
        {
            Left = area.Left + (area.Width - WindowWidth * _scale) / 2;
            Top = area.Top;
        }
    }

    // ---- Inputs -------------------------------------------------------------------------------

    /// <param name="ranked">Every live activity, most important first: the pet picks its costume and dot from these.</param>
    internal void SetActivities(Activity? current, Activity? secondary, IReadOnlyList<Activity>? ranked = null)
    {
        bool wasAction = IsAction(_activity);
        bool wasNotification = _mode == NotchMode.Notification;
        _activity = current;
        _secondary = secondary;
        _ranked = ranked ?? (current is null ? Array.Empty<Activity>() : new[] { current });
        if (_petPreferred is { } preferred && !_ranked.Any(a => a.Id == preferred)) _petPreferred = null;
        Recompute();
        UpdateBubble();
        FlashForActivity();
        UpdateAmbience();

        // A notification that arrives (or is replaced by the next one) under a resting pointer is being
        // read: hold it, as MouseEnter would have. Only while it still has a countdown, so the hold's own
        // re-post doesn't loop back here.
        if (_mode == NotchMode.Notification && Root.IsMouseOver && current?.ExpiresAt is not null)
        {
            _hoveringNotification = true;
            NotificationHovered?.Invoke(true);
        }
        // The notification under the pointer went away: hovering opens the notch again.
        if (wasNotification && _mode != NotchMode.Notification && Root.IsMouseOver && !_hoverExpanded)
        {
            _hoveringNotification = false;
            StartHoverIn();
        }

        bool isAction = IsAction(current);
        // An action that arrives under the pointer (e.g. while the notch is open) waits until it's seen.
        // A different pill (or none) is up now: the one we were holding gets its countdown back.
        if (_heldActionId is { } held && (!isAction || current!.Id != held)) SetActionHold(false);
        if (isAction && Root.IsMouseOver && _heldActionId is null) SetActionHold(true);
        // The pointer stayed on the notch while an action pill was up; once it's gone, hovering works again.
        if (wasAction && !isAction && Root.IsMouseOver && !_hoverExpanded && _mode != NotchMode.Notification) StartHoverIn();
    }

    /// <summary>Raised while the pointer is on a one-shot action pill, so its countdown pauses.</summary>
    public event Action<string, bool>? ActionHovered;

    /// <summary>Raised after a one-shot action pill was clicked, with its activity id.</summary>
    public event Action<string>? ActionClicked;

    /// <summary>
    /// One-shot messages you act on by clicking ("Update ready · click to restart", "Saved · click to
    /// show"). Hovering must not expand the notch over them, or the thing to click disappears.
    /// </summary>
    static bool IsAction(Activity? a) =>
        a is { Click: not null, Kind: ActivityKind.Message or ActivityKind.DownloadComplete }
        // A finished timer the pet is jumping about (it was a message before it had a costume).
        || a is { Click: not null, Payload: Modules.Productivity.TimerDoneContent };

    /// <summary>
    /// Holds or releases by id, so a release still finds the right pill after something with a higher
    /// priority (a volume HUD, a notification) has pushed in front of it.
    /// </summary>
    void SetActionHold(bool on)
    {
        var id = on ? _activity?.Id : _heldActionId;
        _heldActionId = on ? id : null;
        if (id is not null) ActionHovered?.Invoke(id, on);
    }

    /// <summary>The answer card changed size in place (a short answer arrived): the island follows.</summary>
    internal void RefitAnswer()
    {
        if (_mode == NotchMode.Answer) Transition(NotchMode.Answer);
    }

    public void SetSearchMode(NotchMode? mode)
    {
        // The search bar takes over the keyboard (and manages SetInteractive itself).
        if (mode is not null && _detailTyping) EndDetailTyping(giveBack: false);
        if (mode is not null) EndKeyboard(giveBack: false);
        _searchMode = mode;
        _restView.Mood.SetSearch(mode);
        if (mode is not null)
        {
            _hoverExpanded = false;
            _peeking = false;
            _hoverIn.Stop();
        }
        Recompute();
        UpdateBubble();
        if (mode is null && _pendingOpen is { } pending)
        {
            _pendingOpen = null;
            pending();
        }
        if (mode is null && _afterSearch.Count > 0)
        {
            var waiting = _afterSearch.ToList();
            _afterSearch.Clear();
            foreach (var run in waiting) run();
        }
    }

    public bool IsFullscreen => _fullscreen;

    public void SetFullscreen(bool fullscreen)
    {
        if (_fullscreen == fullscreen) return;
        _fullscreen = fullscreen;
        if (fullscreen) _hoverExpanded = _peeking = false;
        Recompute();
        UpdateBubble();
    }

    /// <summary>Let the notch take keyboard focus (typing, scrolling results) or go back to never stealing it.</summary>
    public void SetInteractive(bool on)
    {
        if (_interactive == on) return;
        _interactive = on;
        if (_hwnd == IntPtr.Zero) return;
        if (on)
        {
            Native.SetExStyle(_hwnd, 0, Native.WS_EX_NOACTIVATE);
            Native.ForceForeground(_hwnd);
            Activate();
        }
        else
        {
            Native.SetExStyle(_hwnd, Native.WS_EX_NOACTIVATE);
        }
    }

    public void RefreshMedia()
    {
        ((CompactView)_compactActive.El).RefreshMedia(_services.Media);
        if (Expanded.IsVisible) Expanded.Refresh();
        RefreshPet();
        UpdateBubble();

        _restView.RefreshMedia();
        if (_mode == NotchMode.Peek) _peekView.Refresh();
        // A song starting makes Shammy happy (seen when the eyes are on screen, e.g. a song resumed from the peek).
        var media = _services.Media;
        // The Album palette follows the cover (worked out once per cover); the island's look follows the music.
        Theme.PaletteTheme.SetMusic(media.Art, media.IsPlaying);
        UpdateAmbience();
        if (media.IsPlaying && (!_wasPlaying || media.Title != _lastTrack)) _restView.Pet.Bounce();
        _wasPlaying = media.IsPlaying;
        _lastTrack = media.Title;
    }

    public void RefreshExpanded()
    {
        if (Expanded.IsVisible) Expanded.Refresh();
    }

    /// <summary>
    /// Open the expanded card on a tab, as if hovered (after a drop, a click, or a command). With
    /// <paramref name="hold"/> it stays open until the pointer has visited and left, or for 8 s if the
    /// pointer never comes over, instead of closing straight away because nobody is hovering.
    /// </summary>
    public void ExpandTo(bool shelf, bool hold = false) =>
        Open(() => { if (shelf) Expanded.ShowShelf(); else Expanded.ShowHome(); }, hold);

    /// <summary>
    /// Open a module's tab. While a search or command card is showing, the tab opens as soon as that
    /// card closes, so a command can confirm first and then show its tab.
    /// </summary>
    internal void OpenTab(FrameworkElement panel, bool hold)
    {
        if (_searchMode is not null)
        {
            _pendingOpen = () => OpenTab(panel, hold);
            return;
        }
        Open(() => Expanded.ShowTab(panel), hold);
    }

    /// <summary>
    /// Open the notch on a view by id or chip label (see ExpandedView.OpenView); false if there's no such view.
    /// Like <see cref="OpenTab"/>, a view asked for while a search or command card shows opens once that card closes.
    /// </summary>
    internal bool OpenView(string idOrLabel, bool hold = true)
    {
        if (!Expanded.CanOpen(idOrLabel)) return false;
        if (_searchMode is not null)
        {
            _pendingOpen = () => OpenView(idOrLabel, hold);
            return true;
        }
        Open(() => Expanded.OpenView(idOrLabel), hold);
        return true;
    }

    void Open(Action selectTab, bool hold)
    {
        selectTab();
        _hoverExpanded = true;
        _peeking = false;
        if (hold)
        {
            if (!_held)
            {
                _held = true;
                _holdOpen++;
            }
            _visitedWhileHeld = Root.IsMouseOver;
            _holdTimeout.Stop();
            _holdTimeout.Start();
        }
        Recompute();
        if (!Root.IsMouseOver) _hoverOut.Start();
    }

    /// <summary>
    /// Run <paramref name="action"/> once the search bar or a command's confirmation card has closed (now if none
    /// is up), e.g. a pill that should get its whole time on screen rather than start behind the confirmation.
    /// </summary>
    internal void AfterSearch(Action action)
    {
        if (_searchMode is null) action();
        else _afterSearch.Add(action);
    }

    /// <summary>Drop this window's own hold; other holds (a drag, a context menu) still keep it open.</summary>
    void ReleaseHold()
    {
        _holdTimeout.Stop();
        if (!_held) return;
        _held = false;
        _holdOpen--;
        if (!Root.IsMouseOver) _hoverOut.Start();
    }

    /// <summary>
    /// The browser engine only starts once its control is visible, so warm it up inside the result
    /// layer while that layer is clipped away by the small notch shape (nothing shows, nothing is clickable).
    /// </summary>
    internal async Task WarmUpResultsAsync()
    {
        if (_result.Active) return;
        Result.IsHitTestVisible = false;
        Result.Visibility = Visibility.Visible;
        try
        {
            await Result.EnsureReadyAsync();
        }
        finally
        {
            Result.IsHitTestVisible = true;
            if (!_result.Active) Result.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Show module content in the notch at the given size (see ModuleContext.ShowCard).</summary>
    internal void ShowCard(FrameworkElement content, double width, double height, bool interactive, bool breaksFullscreen = false)
    {
        CardHost.Content = content;
        CardHost.Width = width;
        CardHost.Height = height;
        _cardSize = (width, height);
        _cardOpen = true;
        _cardBreaksFullscreen = breaksFullscreen;
        // Over a fullscreen app the card must not take focus from it (a game would minimise or lose input);
        // its buttons still work by mouse, since clicks reach a no-activate window.
        _cardInteractive = interactive && !(breaksFullscreen && _fullscreen);
        _hoverExpanded = _peeking = false;
        Recompute();
        if (_cardInteractive && _searchMode is null) SetInteractive(true);
    }

    internal void CloseCard()
    {
        if (!_cardOpen) return;
        _cardOpen = false;
        _cardBreaksFullscreen = false;
        if (_cardInteractive && _searchMode is null) SetInteractive(false);
        _cardInteractive = false;
        Recompute();
    }

    static Color Green => Ui.Tokens.Ok.Color;
    static Color Red => Ui.Tokens.Alarm.Color;

    /// <summary>Keep the edge lit in a module's colour (e.g. a finished timer); null releases it.</summary>
    internal void SetGlow(Color? color)
    {
        _moduleGlow = color;
        // A module holds the edge lit for something you asked to be told about (a finished timer or alarm).
        _restView.Mood.SetTimerDone(color is not null);
        UpdateEdgeHold();
    }

    /// <summary>A short flash of the edge (e.g. a new song in its album colour).</summary>
    internal void FlashEdge(Color color) => Edge.Flash(color);

    public void SetShammyEnabled(bool on) => Edge.Enabled = on;

    /// <summary>
    /// A newly arrived activity flashes the edge in its status colour. Re-posts of the same one (a new
    /// expiry on hover) keep the same payload, so they don't flash again.
    /// </summary>
    void FlashForActivity()
    {
        var a = _activity;
        if (a is null || (a.Id == _flashedId && ReferenceEquals(a.Payload, _flashedPayload))) return;
        _flashedId = a.Id;
        _flashedPayload = a.Payload;
        if (a.Payload is NotificationPayload np) _restView.Mood.Notified(np.Notification.AppName, np.Notification.Title);
        // Screen readers hear what just arrived: a message ("Saved · click to show"), a notification (as the list
        // shows it, so it stays hidden while presenting) or a finished download.
        Ui.A11y.Announce(a.Payload switch
        {
            MessagePayload m when a.Kind is ActivityKind.Message or ActivityKind.Custom => m.Text,
            NotificationPayload n => $"{n.Notification.ListTitle}. {n.Notification.ListDetail}",
            DownloadPayload d when a.Kind == ActivityKind.DownloadComplete => $"Downloaded {d.Name}",
            _ => null,
        }, this, a.Id);
        Color? color = a switch
        {
            { Kind: ActivityKind.Notification } => Ui.Tokens.Accent.Color,
            { Kind: ActivityKind.Charging } => Green,
            { Kind: ActivityKind.LowBattery } => Red,
            { Payload: MessagePayload { IsError: true } } => Red,
            _ => null,
        };
        if (color is { } c) Edge.Flash(c);
    }

    /// <summary>A steady edge while listening or searching; otherwise only a module's held colour.</summary>
    void UpdateEdgeHold()
    {
        Edge.Shown = _mode != NotchMode.Hidden;
        Edge.Hold(_mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking ? Ui.Tokens.Accent.Color : _moduleGlow);
    }

    public void Collapse()
    {
        _pendingOpen = null;
        _holdTimeout.Stop();
        if (_held)
        {
            _held = false;
            _holdOpen--;
        }
        _hoverExpanded = _peeking = false;
        Recompute();
    }

}
