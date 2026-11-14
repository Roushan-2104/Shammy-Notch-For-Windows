using System.Windows.Threading;
using Shammy.Core.Clipboard;
using Shammy.Interop;

namespace Shammy.Modules.ClipboardHistory;

/// <summary>
/// Notices new screenshots two ways, both event driven: files appearing in the Screenshots folder
/// (Win+PrtScn, Snipping Tool auto-save) and the snip hotkeys (Win+Shift+S, PrtScn, seen by
/// <see cref="GlobalKeys"/>), which arm <see cref="Snip"/> so the clipboard image that follows is treated as a screenshot.
/// </summary>
internal sealed class ScreenshotWatcher : IDisposable
{
    static readonly Guid ScreenshotsFolderId = new("B7BEDE81-DF94-4682-A7D8-57A52620B86F");

    readonly Dispatcher _ui;
    readonly FileSystemWatcher? _watcher;
    readonly GlobalKeys? _keys = GlobalKeys.Current;
    readonly Dictionary<string, DateTime> _seen = new(StringComparer.OrdinalIgnoreCase);

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
        if (_keys is not null) _keys.SnipKey += OnSnipKey;
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

    /// <summary>On the UI thread: a snip hotkey arms the snip detector, Esc disarms it.</summary>
    void OnSnipKey(bool snip)
    {
        if (snip) Snip.Arm(DateTime.Now);
        else Snip.Disarm();
    }

    public void Dispose()
    {
        if (_keys is not null) _keys.SnipKey -= OnSnipKey;
        _watcher?.Dispose();
    }
}
