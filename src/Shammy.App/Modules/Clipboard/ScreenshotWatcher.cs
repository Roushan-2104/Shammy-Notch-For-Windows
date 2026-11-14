using System.Windows.Threading;
using Shammy.Core.Clipboard;
using Shammy.Interop;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>
/// Notices new screenshots two ways, both event driven: files appearing in the Screenshots folder
/// (Win+PrtScn, Snipping Tool auto-save) and the snip hotkeys (Win+Shift+S, PrtScn), which arm
/// <see cref="Snip"/> so the clipboard image that follows is treated as a screenshot.
/// </summary>
internal sealed class ScreenshotWatcher : IDisposable
{
    static readonly Guid ScreenshotsFolderId = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
    const int VK_SHIFT = 0x10, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    const int VK_S = 0x53, VK_SNAPSHOT = 0x2C, VK_ESCAPE = 0x1B;

    readonly Dispatcher _ui;
    readonly FileSystemWatcher? _watcher;
    readonly KeyboardHook _keys;
    readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);
    volatile bool _win, _shift;

    public ScreenshotWatcher(Dispatcher ui, Action<string, Exception?> warn)
    {
        _ui = ui;
        Folder = ResolveFolder();
        if (Directory.Exists(Folder))
        {
            try
            {
                _watcher = new FileSystemWatcher(Folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
                _watcher.Created += (_, e) => Queue(e.FullPath);
                // Some capture tools write a temp name and rename it when done.
                _watcher.Renamed += (_, e) => Queue(e.FullPath);
                _watcher.Error += (_, e) => warn("Screenshots watcher error", e.GetException());
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                warn($"Can't watch {Folder}", ex);
                _watcher = null;
            }
        }
        else
        {
            warn($"Screenshots folder {Folder} doesn't exist yet; only clipboard snips will be caught", null);
        }
        _keys = new KeyboardHook(OnKey);
    }

    public string Folder { get; }
    public bool IsWatching => _watcher is not null;
    public SnipDetector Snip { get; } = new();

    /// <summary>A new image file appeared in the Screenshots folder (raised on the UI thread, once per path).</summary>
    public event Action<string>? FileArrived;

    internal static string ResolveFolder()
    {
        var known = ClipNative.KnownFolder(ScreenshotsFolderId);
        if (!string.IsNullOrEmpty(known)) return known;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    public static bool IsImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg";

    void Queue(string path)
    {
        if (!IsImage(path)) return;
        _ui.BeginInvoke(() =>
        {
            var now = DateTime.Now;
            foreach (var old in _seen.Where(kv => now - kv.Value > TimeSpan.FromSeconds(30)).Select(kv => kv.Key).ToList())
                _seen.Remove(old);
            if (!_seen.TryAdd(path, now)) return;
            FileArrived?.Invoke(path);
        });
    }

    /// <summary>Runs on the hook thread for every key: only flips flags, never blocks, never swallows.</summary>
    bool OnKey(int vk, bool down, uint time)
    {
        switch (vk)
        {
            case VK_LWIN or VK_RWIN:
                _win = down;
                break;
            case VK_SHIFT or VK_LSHIFT or VK_RSHIFT:
                _shift = down;
                break;
            case VK_S when down && _win && _shift:
            case VK_SNAPSHOT when down:
                _ui.BeginInvoke(() => Snip.Arm(DateTime.Now));
                break;
            case VK_ESCAPE when down:
                _ui.BeginInvoke(Snip.Disarm);
                break;
        }
        return false;
    }

    public void Dispose()
    {
        _keys.Dispose();
        _watcher?.Dispose();
    }
}
