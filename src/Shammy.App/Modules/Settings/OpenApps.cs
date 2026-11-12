using System.Runtime.InteropServices;
using System.Text;
using Shammy.Core.Settings;
using Shammy.Infrastructure;

namespace Shammy.Modules.Settings;

/// <summary>
/// The apps with a window open right now, by plain name ("photoshop"), for Settings › Notch › Hide while using.
/// One pass over the top-level windows, off the UI thread, kept for ten seconds so rebuilding the tab (after an
/// edit, a search keystroke) costs nothing. Never Shammy itself or the shell's own windows.
/// </summary>
internal static class OpenApps
{
    static readonly TimeSpan KeepFor = TimeSpan.FromSeconds(10);
    static readonly object Gate = new();
    static IReadOnlyList<string>? _apps;
    static DateTime _at;
    static Task<IReadOnlyList<string>>? _pending;

    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "shammy", "explorer", "textinputhost", "applicationframehost", "shellexperiencehost", "searchhost", "startmenuexperiencehost" };

    /// <summary>The list from the last pass, while it is still fresh; null when a new pass is needed.</summary>
    public static IReadOnlyList<string>? Fresh
    {
        get
        {
            lock (Gate) return _apps is not null && DateTime.UtcNow - _at < KeepFor ? _apps : null;
        }
    }

    public static Task<IReadOnlyList<string>> ListAsync()
    {
        lock (Gate)
        {
            if (_apps is not null && DateTime.UtcNow - _at < KeepFor) return Task.FromResult(_apps);
            return _pending ??= Task.Run(() =>
            {
                var apps = Scan();
                lock (Gate)
                {
                    _apps = apps;
                    _at = DateTime.UtcNow;
                    _pending = null;
                }
                return apps;
            });
        }
    }

    static IReadOnlyList<string> Scan()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<uint, string>();
        uint self = (uint)Environment.ProcessId;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0 || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return true;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    if (pid == 0 || pid == self) return true;
                    if (!seen.TryGetValue(pid, out var key))
                    {
                        key = HideRules.Key(ProcessName(pid));
                        seen[pid] = key;
                    }
                    if (key.Length > 0 && !Skip.Contains(key)) names.Add(key);
                }
                catch (Exception)
                {
                    // A protected process: skip it.
                }
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Warn("Listing open apps failed", ex);
        }
        return names.ToList();
    }

    /// <summary>The exe's name without ".exe" ("" when the process can't be asked).</summary>
    static string ProcessName(uint pid)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? Path.GetFileNameWithoutExtension(path.ToString(0, size)) : "";
        }
        finally { CloseHandle(handle); }
    }

    const uint GwOwner = 4, QueryLimitedInformation = 0x1000;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
