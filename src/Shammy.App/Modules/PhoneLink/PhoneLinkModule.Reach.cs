using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Shammy.Core.Activities;
using Shammy.Core.Notch;
using Shammy.Core.PhoneLink;
using Shammy.Interop;

namespace Shammy.Modules.PhoneLink;

/// <summary>
/// Reach (24 Nov): the phone controls this laptop. The phone turns it on (after your fingerprint there) with
/// <c>reach</c> {on: true}; until then every key, text, move, click and scroll is ignored. While it's on, the notch says
/// "Phone · In control" with Stop, which ends it on both sides. It also ends when the phone says so or that connection
/// drops; input still waiting is dropped and any mouse button the phone was holding is let go.
/// Trust: the fingerprint is asked on the phone; the laptop trusts its paired phone's "on" (shortcut: no laptop-side
/// "Allow Reach" setting yet).
/// </summary>
internal sealed partial class PhoneLinkModule
{
    const string ReachActivity = "phonelink.reach";
    const int ReachPriority = 24;

    readonly ReachGate _reach = new();
    ReachInjector? _injector;
    string _lastBlocked = "";
    long _lastBlockedAt;

    /// <summary>A Reach message from the phone on connection [s] (that connection's reader thread).</summary>
    void OnReach(JsonObject m, object s)
    {
        switch (LinkSession.Text(m, "t"))
        {
            case "watch":
                // "tell me when the screen goes quiet": read-only (a ping, no input), so it works without Reach
                Screen().Quiet = m["quiet"] is JsonValue q && q.TryGetValue<bool>(out var quiet) && quiet;
                return;
            case "screen":
                // the screen picture: only while Reach is on (your fingerprint was given on the phone)
                if (_reach.On) { Screen().Mode = LinkSession.Text(m, "mode"); _ = _ctx.Ui.BeginInvoke(PostReachPill); } // "Viewing" shows at once
                return;
            case "win" or "tab":
                // a window or tab the phone was shown, while Reach is on: on the UI thread, where the lists live
                if (_reach.On) _ = _ctx.Ui.BeginInvoke(() => OnWindowAct(m));
                return;
            case not "reach":
                _reach.Admit(m, DateTime.UtcNow, input => _injector?.Send(input));
                return;
        }
        if (m["on"] is JsonValue v && v.TryGetValue<bool>(out var on) && on)
        {
            _injector ??= NewInjector();
            if (_reach.Start(s))
                _ = _ctx.Ui.BeginInvoke(() =>
                {
                    PostReachPill();
                    _ctx.Info("Reach on: the phone controls this laptop");
                    _sentFront = ""; _sentWins = ""; _sentTabs = ""; _sentIcons.Clear(); _names.Clear(); _webApps.Clear(); _webAppNames.Clear(); _sentCpu = -100; _kernel = 0;
                    SendFront();
                    _frontTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1), System.Windows.Threading.DispatcherPriority.Background,
                        (_, _) => SendLaptop(), _ctx.Ui);
                    _frontTimer.Start();
                });
        }
        else EndReach(s, tellPhone: false);
    }

    /// <summary>Ends Reach (any thread; [owner]: only if that connection started it, null: whichever).</summary>
    void EndReach(object? owner, bool tellPhone)
    {
        bool ended = _reach.End(owner, held =>
        {
            if (_screen is { } scr) scr.Mode = "off";
            _injector?.Clear();
            foreach (var b in held) _injector?.Send(new ReachButton(b, ReachPress.Up));
        });
        if (!ended) return;
        if (tellPhone) Send(new JsonObject { ["t"] = "reach", ["on"] = false });
        _ = _ctx.Ui.BeginInvoke(() =>
        {
            _frontTimer?.Stop();
            _ctx.Remove(ReachActivity);
            _ctx.Info("Reach off");
        });
    }

    System.Windows.Threading.DispatcherTimer? _frontTimer;
    string _sentFront = "";

    /// <summary>
    /// While Reach is on (UI thread, every second): the app in front and its window's title, when they change, so the
    /// phone's panel names what your keys will land in ("Chrome · YouTube"). Shammy's own windows are skipped.
    /// </summary>
    void SendFront()
    {
        if (!_reach.On) return;
        var front = FrontWindow.Ordinary;
        // the window in front's own app, by its process name ("chrome" → "Chrome"): no process lookups on title changes
        var name = Shammy.Core.CircleMemory.AppNames.Friendly(front.Process) ?? front.Process ?? "";
        // an installed web app in front says its own name ("YouTube Music", not "Chrome")
        if (front.Window != IntPtr.Zero && WebApp(front.Window) is { } web) name = web.Name;
        var key = $"{front.Process}\n{front.Title}\n{name}";
        if (key == _sentFront) return;
        _sentFront = key;
        Send(new JsonObject { ["t"] = "front", ["app"] = LinkSession.Cap(name), ["title"] = LinkSession.Cap(front.Title ?? "") });
    }

    // ---- P2 windows, P3 tabs (UI thread; tabs are read on a worker) ----

    readonly Dictionary<string, (IntPtr Hwnd, string Title)> _shownWins = new();
    readonly HashSet<string> _sentIcons = new();
    /// <summary>Each process's name and program, looked up once per Reach (a protected process falls back to the process list once).</summary>
    readonly Dictionary<uint, (string Name, string? Exe)> _names = new();
    string _sentWins = "", _sentTabs = "", _tabsTitle = "";
    IntPtr _tabsOf;
    List<BrowserTabs.Tab> _shownTabs = new();
    bool _readingTabs, _actingTab;
    int _tabsAge;

    int _loadTick, _sentCpu = -100;
    long _idle, _kernel, _user;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>How busy the laptop's processors were since the last call, 0..100 (null the first time).</summary>
    int? Load()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        long di = idle - _idle, total = (kernel - _kernel) + (user - _user); // kernel time includes idle
        bool first = _kernel == 0;
        (_idle, _kernel, _user) = (idle, kernel, user);
        return first || total <= 0 ? null : (int)Math.Round(100.0 * (total - di) / total);
    }

    readonly Dictionary<(IntPtr Hwnd, uint Pid), string?> _webApps = new();
    readonly Dictionary<string, string?> _webAppNames = new();

    /// <summary>
    /// A window's installed-web-app ID and name, or null: not a web app. Remembered per window (and process, as handles
    /// get reused); a browser's own window isn't remembered as "not a web app", as Chrome may set the ID a moment late.
    /// </summary>
    (string Id, string Name)? WebApp(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var key = (hwnd, pid);
        if (!_webApps.TryGetValue(key, out var id))
        {
            id = WindowList.WebAppId(hwnd);
            if (id is null && BrowserTabs.Browsers.Contains(NameOf(pid).Name)) return null;
            if (_webApps.Count > 200) _webApps.Clear();
            _webApps[key] = id;
        }
        if (id is null) return null;
        // the Start menu's name, asked once per app; the title fallback is read now (it changes, a song at a time)
        if (!_webAppNames.TryGetValue(id, out var startMenu)) startMenu = _webAppNames[id] = WindowList.AppName(id);
        return (id, Shammy.Core.PhoneLink.Reach.WebAppName(startMenu, id, FrontWindow.TitleOf(hwnd)));
    }

    (string Name, string? Exe) NameOf(uint pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        var exe = WindowList.ExePath(pid);
        n = (exe is not null ? Path.GetFileNameWithoutExtension(exe) : FrontWindow.ProcessName(pid) ?? "", exe);
        if (_names.Count > 200) _names.Clear();
        return _names[pid] = n;
    }

    /// <summary>
    /// Every second while Reach is on: the open windows (when the list changes; each app's icon once per Reach), and the
    /// tabs of the browser window in front (read on a worker, only when a browser is in front).
    /// </summary>
    void SendLaptop()
    {
        SendFront();
        if (!_reach.On) return;
        if (++_loadTick % 5 == 0 && Load() is int cpu && Math.Abs(cpu - _sentCpu) >= 5) { _sentCpu = cpu; Send(new JsonObject { ["t"] = "load", ["cpu"] = cpu }); }
        var wins = WindowList.Top(8);
        // the window really in front (a topmost picture-in-picture or mini window can sit above it in the stack)
        var fg = Native.GetForegroundWindow();
        var front = wins.FirstOrDefault(x => x.Hwnd == fg) ?? wins.FirstOrDefault();
        var list = new JsonArray();
        var sig = new System.Text.StringBuilder();
        _shownWins.Clear();
        foreach (var w in wins)
        {
            var id = w.Hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var (process, exe) = NameOf(w.Pid);
            var app = Shammy.Core.CircleMemory.AppNames.Friendly(process) ?? process;
            // an installed web app (YouTube Music is chrome.exe underneath): its own name and icon, not the browser's
            var web = WebApp(w.Hwnd);
            var iconKey = web is { } wa ? "app:" + wa.Id : process;
            if (web is { } named) app = named.Name;
            _shownWins[id] = (w.Hwnd, w.Title);
            list.Add(new JsonObject { ["id"] = id, ["app"] = LinkSession.Cap(app), ["title"] = LinkSession.Cap(w.Title), ["icon"] = iconKey, ["front"] = w == front });
            sig.Append(id).Append('|').Append(w.Title).Append(w == front ? "*" : "").Append('\n');
            // a program's icon is tried once; a web app's window may not have its icon yet, so it's asked until it does
            if (iconKey.Length > 0 && !_sentIcons.Contains(iconKey))
            {
                if (web is null) _sentIcons.Add(iconKey);
                if ((web is not null ? WindowList.WindowIconPng(w.Hwnd) : exe is not null ? WindowList.IconPng(exe, 96) : null) is { } png && png.Length < 40_000)
                {
                    _sentIcons.Add(iconKey);
                    Send(new JsonObject { ["t"] = "icon", ["key"] = iconKey, ["png"] = Convert.ToBase64String(png) });
                }
            }
        }
        if (sig.ToString() != _sentWins)
        {
            _sentWins = sig.ToString();
            Send(new JsonObject { ["t"] = "wins", ["list"] = list });
        }
        // the tabs of the browser in front, if one is: read when its title changes, else every 5 s (the walk is cross-process)
        bool browser = front is not null && BrowserTabs.Browsers.Contains(NameOf(front.Pid).Name) && WebApp(front.Hwnd) is null;
        if (!browser) { if (_sentTabs.Length > 0) { _sentTabs = ""; _shownTabs = new(); Send(new JsonObject { ["t"] = "tabs", ["win"] = "", ["list"] = new JsonArray() }); } return; }
        if (_readingTabs || (front!.Title == _tabsTitle && front.Hwnd == _tabsOf && ++_tabsAge < 5)) return;
        _readingTabs = true; _tabsAge = 0; _tabsTitle = front.Title;
        var hwnd = front.Hwnd;
        _ = Task.Run(() => BrowserTabs.Read(hwnd)).ContinueWith(t => _ctx.Ui.BeginInvoke(() =>
        {
            _readingTabs = false;
            if (!_reach.On || t.Status != TaskStatus.RanToCompletion || t.Result is not { } tabs) return;
            var key = hwnd + "|" + string.Join("\n", tabs.Select(x => (x.Selected ? "*" : "") + x.Title));
            if (key == _sentTabs) return;
            (_sentTabs, _tabsOf, _shownTabs) = (key, hwnd, tabs);
            // the 12 around the current tab, with where they start in the strip and how many there are
            int sel = Math.Max(0, tabs.FindIndex(x => x.Selected));
            int from = Math.Clamp(sel - 6, 0, Math.Max(0, tabs.Count - 12));
            var arr = new JsonArray();
            foreach (var x in tabs.Skip(from).Take(12)) arr.Add(new JsonObject { ["t"] = LinkSession.Cap(x.Title), ["sel"] = x.Selected });
            Send(new JsonObject { ["t"] = "tabs", ["win"] = hwnd.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["from"] = from, ["n"] = tabs.Count, ["list"] = arr });
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// The phone tapped a window (front, or close after its long press) or a tab (select or close). Only what it was last
    /// shown, and only while that window is still open (a tab: only while its title still matches).
    /// </summary>
    void OnWindowAct(JsonObject m)
    {
        if (!_reach.On) return;
        var act = LinkSession.Text(m, "act");
        if (LinkSession.Text(m, "t") == "win")
        {
            // the window the phone was shown, still open and still titled the same (a reused handle isn't the same window)
            if (!_shownWins.TryGetValue(LinkSession.Text(m, "id"), out var shown) || !WindowList.StillOpen(shown.Hwnd) || LinkSession.Text(m, "title") != shown.Title) return;
            if (act == "front") WindowList.Front(shown.Hwnd);
            else if (act == "close" && !WindowList.Close(shown.Hwnd)) Send(new JsonObject { ["t"] = "reachNote", ["why"] = "admin" });
            _sentWins = ""; // the next tick sends the new order
            return;
        }
        int i = m["i"] is JsonValue iv && iv.TryGetValue<int>(out var n) ? n : -1;
        var title = LinkSession.Text(m, "title");
        if (_tabsOf == IntPtr.Zero || i < 0 || i >= _shownTabs.Count || _shownTabs[i].Title != title || act is not ("select" or "close")) return;
        if (_actingTab) return; // one at a time: a hung browser can't pile up waiting threads
        _actingTab = true;
        var hwnd = _tabsOf;
        _ = Task.Run(() => BrowserTabs.Act(hwnd, i, title, close: act == "close")).ContinueWith(_ => _ctx.Ui.BeginInvoke(() => _actingTab = false), TaskScheduler.Default);
        _sentTabs = ""; _tabsAge = 5; // read the tabs again on the next tick
    }

    ReachScreen? _screen;

    ReachScreen Screen() => _screen ??= new ReachScreen(Send, msg => _ctx.Warn(msg), () => _reach.On);

    /// <summary>The notch while the phone is in control: "Phone · In control", or "Phone · Viewing" while the screen goes to it (UI thread).</summary>
    void PostReachPill()
    {
        if (_reach.On) _ctx.Post(new Activity(ReachActivity, ActivityKind.Custom, ReachPriority, new ReachContent(() => EndReach(null, tellPhone: true), () => _screen?.Mode is "snap" or "live")));
    }

    ReachInjector NewInjector()
    {
        var i = new ReachInjector(msg => _ctx.Warn(msg))
        {
            // a tap on the phone's picture lands on the screen that picture showed
            Screen = () => _screen is { Mode: not "off", Shown: [var r] } ? r : null,
        };
        i.Blocked += why =>
        {
            // once per reason every few seconds, not once per pointer move (injector thread only)
            long now = Environment.TickCount64;
            if (why == _lastBlocked && now - _lastBlockedAt < 5000) return;
            (_lastBlocked, _lastBlockedAt) = (why, now);
            Send(new JsonObject { ["t"] = "reachNote", ["why"] = why });
        };
        return i;
    }

    void DisposeReach()
    {
        EndReach(null, tellPhone: false);
        _injector?.Dispose();
        _screen?.Dispose();
    }

    /// <summary>The notch while the phone is in control: "Phone · In control" and a Stop circle.</summary>
    sealed class ReachContent(Action stop, Func<bool> viewing) : ICompactContent, IPetActivity
    {
        static readonly CircleSpec StopCircle = new(CircleAction.Stop, CircleTone.Neutral, true, "Stop the phone's control");

        public PetActivityState Describe(DateTime utcNow) => new()
        {
            Costume = PetCostume.Custom,
            Face = PetFace.Focused,
            Label = "Phone",
            Value = viewing() ? "Viewing" : "In control",
            Ongoing = true,
            Circle = StopCircle,
        };

        public void RunCircle(CircleAction action)
        {
            if (action == CircleAction.Stop) stop();
        }

        public double Build(ContentControl left, ContentControl right)
        {
            left.Content = Words("Phone", "Fg2");
            right.Content = Words(viewing() ? "Viewing" : "In control", "Fg");
            return 64;
        }

        static TextBlock Words(string text, string brush) => new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Ui.Tokens.Res(brush),
            VerticalAlignment = VerticalAlignment.Center,
        };

        public UIElement? BuildBubble() => null;
    }
}
