using System.ComponentModel;
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

    // The address box sits a few levels into the frame; the walk stops well before anything large.
    const int MaxDepth = 14, MaxNodes = 900;

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
        bool first = true;
        foreach (var (hwnd, pid) in Interop.FrontWindow.OrdinaryWindows(96))
        {
            if (Interop.FrontWindow.ProcessName(pid) is { } name && Browsers.Contains(name)) return (hwnd, first);
            first = false;
        }
        return (IntPtr.Zero, false);
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
