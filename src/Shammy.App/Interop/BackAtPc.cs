using Microsoft.Win32;

namespace Shammy.Interop;

/// <summary>
/// You are back at the PC: the session was unlocked or the PC woke from sleep. Windows may have dropped the
/// low-level hooks meanwhile, and key releases on the lock screen (Win+L, Ctrl+Alt+Del) never reached them.
/// Raised on the system events thread.
/// </summary>
internal static class BackAtPc
{
    public static event Action? Happened;

    static BackAtPc()
    {
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect) Happened?.Invoke();
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) Happened?.Invoke();
        };
    }
}
