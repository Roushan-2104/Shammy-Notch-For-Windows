using System.Runtime.InteropServices;
using Shammy.Infrastructure;

namespace Shammy.Interop;

/// <summary>
/// A low-level keyboard hook on its own thread with its own message loop. Windows silently removes a
/// hook whose callback takes longer than about a second, so the hook must never wait on the UI thread:
/// the filter decides synchronously whether to swallow a key and posts any work elsewhere.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>Return true to swallow the key so no other app sees it.</summary>
    /// <param name="time">The event's timestamp (ms); AltGr shows up as LCtrl and RAlt with the same one.</param>
    public delegate bool KeyFilter(int vk, bool down, uint time);

    // Posted to the hook thread to hook again (see BackAtPc).
    const uint WM_REHOOK = 0x8001;

    readonly KeyFilter _filter;
    readonly Native.LowLevelKeyboardProc _proc;
    readonly Thread _thread;
    readonly ManualResetEventSlim _started = new();
    uint _threadId;
    IntPtr _hook;

    public KeyboardHook(KeyFilter filter)
    {
        _filter = filter;
        _proc = HookProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Shammy keyboard hook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _started.Wait(2000);
        BackAtPc.Happened += Reinstall;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        Install();
        _started.Set();

        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_REHOOK) Install();
        }

        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
    }

    /// <summary>On the hook thread: (re)places the hook. Windows drops one silently after a slow callback, and may over sleep.</summary>
    void Install()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Error($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
    }

    void Reinstall()
    {
        if (_threadId != 0) Native.PostThreadMessage(_threadId, WM_REHOOK, IntPtr.Zero, IntPtr.Zero);
    }

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool down = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN;
            bool up = msg is Native.WM_KEYUP or Native.WM_SYSKEYUP;
            if (down || up)
            {
                var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                // Keys injected by a lower-integrity (sandboxed) process must never drive Shammy's hotkeys:
                // otherwise a sandbox could "hold Right Alt", play speech, and run voice commands here.
                const uint LLKHF_LOWER_IL_INJECTED = 0x02;
                if ((k.flags & LLKHF_LOWER_IL_INJECTED) != 0) return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
                try
                {
                    if (_filter((int)k.vkCode, down, k.time)) return new IntPtr(1);
                }
                catch (Exception ex)
                {
                    Log.Error("Keyboard filter threw", ex);
                }
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        BackAtPc.Happened -= Reinstall;
        if (_threadId != 0) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
