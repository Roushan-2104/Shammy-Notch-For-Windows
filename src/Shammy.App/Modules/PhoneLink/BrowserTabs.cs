using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Reach P3: the tabs of a Chrome or Edge window (and other Chromium browsers) with their real titles, read through UI
/// Automation from the browser's own tab strip, no extension. Like <see cref="PhoneSend.BrowserAddress"/>, only the
/// browser's frame is walked, never the page (Document), with a node limit, on a background thread. A tab can be brought
/// to the front (SelectionItem) or closed (its own Close button), only when its title still matches what the phone was shown.
/// shortcut: tabs inside a collapsed tab group aren't in the strip, so they aren't listed.
/// </summary>
internal static class BrowserTabs
{
    public static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "brave", "vivaldi", "opera", "chromium",
    };

    const int MaxDepth = 14, MaxNodes = 900, MaxTabs = 100;

    public readonly record struct Tab(string Title, bool Selected);

    /// <summary>The window's tabs in strip order, or null when they can't be read (names and selection come cached with the walk).</summary>
    public static List<Tab>? Read(IntPtr hwnd) => Guard(() => Items(hwnd)?.Take(MaxTabs).Select(t =>
        new Tab(t.Cached.Name, t.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty) is true)).ToList());

    /// <summary>Selects ([close] false) or closes tab [index] if it is still titled [title]; true if it was done.</summary>
    public static bool Act(IntPtr hwnd, int index, string title, bool close) => Guard(() =>
    {
        var items = Items(hwnd);
        if (items is null || index < 0 || index >= items.Count || items[index].Current.Name != title) return false;
        var tab = items[index];
        if (!close)
        {
            if (tab.GetCurrentPattern(SelectionItemPattern.Pattern) is not SelectionItemPattern sel) return false;
            sel.Select();
            return true;
        }
        // the tab's own ✕ is a button inside it
        var button = tab.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
        if (button?.GetCurrentPattern(InvokePattern.Pattern) is not InvokePattern invoke) return false;
        invoke.Invoke();
        return true;
    });

    /// <summary>Breadth first through the frame (skipping the page): the first tab strip's tab items.</summary>
    static List<AutomationElement>? Items(IntPtr hwnd)
    {
        var root = AutomationElement.FromHandle(hwnd);
        if (root is null) return null;
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        cache.Add(AutomationElement.ControlTypeProperty);
        cache.Add(AutomationElement.NameProperty);
        cache.Add(SelectionItemPattern.IsSelectedProperty);
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        int nodes = 0;
        while (queue.Count > 0 && nodes < MaxNodes)
        {
            var (element, depth) = queue.Dequeue();
            AutomationElementCollection children;
            using (cache.Activate())
                children = element.FindAll(TreeScope.Children, Condition.TrueCondition);
            var tabs = new List<AutomationElement>();
            foreach (AutomationElement child in children)
            {
                nodes++;
                var type = child.Cached.ControlType;
                if (type == ControlType.Document) continue;
                if (type == ControlType.TabItem) tabs.Add(child);
                else if (depth + 1 < MaxDepth) queue.Enqueue((child, depth + 1));
            }
            if (tabs.Count > 0) return tabs;
        }
        return null;
    }

    static T? Guard<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException
                                       or ArgumentException or Win32Exception or TimeoutException)
        {
            Infrastructure.Log.Warn("[phonelink] Reading the browser's tabs failed", ex);
            return default;
        }
    }

    static bool Guard(Func<bool> act)
    {
        try { return act(); }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException
                                       or ArgumentException or Win32Exception or TimeoutException)
        {
            Infrastructure.Log.Warn("[phonelink] Acting on a browser tab failed", ex);
            return false;
        }
    }
}
