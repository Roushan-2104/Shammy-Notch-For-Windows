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
/// The notch. One fixed-width transparent window pinned to the top-centre of the primary monitor; the
/// black shape inside it morphs between states with springs, and each state's content fades and scales
/// in on top, clipped to the shape, so content is revealed by the shape rather than resized with it.
/// The window only grows taller as needed and never moves, so there's nothing to jitter.
/// </summary>
public partial class NotchWindow : Window
{
    // Wide enough for the open notch (1012) plus its hit margin and shadow on both sides.
    public const double WindowWidth = 1100;
    const double ShadowPad = 64;
    const double Cx = WindowWidth / 2;

    readonly ShammyServices _services;
    readonly SpringValue _w = new(186), _h = new(32), _r = new(11), _shadow = new(0, 0.002);
    readonly SpringValue _bubbleOpacity = new(0, 0.002), _bubbleScale = new(0.4, 0.001);
    readonly List<Layer> _layers = new();
    readonly Layer _compactA, _compactB, _expanded, _drop, _searchBar, _answer, _result, _notification, _card;
    (double W, double H) _cardSize;
    bool _cardOpen, _cardInteractive, _cardBreaksFullscreen;
    readonly DispatcherTimer _hoverIn, _hoverOut, _dragOut;
    Layer _compactActive;
    IntPtr _hwnd;

    NotchMode _mode = NotchMode.Idle;
    (double W, double H, double R) _spec = (186, 32, 11);
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
        Animator.Frame += OnFrame;
        Reposition();
        ApplyVisuals(force: true);
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

    // ---- Size and screen (Settings → Notch & pet) --------------------------------------------------

    double _scale = 1;
    IntPtr _monitor;
    DispatcherTimer? _pointerFollow;

    /// <summary>The screen the notch is on (the fullscreen check looks at this one).</summary>
    public IntPtr Monitor => _monitor != IntPtr.Zero ? _monitor : Native.PrimaryMonitor();

    /// <summary>Motion changed (Lively or Calm): the pet is redrawn so its record and extras start or stop at once.</summary>
    public void MotionChanged()
    {
        _activityView.Pet.Reset();
        RefreshPet();
    }

    /// <summary>Scales everything in the notch (Settings → Notch size) and re-centres it.</summary>
    public void ApplyScale()
    {
        double scale = Shammy.Core.Settings.NotchLook.Scale(_services.Settings.NotchSize);
        if (Math.Abs(scale - _scale) < 0.001 && Root.LayoutTransform is ScaleTransform) return;
        _scale = scale;
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = WindowWidth * scale;
        Height = Math.Max(Height, (_spec.H + ShadowPad) * scale);
        Reposition();
        ApplyVisuals(force: true);
    }

    /// <summary>The screen chosen in Settings: the main one, the one under the pointer, or a named one.</summary>
    IntPtr TargetMonitor()
    {
        var primary = Native.PrimaryMonitor();
        var setting = Shammy.Core.Settings.NotchDisplay.Normalize(_services.Settings.NotchDisplay);
        if (setting == Shammy.Core.Settings.NotchDisplay.Main) return primary;
        try
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var names = screens.Select(s => s.DeviceName).ToList();
            var main = screens.FirstOrDefault(s => s.Primary)?.DeviceName ?? names.FirstOrDefault() ?? "";
            string? under = Native.GetCursorPos(out var p) ? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y)).DeviceName : null;
            var pick = Shammy.Core.Settings.NotchDisplay.Pick(setting, names, main, under);
            var screen = screens.FirstOrDefault(s => s.DeviceName == pick);
            if (screen is null || screen.Primary) return primary;
            var b = screen.Bounds;
            return Native.MonitorFromPoint(new Native.POINT(b.Left + b.Width / 2, b.Top + b.Height / 2), Native.MONITOR_DEFAULTTOPRIMARY);
        }
        catch (Exception ex)
        {
            Log.Warn("Finding the notch's screen failed", ex);
            return primary;
        }
    }

    /// <summary>"Follow pointer": once a second, move to the pointer's screen while the notch is at rest.</summary>
    void UpdatePointerFollow()
    {
        bool follow = Shammy.Core.Settings.NotchDisplay.Normalize(_services.Settings.NotchDisplay) == Shammy.Core.Settings.NotchDisplay.Pointer;
        if (!follow)
        {
            _pointerFollow?.Stop();
            return;
        }
        _pointerFollow ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_mode is not (NotchMode.Idle or NotchMode.Compact or NotchMode.Hidden) || _hwnd == IntPtr.Zero) return;
            if (TargetMonitor() != _monitor) PlaceInPixels();
        }, Dispatcher);
        _pointerFollow.Start();
    }

    /// <summary>
    /// Centres the notch on the primary monitor in physical pixels at that monitor's own scale.
    /// SystemParameters (used before the window exists) are in the system DPI captured at sign-in, which is
    /// wrong once the primary monitor's scale differs from it (docking a 150% laptop to a 100% screen,
    /// changing the scale in Settings): the notch ended up hundreds of pixels off centre.
    /// </summary>
    bool PlaceInPixels()
    {
        var monitor = TargetMonitor();
        if (Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) != 0 || dpi == 0) return false;
        _monitor = monitor;
        var info = Native.MonitorInfo(monitor);
        var r = _services.Settings.PinToScreenEdge ? info.rcMonitor : info.rcWork;
        int width = (int)Math.Round(WindowWidth * _scale * dpi / 96.0);
        int x = r.Left + (r.Right - r.Left - width) / 2;
        return Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, r.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void BringToFront()
    {
        if (_hwnd != IntPtr.Zero && !MenusAboveNotch.AnyOpen) Native.BringToTopmost(_hwnd);
    }

    /// <summary>Keep the notch out of Shammy's own screen freeze (it's drawn live on top anyway).</summary>
    public void ExcludeFromCapture(bool exclude)
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowDisplayAffinity(_hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
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

    public void SetSearchMode(NotchMode? mode)
    {
        // The search bar takes over the keyboard (and manages SetInteractive itself).
        if (mode is not null && _detailTyping) EndDetailTyping(giveBack: false);
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

    static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58), Red = Color.FromRgb(0xFF, 0x45, 0x3A);

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
        Color? color = a switch
        {
            { Kind: ActivityKind.Notification } => Theme.Accent.Current,
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
        Edge.Hold(_mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking ? Theme.Accent.Current : _moduleGlow);
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

    // ---- State machine ------------------------------------------------------------------------

    void Recompute()
    {
        NotchMode mode;
        if (_searchMode is { } s) mode = s;
        // Over a fullscreen app only what you asked to be interrupted for shows: a reminder/alarm card, a
        // finished timer, or the volume HUD for the key you just pressed. Never expanded, never activated.
        else if (_fullscreen)
            mode = _cardOpen && _cardBreaksFullscreen ? NotchMode.Card
                : FullscreenPolicy.ShowsOver(_activity) ? NotchMode.Compact
                : NotchMode.Hidden;
        else if (_dragging) mode = NotchMode.Drop;
        else if (_cardOpen) mode = NotchMode.Card;
        else if (_activity?.Kind == ActivityKind.Notification && !_hoverExpanded)
            mode = NotchMode.Notification;
        else if (_hoverExpanded) mode = NotchMode.Expanded;
        else if (_peeking) mode = NotchMode.Peek;
        // Music, like every live activity the pet can wear, shows as the pet in costume (ActivityView); a paused
        // song with nothing to show rests.
        else if (_activity is not null && (_activity.Kind != ActivityKind.NowPlaying || PetFor(_activity) is not null)) mode = NotchMode.Compact;
        else mode = NotchMode.Idle;

        // Typing in the open notch ends when the notch stops being open (closed, a card or a notification took over).
        if (mode != NotchMode.Expanded && _detailTyping) EndDetailTyping(giveBack: true);
        if (mode == NotchMode.Compact) PrepareCompact();
        if (mode == NotchMode.Notification && _activity?.Payload is NotificationPayload p) NotificationCard.Show(p.Notification, p.More);
        // Breaking through: the fullscreen app may have pushed itself above us; re-assert topmost (without activating).
        if (_fullscreen && _mode == NotchMode.Hidden && mode != NotchMode.Hidden) BringToFront();
        if (mode != _mode || mode is NotchMode.Compact or NotchMode.Card) Transition(mode);
        // The side bubble belongs next to the compact pill only; hide it whenever a card, tab or drop zone opens.
        UpdateBubble();
    }

    void PrepareCompact()
    {
        var a = _activity!;
        if (PreparePet(a)) return;
        _petShowing = false;
        var current = (CompactView)_compactActive.El;
        if (current.ShownId == a.Id && current.ShownKind == a.Kind && _compactActive.Active)
        {
            _compactWidth = current.Update(a, _services);
            return;
        }
        // Different activity: build it in the spare layer and cross-fade.
        var next = _compactActive == _compactA ? _compactB : _compactA;
        if (_mode != NotchMode.Compact) next = _compactActive;
        _compactWidth = ((CompactView)next.El).Show(a, _services);
        _compactActive = next;
    }

    (double W, double H, double R) SpecFor(NotchMode mode) => mode switch
    {
        NotchMode.Hidden => (150, 0, 0),
        NotchMode.Idle => (RestView.RestWidth, RestView.RestHeight, 12),
        NotchMode.Peek => (PeekView.PeekWidth, PeekView.PeekHeight, 26),
        NotchMode.Compact when _petShowing => (ActivityView.ViewWidth, _activityView.CurrentHeight, _activityView.CurrentRadius),
        NotchMode.Compact => (_compactWidth, 32, 12),
        NotchMode.Notification => (NotificationView.CardWidth, NotificationView.CardHeight, 30),
        NotchMode.Card => (_cardSize.W, _cardSize.H, Math.Min(32, _cardSize.H / 2)),
        NotchMode.Expanded => (Expanded.Width, Expanded.Height, 36),
        NotchMode.Drop => (DropZone.Width, DropZone.Height, 28),
        NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking => (420, 54, 27),
        NotchMode.Typing => (560, 58 + _paletteHeight, _paletteHeight > 0 ? 30 : 29),
        NotchMode.Answer => (440, 120, 32),
        NotchMode.Result => (560, 680, 34),
        _ => (186, 32, 11),
    };

    Layer? LayerFor(NotchMode mode) => mode switch
    {
        NotchMode.Idle => _rest,
        NotchMode.Peek => _peek,
        NotchMode.Compact => _petShowing ? _petCompact : _compactActive,
        NotchMode.Notification => _notification,
        NotchMode.Card => _card,
        NotchMode.Expanded => _expanded,
        NotchMode.Drop => _drop,
        NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking or NotchMode.Typing => _searchBar,
        NotchMode.Answer => _answer,
        NotchMode.Result => _result,
        _ => null,
    };

    void Transition(NotchMode mode)
    {
        var spec = SpecFor(mode);
        bool grow = spec.W * spec.H > _spec.W * _spec.H + 1;
        bool changed = mode != _mode || spec != _spec;
        _mode = mode;
        _spec = spec;

        if (changed)
        {
            // Growing is a touch bouncy, like Dynamic Island; shrinking settles without overshoot.
            double response = grow ? 0.5 : 0.4;
            double damping = grow ? 0.72 : 0.9;
            if (mode == NotchMode.Hidden) (response, damping) = (0.32, 1);
            // Calm: the notch still moves to its new size, but never overshoots.
            if (!MotionPrefs.Lively) damping = 1;
            _w.AnimateTo(spec.W, response, damping);
            _h.AnimateTo(spec.H, response, damping);
            _r.AnimateTo(spec.R, response, Math.Min(1, damping + 0.08));
            // A soft shadow always grounds the notch; bigger cards cast a fuller one.
            _shadow.AnimateTo(mode == NotchMode.Hidden ? 0 : spec.H > 60 ? 1 : 0.5, 0.45, 1);
        }
        UpdateEdgeHold();

        if (mode == NotchMode.Typing) SearchBar.Width = spec.W;
        else if (mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking) SearchBar.Width = spec.W;
        SearchBar.Height = spec.H;

        var target = LayerFor(mode);
        foreach (var layer in _layers) SetLayerActive(layer, layer == target, grow);

        EnsureWindowHeight(Math.Max(_h.Value, spec.H));
        if (changed) Animator.Kick();
    }

    void SetLayerActive(Layer layer, bool active, bool grow)
    {
        if (active)
        {
            // Already in and settled (a re-post of the same activity, a media position update): nothing to animate.
            // Re-arming the springs here kept the render loop and the window repainting on every such update.
            if (layer.Active && layer.Opacity.Target == 1 && layer.Size.Target == 1 && layer.Opacity.IsSettled && layer.Size.IsSettled)
            {
                Canvas.SetLeft(layer.El, Cx - layer.El.Width / 2);
                return;
            }
            if (!layer.Active)
            {
                if (layer.Opacity.Value < 0.05) layer.Size.Snap(grow ? 0.9 : 1.05);
                layer.El.Visibility = Visibility.Visible;
            }
            Canvas.SetLeft(layer.El, Cx - layer.El.Width / 2);
            double delay = grow ? 0.06 : 0.03;
            layer.Opacity.AnimateTo(1, 0.3, 1, delay);
            layer.Size.AnimateTo(1, 0.5, MotionPrefs.Lively ? 0.78 : 1, delay);
        }
        else if (layer.Active || layer.Opacity.Target > 0)
        {
            layer.Opacity.AnimateTo(0, 0.14, 1);
            layer.Size.AnimateTo(grow ? 1.04 : 0.94, 0.25, 1);
        }
        layer.Active = active;
    }

    void UpdateBubble()
    {
        // The pet's compact notch has the action circle and dot instead of the side bubble.
        bool pet = _mode == NotchMode.Compact && _petShowing;
        if (pet) _chrome.Set(_petOwner, _fullscreen ? null : _petDot, PetGlow);
        else _chrome.Hide();
        _petTick.IsEnabled = pet;
        bool show = !pet && _secondary is not null && _mode is NotchMode.Compact or NotchMode.Idle;
        if (show)
        {
            BubbleContent.Content = _secondary!.Kind switch
            {
                ActivityKind.NowPlaying when _services.Media.Art is { } art => new Border
                {
                    Width = 20,
                    Height = 20,
                    CornerRadius = new CornerRadius(10),
                    Background = new ImageBrush(art) { Stretch = Stretch.UniformToFill },
                },
                ActivityKind.NowPlaying => CompactView.Glyph("", 13, Brushes.White),
                ActivityKind.Downloading => new Spinner { Width = 16, Height = 16, Stroke = (Brush)Application.Current.Resources["AccentBrush"] },
                ActivityKind.Custom when _secondary.Payload is Modules.ICompactContent c && c.BuildBubble() is { } bubble => bubble,
                _ => CompactView.Glyph("", 13, Brushes.White),
            };
        }
        _bubbleOpacity.AnimateTo(show ? 1 : 0, 0.35, 1);
        _bubbleScale.AnimateTo(show ? 1 : 0.4, 0.45, show ? 0.65 : 1);
    }

    // ---- The pet wears the activity ---------------------------------------------------------------

    PetLive? PetFor(Activity? a, DateTime? now = null) => a is null ? null : PetActivitySource.From(a, _services, now ?? DateTime.UtcNow);

    /// <summary>Music playing: the notch glows in the album's colour.</summary>
    bool PetGlow => _petOwner?.State is { Costume: PetCostume.Music, Playing: true };

    /// <summary>
    /// Puts the pet in <paramref name="current"/>'s costume (or the one the user swapped to) and picks the dot; false
    /// when it isn't something the pet wears (a volume HUD, a keyboard pill, a message), which keeps its own pill.
    /// Over a fullscreen app only what broke through shows, with no dot.
    /// </summary>
    bool PreparePet(Activity current)
    {
        var now = DateTime.UtcNow;
        if (PetFor(current, now) is not { } first) return false;
        var lives = new List<PetLive> { first };
        if (!_fullscreen)
            foreach (var a in _ranked)
                if (a.Id != current.Id && PetFor(a, now) is { } live) lives.Add(live);
        var stage = PetActivities.Stage(lives.Select(l => l.State).ToList(), _fullscreen ? null : _petPreferred);
        _petOwner = lives.FirstOrDefault(l => l.State.Id == stage.Owner) ?? first;
        _petDot = stage.Dot is null ? null : lives.FirstOrDefault(l => l.State.Id == stage.Dot);
        _activityView.Show(_petOwner, _services.Settings.PetEnabled);
        _petShowing = true;
        return true;
    }

    /// <summary>Once a second while the pet wears something, and right after its circle is used: live values.</summary>
    void RefreshPet()
    {
        if (_mode != NotchMode.Compact || !_petShowing || _activity is null)
        {
            _petTick.IsEnabled = false;
            return;
        }
        if (!PreparePet(_activity))
        {
            Recompute();
            return;
        }
        _chrome.Set(_petOwner, _fullscreen ? null : _petDot, PetGlow);
        if (_petOwner is not null) _chrome.Refresh(_petOwner);
        // A line under the row came or went (a lyric started): the notch grows or shrinks around it.
        if (SpecFor(_mode) != _spec) Transition(_mode);
    }

    void RunCircle(CircleAction action)
    {
        if (_petOwner is not { } owner) return;
        try
        {
            owner.Run?.Invoke(action);
        }
        catch (Exception ex)
        {
            Log.Warn($"Action circle {action} on {owner.Activity.Id} failed", ex);
        }
        // Opening or dismissing a one-off moment is the end of it.
        if (action is CircleAction.Open or CircleAction.Dismiss or CircleAction.Skip && !PetActivities.IsOngoing(owner.State)) ActionClicked?.Invoke(owner.Activity.Id);
        RefreshPet();
    }

    /// <summary>
    /// The pet itself was clicked: a finished timer or download runs its own click
    /// (dismiss, the download card), and anything ongoing opens the notch (its one action is on the circle).
    /// </summary>
    void PetBodyClicked()
    {
        var owner = _petOwner;
        if (owner is null || !PetActivities.BodyClickRunsActivity(owner.State) || owner.Activity.Click is not { } click)
        {
            OpenFromHover();
            return;
        }
        click();
        if (IsAction(owner.Activity) || !PetActivities.IsOngoing(owner.State)) ActionClicked?.Invoke(owner.Activity.Id);
    }

    internal string DescribeCircle()
    {
        var o = _petOwner;
        var pet = _activityView.Pet;
        return $"mode={_mode} petCompact={(_mode == NotchMode.Compact && _petShowing ? "yes" : "no")} petOn={_services.Settings.PetEnabled} " +
               $"owner={(o is null ? "-" : $"{o.Activity.Id}:{o.State.Costume}")} face={pet.Face?.ToString() ?? "-"} " +
               $"label='{_activityView.LabelText}' value='{_activityView.ValueText}' line='{_activityView.LineText}' {_chrome.Describe()} " +
               $"preferred={_petPreferred ?? "-"} mouth={pet.MouthOpen:0.00} fullscreen={_fullscreen}";
    }

    /// <summary>Test hook: click the action circle, or with "dot" the dot (as a user would).</summary>
    internal string ClickCircle(string what)
    {
        if (_mode != NotchMode.Compact || !_petShowing) return "error: no pet activity on the notch";
        if (what.Trim() == "dot")
        {
            if (_chrome.DotId is not { } id) return "error: no dot";
            _petPreferred = id;
            Recompute();
            return DescribeCircle();
        }
        if (_chrome.Circle is not { } spec) return "error: no circle";
        if (!spec.Enabled) return $"disabled: {spec.Description}";
        RunCircle(spec.Action);
        return $"ran {spec.Action}; {DescribeCircle()}";
    }

    // ---- Per-frame rendering ------------------------------------------------------------------

    void OnFrame(double dt) => ApplyVisuals(force: false);

    void ApplyVisuals(bool force)
    {
        double w = _w.Value, h = _h.Value, r = _r.Value;
        if (force || w != _lastW || h != _lastH || r != _lastR)
        {
            _lastW = w;
            _lastH = h;
            _lastR = r;
            Shape.Update(w, h, r);
            ShadowShape.Update(w, h, r);
            Edge.UpdateShape(w, h, r);
            ContentHost.Clip = Shape.Geometry;

            bool visible = h > 1;
            Canvas.SetLeft(HitArea, Cx - w / 2 - 12);
            Canvas.SetTop(HitArea, 0);
            HitArea.Width = visible ? w + 24 : 0;
            HitArea.Height = visible ? h + 8 : 0;
        }
        ShadowShape.Opacity = _shadow.Value;

        foreach (var layer in _layers)
        {
            double o = layer.Opacity.Value;
            layer.El.Opacity = o;
            layer.Scale.ScaleX = layer.Scale.ScaleY = layer.Size.Value;
            if (!layer.Active && o <= 0.003 && layer.El.Visibility == Visibility.Visible) layer.El.Visibility = Visibility.Collapsed;
        }

        _chrome.Apply(Cx, w, h, r);
        Bubble.Opacity = _bubbleOpacity.Value;
        BubbleScale.ScaleX = BubbleScale.ScaleY = _bubbleScale.Value;
        Canvas.SetLeft(Bubble, Cx + w / 2 + 10);
        Canvas.SetTop(Bubble, 0);
        Bubble.IsHitTestVisible = _bubbleOpacity.Target > 0;

        if (_w.IsSettled && _h.IsSettled && _shadow.IsSettled)
        {
            double want = (_spec.H + ShadowPad) * _scale;
            if (Math.Abs(Height - want) > 0.5) Height = want;
        }
    }

    void EnsureWindowHeight(double contentHeight)
    {
        double want = (contentHeight + ShadowPad) * _scale;
        if (want > Height) Height = want;
    }

    // ---- Hover ---------------------------------------------------------------------------------

    internal string HoverDiag =>
        $"root={Root.IsMouseOver} bubble={Bubble.IsMouseOver} bubbleHit={Bubble.IsHitTestVisible} bubbleLeft={Canvas.GetLeft(Bubble):0} " +
        $"bubbleOpacity={Bubble.Opacity:0.00} mode={_mode} hoverExpanded={_hoverExpanded} hoverInMs={_hoverIn.Interval.TotalMilliseconds} dpiX={System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX}";

    bool CanHover => _searchMode is null && !_fullscreen && !_dragging;

    void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        _hoverOut.Stop();
        if (_held) _visitedWhileHeld = true;
        _wiggle.Reset();
        _restView.Mood.Noticed();
        if (_mode == NotchMode.Card) return;
        // Hovering a notification holds it on screen (to read or click) instead of opening the card.
        if (_mode == NotchMode.Notification)
        {
            _hoveringNotification = true;
            NotificationHovered?.Invoke(true);
            return;
        }
        if (IsAction(_activity))
        {
            if (_heldActionId is null) SetActionHold(true);
            if (_mode == NotchMode.Compact) return;
        }
        if (!_hoverExpanded) StartHoverIn();
    }

    /// <summary>
    /// Hovering no longer opens the notch at once: the pointer has to slow down and rest on it, which shows
    /// the peek, and rest a little longer (or click) to open it (<see cref="HoverIntent"/>). A pointer on its
    /// way to a browser tab or the address bar passes straight through. Over a pill that does something when
    /// clicked (the meeting mic, a running timer) there's no peek and the wait is longer, so a click lands first.
    /// </summary>
    void StartHoverIn()
    {
        if (!CanHover || Bubble.IsMouseOver || _chrome.IsMouseOver) return;
        // The pet's own body opens the notch (its action is on the circle), so it peeks like the resting notch.
        bool clickable = _mode == NotchMode.Compact && !_petShowing && _activity is { Click: not null, Kind: not ActivityKind.NowPlaying };
        _intent.Reset();
        // Settings: the hover speed sets the waits; with the peek off the notch opens after the whole wait.
        _intent.SpeedSetting = _services.Settings.HoverSpeed;
        _intent.AllowPeek = !clickable && _services.Settings.PeekEnabled;
        if (_peeking) _intent.ForcePeek(_hoverClock.Elapsed);
        _hoverIn.Start();
        SampleHover();
    }

    void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        _hoverIn.Stop();
        _press = null;
        if (_heldActionId is not null) SetActionHold(false);
        if (_hoveringNotification)
        {
            _hoveringNotification = false;
            NotificationHovered?.Invoke(false);
        }
        // A tab opened for the user (by a command or a click) closes once they've looked and moved on.
        if (_held && _visitedWhileHeld) ReleaseHold();
        // Leaving the peek (or the open notch) goes back to resting.
        if (_hoverExpanded || _peeking) _hoverOut.Start();
    }

    /// <summary>The pointer may be standing still (no mouse moves), so the timer keeps sampling it.</summary>
    void OnHoverTick(object? sender, EventArgs e) => SampleHover();

    void SampleHover()
    {
        if (!Root.IsMouseOver || !CanHover || Bubble.IsMouseOver || _chrome.IsMouseOver || _hoverExpanded)
        {
            _hoverIn.Stop();
            return;
        }
        // A pressed button means a drag is passing through (the drop zone handles that) or a click is coming.
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _intent.Reset();
            return;
        }
        // Hovering a notification holds it to be read, and an action pill waits to be clicked: neither peeks.
        if (_mode == NotchMode.Notification || (_mode == NotchMode.Compact && IsAction(_activity))) return;

        var p = Mouse.GetPosition(Root);
        switch (_intent.Sample(p.X, p.Y, _hoverClock.Elapsed))
        {
            case HoverDecision.Peek:
                ShowPeek();
                break;
            case HoverDecision.Open:
                OpenFromHover();
                break;
        }
    }

    void ShowPeek()
    {
        _peekView.Refresh();
        _peeking = true;
        Recompute();
        UpdateBubble();
    }

    /// <summary>Open the full notch from the resting notch or the peek (rested long enough, or clicked).</summary>
    void OpenFromHover()
    {
        _hoverIn.Stop();
        if (!CanHover) return;
        _peeking = false;
        _hoverExpanded = true;
        Recompute();
        UpdateBubble();
        if (!Root.IsMouseOver) _hoverOut.Start();
    }

    void OnPeekGlanceClicked(Modules.GlanceItem? item)
    {
        if (item?.OnClick is not { } click)
        {
            OpenFromHover();
            return;
        }
        // Done with the peek; it doesn't come back until the pointer leaves and rests on the notch again.
        _hoverIn.Stop();
        _peeking = false;
        Recompute();
        UpdateBubble();
        try { click(); }
        catch (Exception ex) { Log.Warn("Glance click failed", ex); }
    }

    /// <summary>A quick sideways drag with the button held skips tracks, instead of being a click.</summary>
    void OnPressReleased(object sender, MouseButtonEventArgs e)
    {
        if (_press is not { } press) return;
        _press = null;
        var p = e.GetPosition(Root);
        int direction = DragSwipe.Classify(p.X - press.At.X, p.Y - press.At.Y, _hoverClock.Elapsed - press.T);
        if (direction == 0 || !GesturesOn) return;
        e.Handled = true;
        NotchHorizontalWheel?.Invoke(direction * 120);
        if (Root.IsMouseOver && !_hoverExpanded) StartHoverIn();
    }

    void OnHoverOut(object? sender, EventArgs e)
    {
        _hoverOut.Stop();
        if (Root.IsMouseOver || _holdOpen > 0) return;
        _hoverExpanded = false;
        _peeking = false;
        Recompute();
        UpdateBubble();
    }

    // ---- Typing in the open notch ----------------------------------------------------------------

    /// <summary>
    /// A text box in the open notch wants the keyboard (it was clicked, or its view was opened by a click). The
    /// notch never activates on hover, so take focus now, keep the notch open while typing (the pointer may wander
    /// off), and remember the window that had focus to hand it back afterwards. Esc goes back (ExpandedView).
    /// </summary>
    void BeginDetailTyping(Control input)
    {
        if (_searchMode is not null || _mode != NotchMode.Expanded) return;
        if (!_detailTyping)
        {
            _detailTyping = true;
            _holdOpen++;
            var current = Native.GetForegroundWindow();
            _typingReturnTo = current == _hwnd ? IntPtr.Zero : current;
            if (_interactive) Activate();
            else SetInteractive(true);
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!_detailTyping || !input.IsVisible) return;
            input.Focus();
            Keyboard.Focus(input);
        }, DispatcherPriority.Input);
    }

    /// <summary>After the view changed: typing is over unless the keyboard is still in a text box in the notch.</summary>
    void CheckDetailTyping()
    {
        if (!_detailTyping) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox
            && Keyboard.FocusedElement is UIElement { IsVisible: true } focused && Expanded.IsAncestorOf(focused)) return;
        EndDetailTyping(giveBack: true);
    }

    /// <summary>Back to never taking focus; with <paramref name="giveBack"/> the app that had focus gets it back.</summary>
    void EndDetailTyping(bool giveBack)
    {
        if (!_detailTyping) return;
        _detailTyping = false;
        _holdOpen--;
        var back = _typingReturnTo;
        _typingReturnTo = IntPtr.Zero;
        if (_searchMode is null)
        {
            SetInteractive(false);
            if (giveBack && back != IntPtr.Zero && Native.GetForegroundWindow() == _hwnd) Native.ForceForeground(back);
        }
        if (!Root.IsMouseOver && _mode == NotchMode.Expanded) _hoverOut.Start();
    }

    internal string DescribeTyping() =>
        $"typing={_detailTyping} interactive={_interactive} focused={(Keyboard.FocusedElement as FrameworkElement)?.GetType().Name ?? "-"} " +
        $"active={(Native.GetForegroundWindow() == _hwnd ? "notch" : "other")} hold={_holdOpen}";

    /// <summary>Test hook: as if the user clicked into the open view's first text box.</summary>
    internal string FocusFirstInputForTest()
    {
        if (_mode != NotchMode.Expanded) return "error: the notch isn't open";
        return Expanded.RequestFirstTextInput() ? "ok" : "error: no text box in the open view";
    }

    // ---- v2 test hooks (pipe commands) -----------------------------------------------------------

    internal string DescribeIdle()
    {
        var pet = _restView.Pet;
        var mood = _restView.Mood;
        return $"mode={_mode} peeking={_peeking} pet={(_restView.PetEnabled ? "on" : "off")} mood={mood.Mood}{(mood.Forced is null ? "" : "(forced)")} " +
               $"eyes={pet.Mood} sleepyHours={EyeLogic.IsSleepy(DateTime.Now)} following={pet.IsFollowing} " +
               $"look={pet.FollowOffset:0.0} glance='{_restView.CurrentText}' items=[{_glances.Describe()}] " +
               $"intent={_intent.Stage} speed={_intent.Speed:0.00}";
    }

    internal string DescribePeek() => $"mode={_mode} {_peekView.Describe()}";

    internal void BounceEyes() => _restView.Pet.Bounce();

    /// <summary>Shammy the pet on the resting notch on or off ("pet on/off"); saved in the settings.</summary>
    internal void SetPetEnabled(bool on)
    {
        if (_services.Settings.PetEnabled != on)
        {
            _services.Settings.PetEnabled = on;
            _services.Settings.Save();
        }
        _restView.SetPetEnabled(on);
        RefreshPet();
    }

    /// <summary>Force the pet's mood for testing ("auto" follows the real inputs again).</summary>
    internal string SetPetMood(string name)
    {
        var controller = _restView.Mood;
        string key = name.Trim().Replace("-", "").ToLowerInvariant();
        key = key switch { "timer" => "timerdone", "meeting" => "meetingsoon", "listen" => "listening", "think" => "thinking", "sleep" => "sleepy", _ => key };
        if (key == "auto") controller.Forced = null;
        else if (Enum.TryParse<PetMood>(key, ignoreCase: true, out var mood) && Enum.IsDefined(mood)) controller.Forced = mood;
        else return "error: usage pet-mood idle|music|notify|timer|listen|think|meeting|tired|sleepy|happy|auto";
        controller.Update();
        return DescribeIdle();
    }

    /// <summary>Show the peek (or go back to resting) as if the pointer had rested on, or left, the notch.</summary>
    internal string ForcePeek(bool on)
    {
        if (on)
        {
            if (!CanHover) return "not now: search, fullscreen or a drag is active";
            _hoverExpanded = false;
            _intent.ForcePeek(_hoverClock.Elapsed);
            ShowPeek();
        }
        else
        {
            _hoverIn.Stop();
            _hoverExpanded = _peeking = false;
            Recompute();
            UpdateBubble();
        }
        return DescribePeek();
    }

    /// <summary>Settings: GesturesEnabled. Off, the wheel and swipes over the notch do nothing.</summary>
    bool GesturesOn => _services.Settings.GesturesEnabled;

    /// <summary>The same events a real swipe or wheel over the notch raises; a dry run only says what it would do.</summary>
    internal string SimulateGesture(string gesture, bool dryRun)
    {
        if (!GesturesOn && !dryRun) return "off: gestures are turned off in settings";
        (bool horizontal, int delta) = gesture switch
        {
            "next" => (true, 120),
            "prev" or "previous" => (true, -120),
            "volup" => (false, 120),
            "voldown" => (false, -120),
            _ => (false, 0),
        };
        if (delta == 0) return "error: usage v2-gesture next|prev|volup|voldown [dry]";
        string what = horizontal ? (delta > 0 ? "next track" : "previous track") : (delta > 0 ? "volume up" : "volume down");
        if (dryRun) return $"would: {what} (horizontal={horizontal} delta={delta})";
        if (horizontal) NotchHorizontalWheel?.Invoke(delta);
        else NotchWheel?.Invoke(delta);
        return $"sent: {what}";
    }

    /// <summary>The modules' glance providers (ModuleRegistry.Glances), for the resting glance and the peek.</summary>
    internal IReadOnlyDictionary<string, Func<Modules.GlanceItem?>>? GlanceProviders
    {
        set => _glances.Providers = value;
    }

    // ---- Drag and drop onto the shelf -----------------------------------------------------------

    void OnDragEnter(object sender, DragEventArgs e)
    {
        _dragOut.Stop();
        // A tab a module opened for you (held for 8 s) mustn't block dropping files; only a shelf drag does.
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop) && _searchMode is null && _holdOpen - (_held ? 1 : 0) == 0;
        e.Effects = files ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
        if (files && !_dragging)
        {
            _dragging = true;
            _peeking = false;
            _hoverIn.Stop();
            Recompute();
        }
    }

    void OnDragOut(object? sender, EventArgs e)
    {
        _dragOut.Stop();
        if (!_dragging) return;
        _dragging = false;
        DropZone.Reset();
        Recompute();
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        _dragOut.Stop();
        _dragging = false;
        // The drop zone knows which of its targets (Shelf, phone, compress, convert) the files landed on.
        var target = DropZone.TakeTarget();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            if (target is null or Core.DropActions.DropAction.Shelf)
            {
                FilesDropped?.Invoke(files);
                ExpandTo(shelf: true);
            }
            else
            {
                DropZone.RaiseDropped(target.Value, files);
                Recompute();
            }
        }
        else
        {
            Recompute();
        }
        e.Handled = true;
    }
}
