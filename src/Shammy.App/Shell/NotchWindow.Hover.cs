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
    // ---- Hover ---------------------------------------------------------------------------------

    internal string HoverDiag =>
        $"root={Root.IsMouseOver} bubble={Bubble.IsMouseOver} bubbleHit={Bubble.IsHitTestVisible} bubbleLeft={Canvas.GetLeft(Bubble):0} " +
        $"tab={TuckTab.IsMouseOver} tabTop={Canvas.GetTop(TuckTab):0} tabVis={TuckTab.Visibility} hitH={HitArea.Height:0} " +
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
        if (!CanHover || Bubble.IsMouseOver || _chrome.IsMouseOver || OverTuckTab || _hoverBlocked) return;
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
        _hoverBlocked = false;
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

        // Over the tuck tab the pointer is on its way to click it, not resting on the notch.
        if (OverTuckTab)
        {
            _intent.Reset();
            return;
        }
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
}
