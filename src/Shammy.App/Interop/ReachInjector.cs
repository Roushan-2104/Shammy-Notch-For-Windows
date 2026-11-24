using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Shammy.Core.PhoneLink;
using static Shammy.Modules.Launcher.SnippetNative;

namespace Shammy.Interop;

/// <summary>
/// Turns Reach input from the phone into real keys, typing, pointer moves and scrolls, on its own thread so the link's
/// reader and the UI never wait on SendInput. Everything it sends carries <see cref="MouseHook.Mark"/>, so Shammy's own
/// hooks let it through untouched. Windows refuses injected input into admin windows and the lock screen (and says
/// nothing); <see cref="Blocked"/> reports that so the phone can say so instead.
/// </summary>
internal sealed class ReachInjector : IDisposable
{
    const uint INPUT_MOUSE = 0, KEYEVENTF_EXTENDEDKEY = 0x1;
    const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10, MIDDLEDOWN = 0x20, MIDDLEUP = 0x40;
    const uint WHEEL = 0x800, HWHEEL = 0x1000, VIRTUALDESK = 0x4000, ABSOLUTE = 0x8000;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    readonly BlockingCollection<ReachInput> _queue = new(boundedCapacity: 512);
    readonly Thread _thread;
    readonly Action<string> _warn;

    /// <summary>Raised on the injector's thread with "admin" or "locked" when input can't reach the window in front.</summary>
    public event Action<string>? Blocked;

    public ReachInjector(Action<string> warn)
    {
        _warn = warn;
        _thread = new Thread(Run) { IsBackground = true, Name = "Reach input" };
        _thread.Start();
    }

    /// <summary>Queues input; drops it when the queue is full (the phone is far ahead of Windows) or after shutdown.</summary>
    public void Send(ReachInput input)
    {
        try { _queue.TryAdd(input); }
        catch (InvalidOperationException) { } // disposed while a reader was still delivering
    }

    /// <summary>Drops everything not yet sent (Reach ended: nothing more may land after Stop).</summary>
    public void Clear()
    {
        while (_queue.TryTake(out _)) { }
    }

    void Run()
    {
        try
        {
            foreach (var input in _queue.GetConsumingEnumerable())
            {
                try
                {
                    // a button let go always goes (Windows drops it if it must), so a drag can't end held down
                    if (input is not ReachButton { Press: ReachPress.Up } && WhyBlocked() is { } why) { Blocked?.Invoke(why); continue; }
                    Inject(input);
                }
                catch (Exception ex) { _warn($"Reach input failed: {ex.GetType().Name}"); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    void Inject(ReachInput input)
    {
        var list = new List<INPUT>();
        switch (input)
        {
            case ReachKey k:
                foreach (var m in k.Mods) list.Add(KeyEvent(m, up: false));
                list.Add(KeyEvent(k.Vk, up: false));
                list.Add(KeyEvent(k.Vk, up: true));
                for (int i = k.Mods.Count - 1; i >= 0; i--) list.Add(KeyEvent(k.Mods[i], up: true));
                break;
            case ReachText t:
                foreach (var c in t.Text)
                {
                    if (c is '\n' or '\t') { list.Add(KeyEvent(c == '\n' ? (ushort)VK_RETURN : (ushort)VK_TAB, false)); list.Add(KeyEvent(c == '\n' ? (ushort)VK_RETURN : (ushort)VK_TAB, true)); }
                    else { list.Add(Marked(Unicode(c, false))); list.Add(Marked(Unicode(c, true))); }
                }
                break;
            case ReachMove mv:
                list.Add(MoveTo(mv.Dx, mv.Dy));
                break;
            case ReachWheel w:
                if (w.Vertical != 0) list.Add(Mouse(WHEEL, w.Vertical));
                if (w.Horizontal != 0) list.Add(Mouse(HWHEEL, w.Horizontal));
                break;
            case ReachButton b:
                var (down, up) = b.Button switch { ReachMouse.Right => (RIGHTDOWN, RIGHTUP), ReachMouse.Middle => (MIDDLEDOWN, MIDDLEUP), _ => (LEFTDOWN, LEFTUP) };
                int clicks = b.Press == ReachPress.Double ? 2 : 1;
                for (int i = 0; i < clicks; i++)
                {
                    if (b.Press != ReachPress.Up) list.Add(Mouse(down, 0));
                    if (b.Press != ReachPress.Down) list.Add(Mouse(up, 0));
                }
                break;
        }
        // typed text can be thousands of events: in pieces, so other input isn't held up behind one call
        for (int i = 0; i < list.Count; i += 128)
        {
            var part = list.GetRange(i, Math.Min(128, list.Count - i)).ToArray();
            if (SendInput((uint)part.Length, part, Marshal.SizeOf<INPUT>()) != part.Length)
            {
                _warn($"Reach: Windows took only part of the input ({Marshal.GetLastWin32Error()})");
                // never leave a modifier down
                if (input is ReachKey k && k.Mods.Count > 0)
                    SendInput((uint)k.Mods.Count, k.Mods.Select(m => KeyEvent(m, up: true)).ToArray(), Marshal.SizeOf<INPUT>());
                return;
            }
        }
    }

    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

    static INPUT KeyEvent(ushort vk, bool up)
    {
        var e = Key(vk, up);
        e.u.ki.wScan = (ushort)MapVirtualKey(vk, 0); // games, VMs and remote desktops read the scan code
        if (Reach.Extended.Contains(vk)) e.u.ki.dwFlags |= KEYEVENTF_EXTENDEDKEY;
        return Marked(e);
    }

    static INPUT Marked(INPUT e)
    {
        e.u.ki.dwExtraInfo = MouseHook.Mark;
        return e;
    }

    static INPUT Mouse(uint flags, int data) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = unchecked((uint)data), dwExtraInfo = MouseHook.Mark } },
    };

    /// <summary>
    /// A move as an absolute position over all screens: the phone already shaped the movement (its own acceleration), so
    /// Windows' pointer precision mustn't bend it a second time. Physical pixels, since Shammy is per-monitor DPI aware.
    /// </summary>
    static INPUT MoveTo(int dx, int dy)
    {
        Native.GetCursorPos(out var p);
        return new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion { mi = new MOUSEINPUT
            {
                dx = Reach.Absolute(p.X + dx, GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN)),
                dy = Reach.Absolute(p.Y + dy, GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)),
                dwFlags = MOVE | ABSOLUTE | VIRTUALDESK, dwExtraInfo = MouseHook.Mark,
            } },
        };
    }

    // ---- Where Windows won't let input go ---------------------------------------------------------

    IntPtr _checkedWindow;
    string? _checkedWhy;
    long _checkedAt;

    /// <summary>"locked" on the lock screen or a UAC prompt, "admin" when the window in front runs as administrator (and
    /// Shammy doesn't), else null. Checked at most every second per window, so a stream of moves stays cheap.</summary>
    string? WhyBlocked()
    {
        var fg = Native.GetForegroundWindow();
        long now = Environment.TickCount64;
        if (fg == _checkedWindow && now - _checkedAt < 1000) return _checkedWhy;
        _checkedWindow = fg;
        _checkedAt = now;
        _checkedWhy = !OnNormalDesktop() ? "locked" : fg != IntPtr.Zero && !OwnElevated && IsElevated(fg) ? "admin" : null;
        return _checkedWhy;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformation(IntPtr obj, int index, char[] info, int length, out int needed);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int length, out int returned);

    static readonly bool OwnElevated = Elevated(GetCurrentProcess());

    /// <summary>The desktop taking input is the normal one (not the lock screen or a UAC prompt, where nothing gets in).</summary>
    static bool OnNormalDesktop()
    {
        var d = OpenInputDesktop(0, false, 0x0001 /* DESKTOP_READOBJECTS */);
        if (d == IntPtr.Zero) return false; // the secure desktop can't even be opened
        try
        {
            var name = new char[64];
            return !GetUserObjectInformation(d, 2 /* UOI_NAME */, name, name.Length * 2, out _)
                || new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(d); }
    }

    static bool IsElevated(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid == 0) return false;
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return false; // can't tell: try, and let Windows decide
        try { return Elevated(h); } finally { CloseHandle(h); }
    }

    static bool Elevated(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token)) return false;
        try { return GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, 4, out _) && elevated != 0; }
        finally { CloseHandle(token); }
    }

    public void Dispose() => _queue.CompleteAdding();
}
