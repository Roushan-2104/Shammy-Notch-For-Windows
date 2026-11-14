using System.Globalization;
using Shammy.Core.CircleMemory;
using Shammy.Infrastructure;
using Shammy.Modules.SelectionTools;
using Shammy.Views.Cards;

namespace Shammy.Modules.CircleMemory;

/// <summary>
/// Circle memory: every circle is remembered on this PC (its words, the answer, its kind, a small picture, the app it
/// was in and when) for 30 days, encrypted, at most 500. Nothing is remembered and the Circles card hides while the
/// screen is shared. "my circles" opens the card; "what did I circle yesterday about flights" finds the best match on
/// the PC. The list loads on first use (a circle, a command, the card) and stays small: pictures stay on disk until a
/// row shows them.
/// </summary>
internal sealed class CircleMemoryModule : IShammyModule, ITextCommand, ICommandPreview, ICircleHost
{
    const string Glyph = ""; // History

    readonly CircleMemoryStore _store = new();
    ModuleContext _ctx = null!;
    CirclesCard _card = null!;
    CircleLog? _log;
    bool _paused;
    string? _filter;
    Action<CircleRecord>? _remember;

    public string Name => "circlememory";

    public void Start(ModuleContext ctx)
    {
        _ctx = ctx;
        _card = new CirclesCard(this);
        ctx.AddCard(new NotchCard
        {
            Id = "circles",
            Title = "Circles",
            Width = 176,
            Order = 85,
            Chip = true,
            // Opened on purpose (its chip, "my circles"), never pushed onto the home by itself; hidden while sharing.
            IsRelevant = () => false,
            Compact = _card.Compact,
            Detail = () =>
            {
                Fresh();
                return _card.BuildDetail();
            },
        });
        ctx.AddCommand(this);

        _remember = Remember;
        HostLinks.RememberCircle = _remember;
        HostLinks.OpenCircles = () => !PrivacyState.Presenting && ctx.OpenCard("circles");
        PrivacyState.Changed += OnPrivacy;

        ctx.AddPipeCommand("circles-add", PipeAdd);
        ctx.AddPipeCommand("circles-state", _ => PipeState());
        ctx.AddPipeCommand("circles-find", PipeFind);
        ctx.AddPipeCommand("circles-clear", _ =>
        {
            int n = Loaded().Count;
            ClearAll();
            return $"cleared {n}";
        });
        ctx.AddPipeCommand("circles-open", arg =>
        {
            if (PrivacyState.Presenting) return "hidden: presenting";
            _filter = string.IsNullOrWhiteSpace(arg) ? null : arg.Trim();
            return ctx.OpenCard("circles") ? "opened" : "error: no card";
        });
        ctx.AddPipeCommand("circles-card", _ => _card.DescribeDetail());
        ctx.AddPipeCommand("circles-pause", arg =>
        {
            Paused = !arg.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);
            return $"paused={Paused}";
        });
        // "circles-show 1": what clicking the newest row does (the island moment, or a copy without the link).
        ctx.AddPipeCommand("circles-show", arg =>
        {
            var all = Loaded().All;
            int n = int.TryParse(arg.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int k) ? k : 1;
            if (n < 1 || n > all.Count) return "error: no such circle";
            bool linked = HostLinks.ShowRemembered is not null;
            Reopen(all[n - 1]);
            return linked ? "shown" : "copied (no ShowRemembered link)";
        });
    }

    // ---- The list ------------------------------------------------------------------------------------

    /// <summary>The log, loaded (and pruned) the first time it's needed.</summary>
    CircleLog Loaded()
    {
        if (_log is not null) return _log;
        var data = _store.Load();
        _paused = data.Paused;
        _log = new CircleLog(data.Circles);
        var dropped = _log.Prune(DateTime.Now);
        _store.SweepPictures(_log.All.Where(c => c.HasPicture).Select(c => c.Id).ToList());
        if (dropped.Count > 0) Save();
        _ctx.Info($"Circle memory loaded: {_log.Count} kept, {dropped.Count} expired");
        _card.Refresh();
        return _log;
    }

    /// <summary>The log with anything past 30 days dropped (no timer prunes it; the card and commands do on use).</summary>
    CircleLog Fresh()
    {
        var log = Loaded();
        var dropped = log.Prune(DateTime.Now);
        if (dropped.Count == 0) return log;
        _store.DeletePictures(dropped.Where(c => c.HasPicture).Select(c => c.Id));
        Save();
        _card.Refresh();
        return log;
    }

    public IReadOnlyList<RememberedCircle> Circles => Loaded().All;

    public bool Paused
    {
        get
        {
            Loaded();
            return _paused;
        }
        set
        {
            Loaded();
            if (_paused == value) return;
            _paused = value;
            _ctx.Info(value ? "Circle memory paused" : "Circle memory resumed");
            Save();
            _card.Refresh();
        }
    }

    void Save() => _store.SaveInBackground(_paused, Loaded().All);

    /// <summary>Why a circle wouldn't be remembered right now, or null.</summary>
    string? Skip() => PrivacyState.Presenting ? "presenting" : Paused ? "paused" : null;

    /// <summary>The circle flow's hook: remembers a circle after its answer showed (the app in front read now).</summary>
    void Remember(CircleRecord r)
    {
        try
        {
            if (!_ctx.Ui.CheckAccess())
            {
                var app = FrontApp.Name();
                _ctx.Ui.BeginInvoke(() => Remember(r, app));
                return;
            }
            Remember(r, FrontApp.Name());
        }
        catch (Exception ex)
        {
            _ctx.Warn("Remembering a circle failed", ex);
        }
    }

    // Circles whose picture is still being kept (not yet in the list), by when they were circled, so a later answer for
    // the same circle updates it rather than adding a second row.
    readonly Dictionary<DateTime, RememberedCircle> _pending = new();

    void Remember(CircleRecord r, string? app)
    {
        if (Skip() is not null) return;
        // The same circle again (its answer arrived, or a Translate / Ask AI after the choices): one row, the latest answer.
        if (r.At != default && (_pending.TryGetValue(r.At, out var waiting) ? waiting : Loaded().All.FirstOrDefault(c => c.At == r.At)) is { } same)
        {
            if (!string.IsNullOrWhiteSpace(r.Title)) same.Title = Clip(r.Title, 300);
            if (!string.IsNullOrWhiteSpace(r.Line)) same.Line = Clip(r.Line, 1000);
            if (!string.IsNullOrWhiteSpace(r.Kind)) same.Kind = r.Kind.Trim();
            if (!_pending.ContainsKey(r.At))
            {
                Save();
                _card.Refresh();
            }
            return;
        }
        var circle = new RememberedCircle
        {
            Words = Clip(r.Words, 2000),
            Title = Clip(r.Title, 300),
            Line = string.IsNullOrWhiteSpace(r.Line) ? null : Clip(r.Line, 1000),
            Kind = string.IsNullOrWhiteSpace(r.Kind) ? CircleKinds.Text : r.Kind.Trim(),
            App = app,
            At = r.At == default ? DateTime.Now : r.At,
        };
        if (circle.Words.Length == 0 && circle.Title.Length == 0 && r.Png is null) return;
        if (r.Png is not { Length: > 0 } png)
        {
            Add(circle);
            return;
        }
        // The picture is shrunk and encrypted off the UI thread; the circle joins the list once it's on disk.
        _pending[circle.At] = circle;
        _ = Task.Run(() =>
        {
            try
            {
                if (CircleMemoryStore.Shrink(png) is { } small && _store.SavePicture(circle.Id, small)) circle.HasPicture = true;
            }
            catch (Exception ex)
            {
                Log.Warn("Keeping a circle's picture failed", ex);
            }
            _ctx.Ui.BeginInvoke(() =>
            {
                _pending.Remove(circle.At);
                Add(circle);
            });
        });
    }

    void Add(RememberedCircle circle)
    {
        // Sharing may have started (or memory been paused) while the picture was being kept.
        if (Skip() is not null)
        {
            if (circle.HasPicture) _store.DeletePictures(new[] { circle.Id });
            return;
        }
        var dropped = Loaded().Add(circle, DateTime.Now);
        _store.DeletePictures(dropped.Where(c => c.HasPicture).Select(c => c.Id));
        Save();
        _card.Refresh();
    }

    static string Clip(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    public void Delete(RememberedCircle circle)
    {
        if (Loaded().Remove(circle.Id) is null) return;
        if (circle.HasPicture) _store.DeletePictures(new[] { circle.Id });
        Save();
        _card.Refresh();
    }

    public void ClearAll()
    {
        var all = Loaded().Clear();
        _store.DeletePictures(all.Where(c => c.HasPicture).Select(c => c.Id));
        _ctx.Info($"Circle memory cleared ({all.Count})");
        Save();
        _card.Refresh();
    }

    public byte[]? Picture(RememberedCircle circle) => circle.HasPicture ? _store.LoadPicture(circle.Id) : null;

    public void Reopen(RememberedCircle circle)
    {
        if (HostLinks.ShowRemembered is { } show)
        {
            try
            {
                var app = string.IsNullOrWhiteSpace(circle.App) ? "" : $" · {circle.App}";
                show(circle.Title.Length > 0 ? circle.Title : circle.What, $"From your circles · {CircleWhen.Day(circle.At, DateTime.Now)}{app}",
                    string.IsNullOrWhiteSpace(circle.Line) ? null : circle.Line);
                return;
            }
            catch (Exception ex)
            {
                _ctx.Warn("Showing a remembered circle failed", ex);
            }
        }
        Copy(circle);
    }

    public void Copy(RememberedCircle circle)
    {
        bool ok = ClipboardText.TrySet(circle.AnswerText);
        _ctx.ShowMessage(ok ? Glyphs.Copy : Glyphs.Error, ok ? "Answer copied" : "Clipboard is busy", error: !ok);
    }

    public string? DragFile(RememberedCircle circle)
    {
        try
        {
            if (CircleMemoryStore.ToPng(Picture(circle)) is not { } png) return null;
            var root = DragRoot;
            // Dragged copies only have to outlive the drop: older ones go now.
            if (Directory.Exists(root))
                foreach (var dir in Directory.EnumerateDirectories(root))
                    if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddHours(-1))
                        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            var folder = Path.Combine(root, circle.Id);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, CircleWhen.FileName(circle.At, CardKit.H24));
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            _ctx.Warn("Making a dragged circle's picture failed", ex);
            return null;
        }
    }

    static string DragRoot => Path.Combine(Path.GetTempPath(), "Shammy circles");

    public void HoldOpen(bool on) => _ctx.Notch.ExpandedView.HoldOpen(on);

    public string? TakeFilter()
    {
        var f = _filter;
        _filter = null;
        return f;
    }

    void OnPrivacy(bool presenting) => _ctx.Ui.BeginInvoke(() =>
    {
        // An open card hides itself; the tile follows here.
        if (_log is not null) _card.Refresh();
    });

    // ---- Commands ------------------------------------------------------------------------------------

    public bool CanHandle(string text) => CircleSearch.ParseCommand(text, DateTime.Now) is not null;

    public CommandPreview? Preview(string text)
    {
        var q = CircleSearch.ParseCommand(text, DateTime.Now);
        if (q is null) return null;
        if (PrivacyState.Presenting) return new CommandPreview(Glyph, "Circles are hidden", "while your screen is shared");
        return q.Ask == CircleAsk.Open ? new CommandPreview(Glyph, "Open Circles") : new CommandPreview(Glyph, "Find in your circles", "On this PC");
    }

    public Task<CommandResult> RunAsync(string text)
    {
        var now = DateTime.Now;
        var q = CircleSearch.ParseCommand(text, now);
        if (q is null) return Task.FromResult(new CommandResult(Glyph, "Not about your circles", null, IsError: true));
        if (PrivacyState.Presenting)
            return Task.FromResult(new CommandResult(Glyph, "Circles are hidden", "While your screen is shared, nothing is shown or remembered"));

        var log = Fresh();
        if (q.Ask == CircleAsk.Open)
        {
            _ctx.OpenCard("circles");
            return Task.FromResult(new CommandResult(Glyph, "Circles", $"{log.Count} in the last {CircleLog.KeepDays} days" + (_paused ? " · paused" : "")));
        }

        var found = CircleSearch.Find(log.All, q);
        if (found.Count == 0)
        {
            var detail = log.Count == 0 ? "Nothing is remembered yet" : "Nothing you circled matches · say “my circles” to see them all";
            return Task.FromResult(new CommandResult(Glyph, "No circle found", detail));
        }
        var best = found[0];
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(best.Line)) parts.Add(best.Line!.Trim());
        parts.Add(CircleWhen.Sentence(best, now, CardKit.H24));
        if (found.Count > 1) parts.Add(found.Count == 2 ? "1 more in Circles" : $"{found.Count - 1} more in Circles");
        var title = best.Title.Length > 0 ? best.Title : best.What;
        return Task.FromResult(new CommandResult(Glyph, title, string.Join(" · ", parts)) { CopyText = best.AnswerText });
    }

    // ---- Test pipes ----------------------------------------------------------------------------------

    /// <summary>"circles-add what|answer|kind|app": a circle without a picture, through the same pause and sharing rules.</summary>
    string PipeAdd(string arg)
    {
        var p = arg.Split('|');
        if (p.Length < 2 || p[0].Trim().Length + p[1].Trim().Length == 0) return "error: circles-add what|answer|kind|app";
        if (Skip() is { } why) return $"skipped: {why}";
        var circle = new RememberedCircle
        {
            Words = Clip(p[0], 2000),
            Title = Clip(p[1], 300),
            Kind = p.Length > 2 && p[2].Trim().Length > 0 ? p[2].Trim() : CircleKinds.Text,
            App = p.Length > 3 && p[3].Trim().Length > 0 ? p[3].Trim() : null,
            At = DateTime.Now,
        };
        Add(circle);
        return $"added count={Loaded().Count}";
    }

    string PipeState()
    {
        var all = Fresh().All;
        var newest = string.Join(" | ", all.Take(3).Select(c => $"{c.What} → {c.Answer}"));
        return $"count={all.Count} paused={_paused} presenting={PrivacyState.Presenting} remember={(HostLinks.RememberCircle == _remember ? "linked" : "unlinked")} " +
               $"show={(HostLinks.ShowRemembered is null ? "unlinked" : "linked")} newest=[{newest}]";
    }

    string PipeFind(string words)
    {
        var now = DateTime.Now;
        var q = CircleSearch.ParseCommand(words, now) ?? CircleSearch.ParseFilter(words, now);
        if (q.IsEmpty) return q.Ask == CircleAsk.Open ? "open card" : "error: no words";
        var found = CircleSearch.Find(Loaded().All, q);
        if (found.Count == 0) return "none";
        var top = found.Take(3).Select(c =>
            $"{c.What} → {c.Answer} ({CircleWhen.Chip(c.At, now, CardKit.H24)}{(string.IsNullOrWhiteSpace(c.App) ? "" : ", " + c.App)})");
        return $"{found.Count} found: {string.Join(" | ", top)}";
    }

    public void Dispose()
    {
        PrivacyState.Changed -= OnPrivacy;
        if (HostLinks.RememberCircle == _remember) HostLinks.RememberCircle = null;
        HostLinks.OpenCircles = null;
        // Pictures dragged out today were copies for the drop; the kept (encrypted) ones stay.
        try
        {
            if (Directory.Exists(DragRoot)) Directory.Delete(DragRoot, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Tidying dragged circle pictures failed", ex);
        }
    }
}
