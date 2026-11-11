using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shammy.Infrastructure;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Shammy.Notifications;

/// <summary>A toast another app posted, flattened for display.</summary>
internal sealed class ShammyNotification
{
    public required uint Id { get; init; }
    public required string AppId { get; init; }
    public required string AppName { get; init; }
    public ImageSource? Icon { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset Time { get; init; }

    // The Notifications tab's two lines, read when a row is built; the tab rebuilds its rows when
    // PrivacyState flips, so a call hides them and its end reveals them without reopening.
    public string ListTitle => Shammy.Core.Notifications.NotificationText.ForList(AppName, Title, Body, PrivacyState.Presenting).Title;
    public string ListDetail => Shammy.Core.Notifications.NotificationText.ForList(AppName, Title, Body, PrivacyState.Presenting).Detail;

    public string TimeText
    {
        get
        {
            var age = DateTimeOffset.Now - Time;
            return age.TotalMinutes < 1 ? "now"
                : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m"
                : age.TotalDays < 1 ? $"{(int)age.TotalHours}h"
                : Time.LocalDateTime.ToString("d MMM");
        }
    }
}

/// <summary>
/// Reads other apps' toasts through UserNotificationListener (the API Windows uses for wearables).
/// Arrivals come from the NotificationChanged event where Windows supports it for desktop apps, with
/// polling as a fallback and safety net, so a missed event never loses a notification.
/// </summary>
internal sealed class NotificationService : IDisposable
{
    const int HistoryLimit = 40;

    readonly Dispatcher _ui;
    readonly HashSet<uint> _known = new();
    readonly Shammy.Core.Notifications.UnreadTracker _unread = new();
    readonly Dictionary<string, ImageSource?> _icons = new();
    readonly DispatcherTimer _poll;
    UserNotificationListener? _listener;
    FileSystemWatcher? _store;
    DispatcherTimer? _storeDebounce, _storeCheck;
    (long, DateTime, long, DateTime) _storeStamp;
    bool _polling;

    public NotificationService(Dispatcher ui)
    {
        _ui = ui;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, async (_, _) => await PollAsync(), ui) { IsEnabled = false };
    }

    public event Action<ShammyNotification>? Arrived;

    public ObservableCollection<ShammyNotification> History { get; } = new();

    /// <summary>Apps the user muted (by app id): their toasts are dropped before they reach the history.</summary>
    public Func<string, bool>? IsMuted { get; set; }

    /// <summary>History entries that arrived since the Alerts view was last open.</summary>
    public int UnreadCount => _unread.CountUnread(History.Select(n => n.Id));

    /// <summary>The user has seen the whole history (the Alerts view is open).</summary>
    public void MarkAllSeen() => _unread.MarkSeen(History.Select(n => n.Id));

    /// <summary>An app was just muted: what it already posted leaves the history too.</summary>
    public void RemoveApp(string appId)
    {
        if (appId.Length == 0) return;
        foreach (var n in History.Where(n => n.AppId == appId).ToList()) History.Remove(n);
    }

    bool Muted(ShammyNotification n) => n.AppId.Length > 0 && IsMuted?.Invoke(n.AppId) == true;

    public bool IsAvailable { get; private set; }
    public string? Problem { get; private set; }

    public async Task StartAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var status = _listener.GetAccessStatus();
            if (status == UserNotificationListenerAccessStatus.Unspecified) status = await _listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                Problem = "Notification access is off (Settings › Privacy › Notifications)";
                Log.Warn($"Notification access: {status}");
                return;
            }
        }
        catch (Exception ex)
        {
            // Older Windows builds only allow this for packaged apps.
            Problem = "This Windows version only shares notifications with packaged apps";
            Log.Warn("Notification listener unavailable", ex);
            return;
        }

        IsAvailable = true;
        // Everything already in Action Center is history, not news.
        var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask().ConfigureAwait(true);
        foreach (var n in existing.OrderByDescending(n => n.CreationTime))
        {
            _known.Add(n.Id);
            if (await ConvertAsync(n) is { } h && !Muted(h)) AddToHistory(h, atEnd: true);
        }
        MarkAllSeen();

        try
        {
            _listener.NotificationChanged += (_, e) =>
            {
                if (e.ChangeKind == UserNotificationChangedKind.Added) _ui.BeginInvoke(async () => await PollAsync());
            };
            Log.Info("Notifications: listening for events");
        }
        catch (Exception)
        {
            // Desktop apps don't get this event on most Windows builds; watch the store instead (below).
        }

        // Reading the whole notification list costs real CPU (it marshals every toast in Action Center),
        // so don't poll it rapidly. Windows writes each new toast into its notification database, so a
        // change to that file is the trigger; a slow poll is only a safety net if the file isn't there.
        if (WatchStore())
        {
            _poll.Interval = TimeSpan.FromSeconds(30);
            Log.Info("Notifications: watching the notification store");
        }
        else
        {
            _poll.Interval = TimeSpan.FromSeconds(2);
            Log.Info("Notifications: polling every 2 s");
        }
        _poll.Start();
    }

    bool WatchStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
        if (!Directory.Exists(dir)) return false;
        try
        {
            // Writes come in bursts (database + WAL + SHM); coalesce them into one read.
            _storeDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, async (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                await PollAsync();
            }, _ui) { IsEnabled = false };
            _store = new FileSystemWatcher(dir, "wpndatabase.db*")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _store.Changed += (_, _) => _ui.BeginInvoke(() =>
            {
                _storeDebounce.Stop();
                _storeDebounce.Start();
            });
            // SQLite appends to the -wal file through an open handle, and Windows often holds back change
            // notifications for that until the handle is flushed or closed: toasts then showed up to 30 s late
            // (on the slow poll). Checking the files' size and time once a second costs two stats.
            _storeStamp = StoreStamp(dir);
            _storeCheck = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
            {
                var stamp = StoreStamp(dir);
                if (stamp == _storeStamp) return;
                _storeStamp = stamp;
                _storeDebounce.Stop();
                _storeDebounce.Start();
            }, _ui);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Can't watch the notification store", ex);
            return false;
        }
    }

    static (long, DateTime, long, DateTime) StoreStamp(string dir)
    {
        var db = new FileInfo(Path.Combine(dir, "wpndatabase.db"));
        var wal = new FileInfo(Path.Combine(dir, "wpndatabase.db-wal"));
        return (db.Exists ? db.Length : -1, db.Exists ? db.LastWriteTimeUtc : default,
                wal.Exists ? wal.Length : -1, wal.Exists ? wal.LastWriteTimeUtc : default);
    }

    async Task PollAsync()
    {
        if (_listener is null || _polling) return;
        _polling = true;
        try
        {
            var current = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var fresh = current.Where(n => !_known.Contains(n.Id)).OrderBy(n => n.CreationTime).ToList();
            _known.IntersectWith(current.Select(n => n.Id));
            foreach (var n in fresh)
            {
                _known.Add(n.Id);
                // A muted app's toasts never reach the history (or the pop-up).
                if (await ConvertAsync(n) is not { } h || Muted(h)) continue;
                AddToHistory(h, atEnd: false);
                Arrived?.Invoke(h);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Reading notifications failed", ex);
        }
        finally
        {
            _polling = false;
        }
    }

    async Task<ShammyNotification?> ConvertAsync(UserNotification n)
    {
        try
        {
            var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
            var texts = binding?.GetTextElements().Select(t => t.Text?.Trim() ?? "").Where(t => t.Length > 0).ToList() ?? new List<string>();
            if (texts.Count == 0) return null;

            string appId = "", appName = "Notification";
            try
            {
                appId = n.AppInfo.AppUserModelId ?? "";
                appName = n.AppInfo.DisplayInfo.DisplayName is { Length: > 0 } name ? name : appName;
            }
            catch (Exception)
            {
                // Some tray apps post toasts without registered app info; show them anyway.
            }

            // The app's logo is read without blocking: this runs on the UI thread.
            var icon = await IconForAsync(n, appId);
            return new ShammyNotification
            {
                Id = n.Id,
                AppId = appId,
                AppName = appName,
                Icon = icon,
                Title = texts[0],
                Body = string.Join("\n", texts.Skip(1)),
                Time = n.CreationTime,
            };
        }
        catch (Exception ex)
        {
            Log.Warn("Unreadable notification", ex);
            return null;
        }
    }

    async Task<ImageSource?> IconForAsync(UserNotification n, string appId)
    {
        if (_icons.TryGetValue(appId, out var cached)) return cached;
        // Desktop apps (Chrome, WhatsApp desktop installers, system toasts) have no packaged logo, but
        // the Start menu's entry for the app ID does, so ask the shell for that first.
        ImageSource? icon = appId.Length > 0 ? Shelf.ShellIcons.Get($@"shell:AppsFolder\{appId}", 64) : null;
        if (icon is not null)
        {
            _icons[appId] = icon;
            return icon;
        }
        try
        {
            var stream = await n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)).OpenReadAsync().AsTask();
            using var s = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await s.CopyToAsync(ms);
            ms.Position = 0;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 64;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            icon = bmp;
        }
        catch (Exception)
        {
            // No icon anywhere (e.g. some system toasts); the views fall back to a bell glyph.
        }
        _icons[appId] = icon;
        return icon;
    }

    void AddToHistory(ShammyNotification n, bool atEnd)
    {
        if (atEnd) History.Add(n);
        else History.Insert(0, n);
        while (History.Count > HistoryLimit) History.RemoveAt(History.Count - 1);
    }

    public void ClearHistory() => History.Clear();

    /// <summary>Test pipe: a made-up toast through the same path as a real one (history, then pop-up).</summary>
    internal void InjectForTest(ShammyNotification n)
    {
        if (Muted(n)) return;
        AddToHistory(n, atEnd: false);
        Arrived?.Invoke(n);
    }

    public void Dispose()
    {
        _poll.Stop();
        _storeDebounce?.Stop();
        _storeCheck?.Stop();
        _store?.Dispose();
    }
}
