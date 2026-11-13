using System.Runtime.InteropServices;
using Shammy.Infrastructure;

namespace Shammy.Interop;

/// <summary>
/// A low-level mouse hook on its own thread (like <see cref="KeyboardHook"/>): the filter sees the middle button,
/// pointer moves and (while the wheel is open) the mouse wheel, and decides at once whether to swallow them. It never waits; Windows drops a hook that does.
/// Events Shammy itself sends back (a held-back click returned to the app) carry <see cref="Mark"/> and pass
/// untouched, as do events injected by a sandboxed process.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEMOVE = 0x0200, WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208, WM_MOUSEWHEEL = 0x020A;

    /// <summary>The dwExtraInfo on events Shammy sends itself.</summary>
    public static readonly IntPtr Mark = new(0x5348414D); // "SHAM"

    public enum Kind { Move, MiddleDown, MiddleUp, Wheel }

    /// <summary>Return true to swallow the event. <paramref name="wheel"/> is a <see cref="Kind.Wheel"/>'s delta (positive = away from you), 0 otherwise.</summary>
    public delegate bool MouseFilter(Kind kind, int x, int y, int wheel);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public Native.POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    readonly MouseFilter _filter;
    readonly LowLevelMouseProc _proc;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new();
    uint _threadId;
    IntPtr _hook;

    /// <summary>Whether pointer moves reach the filter (only while something waits on them; moves are many).</summary>
    public volatile bool WatchMoves;

    /// <summary>Whether the vertical mouse wheel reaches the filter (only while the wheel is open, to turn it).</summary>
    public volatile bool WatchWheel;

    public MouseHook(MouseFilter filter)
    {
        _filter = filter;
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shammy mouse hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _started.Wait(2000);
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Mouse hook failed: {Marshal.GetLastWin32Error()}");
        _started.Set();
        while (Native.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            Kind? kind = msg switch
            {
                WM_MBUTTONDOWN => Kind.MiddleDown,
                WM_MBUTTONUP => Kind.MiddleUp,
                WM_MOUSEMOVE when WatchMoves => Kind.Move,
                WM_MOUSEWHEEL when WatchWheel => Kind.Wheel,
                _ => null,
            };
            if (kind is { } k)
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                const uint LLMHF_LOWER_IL_INJECTED = 0x02;
                bool ours = m.dwExtraInfo == Mark;
                if (!ours && (m.flags & LLMHF_LOWER_IL_INJECTED) == 0)
                {
                    try
                    {
                        int wheel = k == Kind.Wheel ? (short)(m.mouseData >> 16) : 0;
                        if (_filter(k, m.pt.X, m.pt.Y, wheel)) return new IntPtr(1);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Mouse filter threw", ex);
                    }
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    // ---- Sending the held-back button back to the app -------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        // MOUSEINPUT is the union's largest member, so this is the full INPUT size (40 bytes on 64-bit).
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;

    /// <summary>Sends a middle press, release, or both (a click), marked as Shammy's. Call off the hook thread.</summary>
    public static void SendMiddle(bool down, bool up)
    {
        var list = new List<INPUT>();
        if (down) list.Add(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEDOWN, dwExtraInfo = Mark } });
        if (up) list.Add(new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MIDDLEUP, dwExtraInfo = Mark } });
        if (list.Count == 0) return;
        uint sent = SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != list.Count) Log.Warn($"Returning the middle button to the app failed: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
