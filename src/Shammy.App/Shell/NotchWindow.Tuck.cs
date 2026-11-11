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
    // ---- Tucking away -----------------------------------------------------------------------------

    /// <summary>The tab hangs under the notch whenever it is closed (resting, a live pill, the peek), if it is switched on.</summary>
    void UpdateTuckTab()
    {
        bool show = _services.Settings.TuckAwayEnabled && _mode is NotchMode.Idle or NotchMode.Compact or NotchMode.Peek;
        _tabShown.AnimateTo(show ? 1 : 0, 0.25, 1);
    }

    /// <summary>The setting changed: show or hide the tab at once.</summary>
    internal void TuckSettingChanged()
    {
        UpdateTuckTab();
        Animator.Kick();
    }

    /// <summary>Slides the whole notch up out of sight; it comes back by itself after a few seconds.</summary>
    internal void TuckAway()
    {
        if (_tucked || _hwnd == IntPtr.Zero) return;
        _tucked = true;
        _tucks++;
        _hoverIn.Stop();
        // An open or peeking notch goes back to what it was showing, out of sight.
        if (_hoverExpanded || _peeking)
        {
            _hoverExpanded = false;
            _peeking = false;
            Recompute();
            UpdateBubble();
        }
        // Clicks fall through to the app underneath while it is away, the line included.
        Native.SetExStyle(_hwnd, WS_EX_TRANSPARENT);
        if (MotionPrefs.Lively) _tuck.AnimateTo(1, 0.32, 1);
        else _tuck.Snap(1);
        Animator.Kick();
        _untuck ??= new DispatcherTimer(Shammy.Core.Notch.TuckAway.Away, DispatcherPriority.Background, (_, _) => ComeBack(), Dispatcher);
        _untuck.Stop();
        _untuck.Start();
        Log.Info("Notch tucked away");
    }

    void ComeBack()
    {
        _untuck?.Stop();
        if (!_tucked) return;
        _tucked = false;
        Native.SetExStyle(_hwnd, 0, WS_EX_TRANSPARENT);
        // Coming back under a pointer that is working there must not pop the notch open; it may once the pointer
        // has been somewhere else. Checked by position, so a missed mouse-leave can never keep it blocked.
        _hoverBlocked = PointerOverNotch();
        if (_hoverBlocked)
        {
            _blockCheck ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
            {
                if (PointerOverNotch() && _hoverBlocked) return;
                _hoverBlocked = false;
                _blockCheck!.Stop();
            }, Dispatcher);
            _blockCheck.Start();
        }
        if (MotionPrefs.Lively) _tuck.AnimateTo(0, 0.42, 0.85);
        else _tuck.Snap(0);
        Animator.Kick();
    }

    internal bool PointerOverNotch()
    {
        if (!Native.GetCursorPos(out var screen)) return false;
        // The window's own coordinates: the notch itself may still be part-way out of sight.
        var p = PointFromScreen(new Point(screen.X, screen.Y));
        p = new Point(p.X / _scale, p.Y / _scale);
        double w = _w.Value, h = _h.Value;
        return p.X >= Cx - w / 2 - 12 && p.X <= Cx + w / 2 + 12 && p.Y >= 0 && p.Y <= h + 8;
    }

    /// <summary>For tests: "tuck" tucks it away now, "tuck back" brings it back; both say where things stand.</summary>
    internal string TuckForTest(string arg)
    {
        if (arg.Trim() == "back") ComeBack();
        else if (arg.Trim() != "state") TuckAway();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        return $"tucked={_tucked} tucks={_tucks} away={_tuck.Target:0} tab={(_tabShown.Target > 0 ? "shown" : "hidden")} clickThrough={(ex & WS_EX_TRANSPARENT) != 0} " +
               $"hoverBlocked={_hoverBlocked} mode={_mode} enabled={_services.Settings.TuckAwayEnabled}";
    }

    /// <summary>Makes the cat purr as if the pointer had rested on it (for testing); says how often it has.</summary>
    internal string PetPurr()
    {
        _restView.Pet.Purr();
        return $"purrs={_restView.Pet.Purrs} kind={Views.Screens.PetLook.Kind} sound={Views.Screens.PetLook.Purrs && Infrastructure.NotchSound.Enabled}";
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
}
