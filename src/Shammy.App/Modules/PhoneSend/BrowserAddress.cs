using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Shammy.Core.PhoneSend;

namespace Shammy.Modules.PhoneSend;

/// <summary>
/// The address of the page in the browser at the front, read through UI Automation from the browser's own address
/// box (Edge, Chrome, Brave, Vivaldi, Opera, Firefox). Only the browser's frame is walked (tab strip, toolbar), never
/// the page itself, so a long page costs nothing. Anything that doesn't read as a page a phone can open
/// (<see cref="PhoneText.FromAddressBar"/>) counts as no page. Runs on a background thread with a time limit.
/// </summary>
internal static class BrowserAddress
{
    static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "brave", "vivaldi", "opera", "chromium", "firefox",
    };

    const uint GW_HWNDNEXT = 2;
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80;
    const int DWMWA_CLOAKED = 14;

    // The address box sits a few levels into the frame; the walk stops well before anything large.
    const int MaxDepth = 14, MaxNodes = 900;

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    /// <summary>A page and whether its browser is the app in front (rather than a window further back).</summary>
    public readonly record struct Page(string Url, bool InFront);

    /// <summary>The page, or null (no browser window, an internal page, or it took longer than <paramref name="limit"/>).</summary>
    public static async Task<Page?> ReadAsync(TimeSpan limit)
    {
        var read = Task.Run(ReadNow);
        var done = await Task.WhenAny(read, Task.Delay(limit)).ConfigureAwait(false);
        return done == read && read.Status == TaskStatus.RanToCompletion ? read.Result : null;
    }

    /// <summary>Blocking form for the test pipe (the work itself never needs the UI thread).</summary>
    public static Page? Read(TimeSpan limit)
    {
        var read = Task.Run(ReadNow);
        try { return read.Wait(limit) ? read.Result : null; }
        catch (AggregateException) { return null; }
    }

    static Page? ReadNow()
    {
        try
        {
            var (hwnd, inFront) = FindBrowserWindow();
            if (hwnd == IntPtr.Zero) return null;
            return AddressOf(hwnd) is { } url ? new Page(url, inFront) : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException
                                       or ArgumentException or Win32Exception or TimeoutException)
        {
            Infrastructure.Log.Warn("[phonesend] Reading the browser's address failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Down the z-order from the front, past Shammy's own windows and hidden or tool windows: the first browser window,
    /// and whether it was the first ordinary window (the app in front).
    /// </summary>
    static (IntPtr Hwnd, bool InFront) FindBrowserWindow()
    {
        uint own = (uint)Environment.ProcessId;
        bool first = true;
        var hwnd = GetForegroundWindow();
        for (int i = 0; hwnd != IntPtr.Zero && i < 96; i++, hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (!Ordinary(hwnd)) continue;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == own) continue;
            if (IsBrowser(pid)) return (hwnd, first);
            first = false;
        }
        return (IntPtr.Zero, false);
    }

    static bool IsBrowser(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return Browsers.Contains(p.ProcessName);
        }
        catch (ArgumentException)
        {
            return false; // closed as we looked
        }
    }

    static bool Ordinary(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return false;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    /// <summary>
    /// Breadth first through the window's frame, skipping the page (Document) entirely: the first edit box whose value
    /// reads as a web address. Children come in one cached call per node, so the walk is a few dozen round trips.
    /// </summary>
    static string? AddressOf(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return null;
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.IsValuePatternAvailableProperty);
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        int nodes = 0;
        while (queue.Count > 0 && nodes < MaxNodes)
        {
            var (element, depth) = queue.Dequeue();
            AutomationElementCollection children;
            using (cache.Activate())
                children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement child in children)
            {
                nodes++;
                var type = child.Cached.ControlType;
                if (type == ControlType.Document) continue;
                if (type == ControlType.Edit)
                {
                    if (child.GetCachedPropertyValue(AutomationElement.IsValuePatternAvailableProperty) is true &&
                        child.GetCurrentPattern(ValuePattern.Pattern) is ValuePattern value &&
                        PhoneText.FromAddressBar(value.Current.Value) is { } url)
                        return url;
                    continue;
                }
                if (depth + 1 < MaxDepth) queue.Enqueue((child, depth + 1));
            }
        }
        return null;
    }
}
