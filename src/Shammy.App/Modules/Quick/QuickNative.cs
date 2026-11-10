using System.Runtime.InteropServices;

namespace Shammy.Modules.Quick;

/// <summary>The three Windows calls the quick actions make: hold the PC awake, lock it, put it to sleep.</summary>
internal static class QuickNative
{
    /// <summary>The setting stays until the next call with <see cref="Continuous"/> (from the same thread).</summary>
    public const uint Continuous = 0x80000000, SystemRequired = 0x00000001, DisplayRequired = 0x00000002;

    /// <summary>
    /// Tells Windows what this thread needs kept on. It belongs to the calling thread, so it is set and cleared
    /// from the same one, and Windows drops it when that thread ends. Returns the previous state, or 0 on failure.
    /// </summary>
    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool LockWorkStation();

    /// <summary>Sleep (not hibernate), letting apps finish what they're doing; it returns once the PC is awake again.</summary>
    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force,
        [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);
}
