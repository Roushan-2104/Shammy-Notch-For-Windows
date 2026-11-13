using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Interop;

namespace Shammy.Modules.Wheel;

internal partial class WheelModule
{
    // ---- The hook (its own thread: decide at once, post the rest) ---------------------------------

    bool Filter(MouseHook.Kind kind, int x, int y, int wheel)
    {
        // The setting can switch the wheel off (and null the hook) on the UI thread while this runs on the hook's
        // own: read it once, and with no hook let the click through untouched.
        var hook = Volatile.Read(ref _hook);
        if (hook is null) return false;
        long ms = _clock.ElapsedMilliseconds;
        switch (kind)
        {
            case MouseHook.Kind.MiddleDown:
            {
                var step = _gesture.Down(x, y, ms, _active);
                if (step != WheelStep.Swallow) return false;
                hook.WatchMoves = true;
                _ctx.Ui.BeginInvoke(() =>
                {
                    _holdStarts++;
                    _holdTimer.Stop();
                    _holdTimer.Interval = WheelGesture.Hold;
                    _holdTimer.Start();
                });
                return true;
            }
            case MouseHook.Kind.Move:
            {
                if (_gesture.Move(x, y) != WheelStep.ReplayDownThenPass) return false;
                hook.WatchMoves = false;
                Interlocked.Increment(ref _replays);
                ThreadPool.QueueUserWorkItem(_ => MouseHook.SendMiddle(down: true, up: false));
                return false;
            }
            case MouseHook.Kind.Wheel:
            {
                // Only while the button is still held with the dial open: then the wheel turns the dial, not the page.
                if (_gesture.Current != WheelGesture.Phase.Open) return false;
                _ctx.Ui.BeginInvoke(() => Turn(wheel));
                return true;
            }
            case MouseHook.Kind.MiddleUp:
            {
                hook.WatchMoves = false;
                hook.WatchWheel = false;
                var step = _gesture.Up(ms);
                switch (step)
                {
                    case WheelStep.SwallowAndReplayClick:
                        Interlocked.Increment(ref _replays);
                        ThreadPool.QueueUserWorkItem(_ => MouseHook.SendMiddle(down: true, up: true));
                        return true;
                    case WheelStep.SwallowAndRelease:
                        _ctx.Ui.BeginInvoke(Release);
                        return true;
                    default:
                        return false;
                }
            }
        }
        return false;
    }

    void OnHoldElapsed()
    {
        _holdTicks++;
        _holdTimer.Stop();
        if (!_gesture.HoldElapsed(_clock.ElapsedMilliseconds))
        {
            // Windows timers can fire a tick early: still held and still, so look again in a moment.
            if (_gesture.Current == WheelGesture.Phase.Pending)
            {
                _holdTimer.Interval = TimeSpan.FromMilliseconds(20);
                _holdTimer.Start();
            }
            return;
        }
        _holdTimer.Interval = WheelGesture.Hold;
        if (_hook is not null) _hook.WatchMoves = false;
        var (x, y) = _gesture.DownAt;
        Open(x, y, follow: true);
    }
}
