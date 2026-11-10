using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Shammy.Core.Notch;
using Shammy.Infrastructure;
using Shammy.Notifications;

namespace Shammy.Views;

/// <summary>
/// A notification as it drops out of the notch: the app's avatar as artwork, a tracked app name, a bold title and two
/// lines of text. Hovering holds it (the window keeps it up) and reveals Dismiss. Dragging it sideways sends it
/// away (<see cref="SwipeAway"/>); when more wait underneath, the next one comes up in its place.
/// </summary>
public partial class NotificationView : UserControl
{
    public const double CardWidth = 460, CardHeight = 104;

    readonly TranslateTransform _slide = new();
    ShammyNotification? _shown;
    Point? _down;
    double _dx, _speed, _lastX;
    DateTime _lastMove;
    bool _dragged, _leaving;

    public NotificationView()
    {
        InitializeComponent();
        Sheet.RenderTransform = _slide;
        MouseEnter += (_, _) => SetHeld(true);
        MouseLeave += (_, _) => SetHeld(false);
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        LostMouseCapture += (_, _) =>
        {
            // Something else took the pointer mid-drag: the card goes back where it was.
            if (_down is null) return;
            _down = null;
            _dragged = false;
            SettleBack();
        };
    }

    internal event Action<ShammyNotification>? Opened;
    internal event Action? Dismissed;
    internal event Action<ShammyNotification>? MuteRequested;

    /// <summary>How many have been swiped away (for tests).</summary>
    internal int Swiped { get; private set; }

    internal void Show(ShammyNotification n, int more)
    {
        bool same = _shown is not null && _shown.Id == n.Id;
        // The same message re-posted (a hover hold, a privacy change) must not snap a card that is being dragged
        // or is on its way out back into place.
        if (!(same && (_down is not null || _leaving))) ResetSheet(fadeIn: _leaving && !same);
        _shown = n;
        AppText.Text = n.AppName;
        TimeText.Text = n.TimeText;
        TitleText.Text = n.Title;
        BodyText.Text = n.Body;
        BodyText.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreBadge.Visibility = more > 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreText.Text = $"+{more}";
        MuteItem.Header = $"Don't show from {n.AppName}";
        // Shell icons have transparent padding, so the crisp icon sits on a field blurred from its own colours.
        IconImage.Source = n.Icon;
        IconField.Source = n.Icon;
        System.Windows.Automation.AutomationProperties.SetName(this,
            n.Body.Length > 0 ? $"{n.AppName}: {n.Title}. {n.Body}" : $"{n.AppName}: {n.Title}");
        IconGlyph.Visibility = n.Icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>While the pointer is over it the window holds the notification on screen: show Dismiss, and "Held" for the time.</summary>
    void SetHeld(bool held)
    {
        DismissButton.Visibility = held ? Visibility.Visible : Visibility.Hidden;
        HeldText.Visibility = held ? Visibility.Visible : Visibility.Collapsed;
        TimeText.Visibility = held ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnDismiss(object sender, RoutedEventArgs e) => Dismissed?.Invoke();

    void OnMute(object sender, RoutedEventArgs e)
    {
        if (_shown is not null) MuteRequested?.Invoke(_shown);
    }

    /// <summary>"Remind me in 1 hour" from the right-click menu.</summary>
    internal event Action<ShammyNotification>? RemindRequested;

    void OnRemind(object sender, RoutedEventArgs e)
    {
        if (_shown is not null) RemindRequested?.Invoke(_shown);
    }

    // ---- Swiping it away ----------------------------------------------------------------------------

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_leaving || e.OriginalSource is Button) return;
        _down = e.GetPosition(this);
        _lastX = _down.Value.X;
        _lastMove = DateTime.UtcNow;
        _dx = _speed = 0;
        _dragged = false;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (_down is not { } down) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _down = null;
            if (_dragged) SettleBack();
            _dragged = false;
            return;
        }
        double x = e.GetPosition(this).X;
        var now = DateTime.UtcNow;
        double seconds = (now - _lastMove).TotalSeconds;
        if (seconds > 0.004)
        {
            // A little smoothing, so one jittery sample does not read as a flick.
            _speed = _speed * 0.4 + (x - _lastX) / seconds * 0.6;
            _lastX = x;
            _lastMove = now;
        }
        _dx = x - down.X;
        if (!_dragged && Math.Abs(_dx) < SwipeAway.Start) return;
        if (!_dragged)
        {
            _dragged = true;
            // Keep following the pointer even if it leaves the card.
            CaptureMouse();
        }
        Slide(_dx);
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        bool had = _down is not null, dragged = _dragged;
        double dx = _dx, speed = (DateTime.UtcNow - _lastMove).TotalSeconds > 0.12 ? 0 : _speed;
        _down = null;
        _dragged = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (dragged)
        {
            Release(dx, speed);
            return;
        }
        if (had && _shown is not null && e.OriginalSource is not Button) Opened?.Invoke(_shown);
    }

    void Slide(double dx)
    {
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        Sheet.BeginAnimation(OpacityProperty, null);
        _slide.X = dx;
        Sheet.Opacity = SwipeAway.Opacity(dx, CardWidth);
    }

    void Release(double dx, double speed)
    {
        if (SwipeAway.Leaves(dx, speed)) Leave(Math.Sign(dx));
        else SettleBack();
    }

    /// <summary>Not far enough: it springs back.</summary>
    void SettleBack()
    {
        if (!MotionPrefs.Lively || Math.Abs(_slide.X) < 0.5)
        {
            ResetSheet(fadeIn: false);
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var back = new DoubleAnimation(_slide.X, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var show = new DoubleAnimation(Sheet.Opacity, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(back, 30);
        Timeline.SetDesiredFrameRate(show, 30);
        _slide.X = 0;
        Sheet.Opacity = 1;
        _slide.BeginAnimation(TranslateTransform.XProperty, back);
        Sheet.BeginAnimation(OpacityProperty, show);
    }

    /// <summary>Far enough: it slides off the way it was going and fades, then counts as dismissed.</summary>
    void Leave(int direction)
    {
        _leaving = true;
        if (!MotionPrefs.Lively)
        {
            Gone();
            return;
        }
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var away = new DoubleAnimation(_slide.X, direction * CardWidth * 0.7, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        var fade = new DoubleAnimation(Sheet.Opacity, 0, TimeSpan.FromMilliseconds(150));
        Timeline.SetDesiredFrameRate(away, 30);
        Timeline.SetDesiredFrameRate(fade, 30);
        away.Completed += (_, _) =>
        {
            if (_leaving) Gone();
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, away);
        Sheet.BeginAnimation(OpacityProperty, fade);
    }

    void Gone()
    {
        Swiped++;
        Sheet.BeginAnimation(OpacityProperty, null);
        Sheet.Opacity = 0;
        // The host shows the one underneath (Show puts the sheet back) or takes the card down.
        Dismissed?.Invoke();
    }

    void ResetSheet(bool fadeIn)
    {
        _leaving = false;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        Sheet.BeginAnimation(OpacityProperty, null);
        _slide.X = 0;
        Sheet.Opacity = 1;
        if (!fadeIn || !MotionPrefs.Lively) return;
        // The next message in the pile rises into place.
        var rise = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(rise, 30);
        Sheet.BeginAnimation(OpacityProperty, rise);
    }

    /// <summary>For tests: drag the card to <paramref name="dx"/> at a speed and let go; says what it did.</summary>
    internal string TestSwipe(double dx, double speed)
    {
        if (_shown is null) return "error: nothing shown";
        int before = Swiped;
        Slide(dx);
        string held = $"x={_slide.X:0} opacity={Sheet.Opacity:0.00}";
        Release(dx, speed);
        // With Calm motion (or no window) the card is gone at once; otherwise once its slide has played.
        return $"dragged {held} leaves={SwipeAway.Leaves(dx, speed)} leaving={_leaving} swiped={Swiped - before}";
    }

    internal string DescribeSheet() => $"x={_slide.X:0} opacity={Sheet.Opacity:0.00} leaving={_leaving} swiped={Swiped} title={_shown?.Title ?? "-"} more={(MoreBadge.Visibility == Visibility.Visible ? MoreText.Text : "-")}";
}
