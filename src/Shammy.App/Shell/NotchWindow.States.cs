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

public partial class NotchWindow
{
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
        // A quiet peek (a call, a meeting, focus) is the pet's one line, not the card.
        else if (_activity is { Kind: ActivityKind.Notification, Payload: not NotificationPayload { Peek: true } } && !_hoverExpanded)
            mode = NotchMode.Notification;
        else if (_hoverExpanded) mode = NotchMode.Expanded;
        else if (_peeking) mode = NotchMode.Peek;
        // Music, like every live activity the pet can wear, shows as the pet in costume (ActivityView); a paused
        // song with nothing to show rests.
        else if (_activity is not null && (_activity.Kind != ActivityKind.NowPlaying || PetFor(_activity) is not null)) mode = NotchMode.Compact;
        else mode = NotchMode.Idle;

        // Typing in the open notch ends when the notch stops being open (closed, a card or a notification took over).
        if (mode != NotchMode.Expanded && _detailTyping) EndDetailTyping(giveBack: true);
        if (mode != NotchMode.Expanded && _keyboardMode) EndKeyboard(giveBack: true);
        if (mode == NotchMode.Compact) PrepareCompact();
        if (mode == NotchMode.Notification && _activity?.Payload is NotificationPayload p)
        {
            // The next message taking the place of one on show dims through the change instead of cutting to it.
            if (_mode == NotchMode.Notification && !ReferenceEquals(_shownNotification, p.Notification)) DipThrough(_notification);
            _shownNotification = p.Notification;
            NotificationCard.Show(p.Notification, p.More);
            _stackDepth = PopupStack<ShammyNotification>.Peeking(p.More);
        }
        // Breaking through: the fullscreen app may have pushed itself above us; re-assert topmost (without activating).
        if (_fullscreen && _mode == NotchMode.Hidden && mode != NotchMode.Hidden) BringToFront();
        if (mode != _mode || mode is NotchMode.Compact or NotchMode.Card) Transition(mode);
        // The side bubble belongs next to the compact pill only; hide it whenever a card, tab or drop zone opens.
        UpdateBubble();
        UpdateStack();
        UpdateTuckTab();
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

    /// <summary>
    /// Each state's island: width, height and corner radius. The closed states (resting, a pill) are fully rounded
    /// pills at least <see cref="IslandGeometry.ClosedHeight"/> tall, their content (still its own size) centred in
    /// them; the open states keep their sizes and become slabs with all four corners rounded.
    /// </summary>
    (double W, double H, double R) SpecFor(NotchMode mode) => mode switch
    {
        NotchMode.Hidden => (150, 0, 0),
        NotchMode.Idle => Pill(RestView.RestWidth, RestView.RestHeight),
        NotchMode.Peek => (PeekView.PeekWidth, PeekView.PeekHeight, 26),
        NotchMode.Compact when _petShowing => (_activityView.CurrentWidth, IslandGeometry.ClosedPill(_activityView.CurrentHeight), _activityView.CurrentRadius),
        NotchMode.Compact => Pill(_compactWidth, 32),
        NotchMode.Notification => (NotificationView.CardWidth, NotificationView.CardHeight, 30),
        NotchMode.Card => (_cardSize.W, _cardSize.H, Math.Min(32, _cardSize.H / 2)),
        NotchMode.Expanded => (Expanded.Width, Expanded.Height, Expanded.SheetOpen ? Math.Min(30, Expanded.Height / 2) : 36),
        NotchMode.Drop => (DropZone.Width, DropZone.Height, 28),
        NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking => (420, 54, 27),
        NotchMode.Typing => (560, 58 + _paletteHeight, _paletteHeight > 0 ? 30 : 29),
        NotchMode.Answer => (AnswerView.Width, AnswerView.Height, 32),
        NotchMode.Result => (560, 680, 34),
        _ => Pill(186, 32),
    };

    static (double W, double H, double R) Pill(double width, double contentHeight)
    {
        double h = IslandGeometry.ClosedPill(contentHeight);
        return (width, h, h / 2);
    }

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
        if (mode != NotchMode.Expanded)
        {
            // The calendar wing lives only beside the open island.
            FoldCalendar(animate: mode != NotchMode.Hidden, withIsland: true);
            // Leaving the open island (Stage taking over, a card, closing) means it reopens on the home page, even when
            // its view never got hidden on the way. (Resetting on the way back in missed the Stage case: checked live.)
            // It happens once the open island has faded out (ApplyVisuals), so you never see it jump to page one.
            _firstPageWhenHidden = true;
        }
        else if (_firstPageWhenHidden)
        {
            _firstPageWhenHidden = false;
            // Already gone (hidden, tucked) without fading: it reopens on the home page. Back before it had finished
            // fading, it simply stays on the page you left: no jump on a quick reopen.
            if (_expanded.El.Visibility != Visibility.Visible) Expanded.FirstPage();
        }
        var spec = SpecFor(mode);
        bool grow = spec.W * spec.H > _spec.W * _spec.H + 1;
        bool changed = mode != _mode || spec != _spec;
        _mode = mode;
        _spec = spec;

        if (changed)
        {
            // Growing is a touch bouncy, like Dynamic Island; shrinking settles without overshoot.
            // Quick enough to feel like it answers you, still with a little give when it grows.
            double response = grow ? 0.38 : 0.3;
            double damping = grow ? 0.74 : 0.9;
            if (mode == NotchMode.Hidden) (response, damping) = (0.32, 1);
            // Calm: the notch still moves to its new size, but never overshoots.
            if (!MotionPrefs.Lively) damping = 1;
            _w.AnimateTo(spec.W, response, damping);
            _h.AnimateTo(spec.H, response, damping);
            _r.AnimateTo(spec.R, response, Math.Min(1, damping + 0.08));
            // A soft shadow always grounds the floating island; bigger cards cast a fuller one.
            _shadow.AnimateTo(mode == NotchMode.Hidden ? 0 : spec.H > 60 ? 1 : 0.6, 0.45, 1);
        }
        UpdateEdgeHold();
        if (changed) UpdateAmbience();

        if (mode == NotchMode.Typing) SearchBar.Width = spec.W;
        else if (mode is NotchMode.Listening or NotchMode.Circling or NotchMode.Thinking) SearchBar.Width = spec.W;
        SearchBar.Height = spec.H;

        var target = LayerFor(mode);
        foreach (var layer in _layers) SetLayerActive(layer, layer == target, grow);

        EnsureWindowHeight(Math.Max(_h.Value, spec.H));
        UpdateTuckTab();
        Animator.Kick();
    }

    ShammyNotification? _shownNotification;
    ImageSource? _bubbleArt;
    Border? _bubbleDisc;

    /// <summary>
    /// Content changing in place on a layer that's on show (a card replacing a card, the next message): it dims and
    /// comes back through the change, as the pet's pill already does, so nothing hard-cuts.
    /// </summary>
    void DipThrough(Layer layer)
    {
        if (!MotionPrefs.Lively || layer.Opacity.Value < 0.5) return;
        layer.Swap.Snap(0);
        layer.Swap.AnimateTo(1, 0.3, 1);
    }

    /// <summary>The open island should go back to its first page once it has faded out.</summary>
    bool _firstPageWhenHidden;

    void SetLayerActive(Layer layer, bool active, bool grow)
    {
        if (active)
        {
            // Already in and settled (a re-post of the same activity, a media position update): nothing to animate.
            // Re-arming the springs here kept the render loop and the window repainting on every such update.
            if (layer.Active && layer.Opacity.Target == 1 && layer.Size.Target == 1 && layer.Opacity.IsSettled && layer.Size.IsSettled)
            {
                Place(layer);
                return;
            }
            if (!layer.Active)
            {
                if (layer.Opacity.Value < 0.05) layer.Size.Snap(grow ? 0.9 : 1.05);
                layer.El.Visibility = Visibility.Visible;
            }
            Place(layer);
            double delay = grow ? 0.03 : 0.02;
            layer.Opacity.AnimateTo(1, 0.24, 1, delay);
            layer.Size.AnimateTo(1, 0.38, MotionPrefs.Lively ? 0.8 : 1, delay);
        }
        else if (layer.Active || layer.Opacity.Target > 0)
        {
            layer.Opacity.AnimateTo(0, 0.14, 1);
            layer.Size.AnimateTo(grow ? 1.04 : 0.94, 0.25, 1);
        }
        layer.Active = active;
    }

    /// <summary>Centred across the island, and down the middle of a pill taller than the content (a 32 px row in a 38 px pill).</summary>
    void Place(Layer layer)
    {
        Canvas.SetLeft(layer.El, Cx - layer.El.Width / 2);
        Canvas.SetTop(layer.El, IslandGeometry.ContentTop(_spec.H, layer.El.Height));
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
                // The same artwork keeps the same little disc (rebuilding it on every update redrew it each time).
                ActivityKind.NowPlaying when _services.Media.Art is { } art => ReferenceEquals(_bubbleArt, art) && _bubbleDisc is not null
                    ? _bubbleDisc
                    : _bubbleDisc = new Border
                    {
                        Width = 20,
                        Height = 20,
                        CornerRadius = new CornerRadius(10),
                        Background = new ImageBrush(_bubbleArt = art) { Stretch = Stretch.UniformToFill },
                    },
                ActivityKind.NowPlaying => CompactView.Glyph("", 13, Brushes.White),
                ActivityKind.Downloading => new Spinner { Width = 16, Height = 16, Stroke = Ui.Tokens.Res("AccentBrush") },
                ActivityKind.Custom when _secondary.Payload is Modules.ICompactContent c && c.BuildBubble() is { } bubble => bubble,
                _ => CompactView.Glyph("", 13, Brushes.White),
            };
        }
        _bubbleOpacity.AnimateTo(show ? 1 : 0, 0.35, 1);
        _bubbleScale.AnimateTo(show ? 1 : 0.4, 0.45, show ? 0.65 : 1);
    }
}
