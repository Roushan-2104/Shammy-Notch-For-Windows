using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using Shammy.Infrastructure;

namespace Shammy.Shelf;

internal sealed class ShelfItem : INotifyPropertyChanged
{
    ImageSource? _icon;
    bool _requested;

    public ShelfItem(string path) => Path = path;

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;

    /// <summary>
    /// Null until the thumbnail has been made off the UI thread; extracting hundreds of thumbnails (or one on
    /// a slow network share) inline used to freeze the notch.
    /// </summary>
    public ImageSource? Icon
    {
        get
        {
            if (_icon is null && !_requested)
            {
                _requested = true;
                IconLoader.Request(this);
            }
            return _icon;
        }
    }

    internal void SetIcon(ImageSource? icon)
    {
        _icon = icon;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
    }

    string? _details;
    bool _detailsRequested;

    /// <summary>
    /// The tile's "2.4 MB · Word" line. Just the type until the size has been read off the UI thread, for the
    /// same reason as <see cref="Icon"/>: a file on an offline share can take a network timeout to answer.
    /// </summary>
    public string Details
    {
        get
        {
            if (!_detailsRequested)
            {
                _detailsRequested = true;
                var ui = Dispatcher.CurrentDispatcher;
                _ = Task.Run(() =>
                {
                    var d = Describe(Path);
                    ui.BeginInvoke(() =>
                    {
                        _details = d;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details)));
                    });
                });
            }
            return _details ?? Core.Notch.FileKinds.Describe(Path);
        }
    }

    static string Describe(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path)) return "Folder";
            long bytes = new System.IO.FileInfo(path).Length;
            return $"{Views.CompactView.FormatBytes(bytes)} · {Core.Notch.FileKinds.Describe(path)}";
        }
        catch (Exception)
        {
            return Core.Notch.FileKinds.Describe(path);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One background STA thread (shell thumbnail handlers expect one) working through icon requests.</summary>
internal static class IconLoader
{
    static readonly BlockingCollection<ShelfItem> Queue = new();
    static Dispatcher? _ui;

    public static void Request(ShelfItem item)
    {
        _ui ??= Dispatcher.CurrentDispatcher;
        EnsureThread();
        Queue.Add(item);
    }

    static Thread? _thread;

    static void EnsureThread()
    {
        if (_thread is not null) return;
        _thread = new Thread(() =>
        {
            foreach (var item in Queue.GetConsumingEnumerable())
            {
                // ShellIcons freezes what it returns, so the UI thread may use it.
                var icon = ShellIcons.Get(item.Path, 96);
                _ui?.BeginInvoke(() => item.SetIcon(icon));
            }
        })
        { IsBackground = true, Name = "Shammy shelf icons", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }
}

/// <summary>
/// Files parked on the notch. Items are references to the original files, not copies, and survive
/// restarts. Entries whose file has since been deleted are dropped shortly after start.
/// </summary>
internal sealed class ShelfStore
{
    readonly AppSettings _settings;

    public ShelfStore(AppSettings settings)
    {
        _settings = settings;
        foreach (var p in settings.Shelf) Items.Add(new ShelfItem(p));
        // Checking existence can block for a network timeout (a file on an offline share), so it mustn't
        // hold up startup on the UI thread.
        var ui = Dispatcher.CurrentDispatcher;
        var paths = settings.Shelf.ToList();
        _ = Task.Run(() =>
        {
            var gone = paths.Where(p => !Exists(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (gone.Count == 0) return;
            ui.BeginInvoke(() =>
            {
                foreach (var item in Items.Where(i => gone.Contains(i.Path)).ToList()) Items.Remove(item);
                Persist();
            });
        });
    }

    public ObservableCollection<ShelfItem> Items { get; } = new();

    public int Add(IEnumerable<string> paths)
    {
        int added = 0;
        var known = new HashSet<string>(Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            // Local paths are checked (instant); network paths aren't, since a dropped file exists by
            // definition and an offline share would stall the UI. A set keeps big drops linear.
            if (!p.StartsWith(@"\\", StringComparison.Ordinal) && !Exists(p)) continue;
            if (!known.Add(p)) continue;
            Items.Add(new ShelfItem(p));
            added++;
        }
        if (added > 0) Persist();
        return added;
    }

    public void Remove(ShelfItem item)
    {
        if (Items.Remove(item)) Persist();
    }

    /// <summary>Takes a file that was just deleted off the shelf, if it was on it.</summary>
    public void Remove(string path)
    {
        if (Items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item) Remove(item);
    }

    /// <summary>Opens the file's folder in Explorer with the file selected; false when Explorer couldn't start.</summary>
    public static bool ShowInFolder(string path)
    {
        try
        {
            using var explorer = System.Diagnostics.Process.Start(Interop.Native.ExplorerPath, $"/select,\"{path}\"");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Showing a file in its folder failed", ex);
            return false;
        }
    }

    public void Clear()
    {
        Items.Clear();
        Persist();
    }

    void Persist()
    {
        _settings.Shelf = Items.Select(i => i.Path).ToList();
        _settings.Save();
    }

    static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);
}
