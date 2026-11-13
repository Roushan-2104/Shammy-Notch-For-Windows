using System.Diagnostics;
using System.Windows.Threading;
using Shammy.Core.Wheel;
using Shammy.Infrastructure;
using Shammy.Interop;

namespace Shammy.Modules.Wheel;

internal partial class WheelModule
{
    // ---- Pipes --------------------------------------------------------------------------------------

    string Describe() => string.Join(" | ", _slots.Select(s => s.ActionId + (s.Pinned ? "*" : "")));

    string State() =>
        $"enabled={_ctx.Settings.WheelEnabled} active={_active} hook={(_hook?.IsInstalled == true ? "on" : "off")} gesture={_gesture.Current} " +
        $"opens={_opens} replays={_replays} runs={_runs} turns={_turns} holds={_holdStarts}/{_holdTicks} last_run={_lastRun} slots=[{Describe()}] " +
        $"follow_moment={_store.FollowMoment} click={_store.ClickSound} forced={(_forcedMoment is { } f ? WheelMoments.Name(f) : "no")} " +
        $"moment={WheelMoments.Name(_moment)} ({_momentWhy}) shown=[{string.Join(" | ", _shown)}] dial={_dial.Selected} " +
        (_window is null ? "window=none" : _window.Describe());
}
