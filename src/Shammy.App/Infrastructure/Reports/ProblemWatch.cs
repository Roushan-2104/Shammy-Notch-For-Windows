using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using Shammy.Core.Reports;

namespace Shammy.Infrastructure.Reports;

/// <summary>
/// Notices problems so the user can choose to report them, and keeps a feature that keeps crashing Shammy switched off.
///
/// A crash: each run leaves a "running" mark that a clean exit removes. A mark found on the next start means the last
/// run ended without one; if Windows logged a crash of Shammy since then (or Shammy wrote its own crash file on the
/// way down), that's a crash report to offer, and two in one feature in a day pause it (safe mode) until the next
/// version or until the user turns it back on. A power cut or a forced restart leaves no crash, so nothing is offered.
///
/// A repeating error: every logged error with an exception is counted per problem per day (network blips aside);
/// the fifth in a day offers a report.
///
/// Files in %LOCALAPPDATA%\Shammy\reports: state.json (reports already cleaned), running, and last-crash.txt (the raw
/// exception, read and cleaned on the next start, then deleted).
/// </summary>
internal static class ProblemWatch
{
    const int LogLines = 30, StackLines = 6;

    static readonly object Gate = new();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static ReportsState _state = new();
    static string _version = "0.0.0";
    static bool _fatal, _dirty;
    [ThreadStatic] static bool _counting;

    /// <summary>A repeating error reached the day's limit: offer this report (raised on the thread that logged it).</summary>
    public static event Action<Report>? ErrorToOffer;

    /// <summary>Whether a report could be offered now (set by the reports module; any thread).</summary>
    public static Func<bool>? CanOffer { get; set; }

    static string Folder => Path.Combine(Paths.Root, "reports");
    static string StateFile => Path.Combine(Folder, "state.json");
    static string RunningFile => Path.Combine(Folder, "running");
    static string CrashFile => Path.Combine(Folder, "last-crash.txt");

    /// <summary>The Windows user and PC names: removed from everything a report holds.</summary>
    public static IReadOnlyList<string> PrivateWords { get; } = Names();

    /// <summary>
    /// The user's names, whole and word by word ("Jane Doe", "Jane", "Doe"), and the PC's name whole (its parts are
    /// words like "DESKTOP" that logs use for other things).
    /// </summary>
    static string[] Names()
    {
        var person = new[] { Environment.UserName, Path.GetFileName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) }
            .Where(w => !string.IsNullOrWhiteSpace(w)).ToList();
        return person.Concat(person.SelectMany(w => w.Split(new[] { ' ', '.', '_' }, StringSplitOptions.RemoveEmptyEntries)))
            .Append(Environment.MachineName)
            .Where(w => w.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static string Version => _version;

    /// <summary>"Windows 11 (26300)".</summary>
    public static string WindowsVersion
    {
        get
        {
            var v = Environment.OSVersion.Version;
            return $"Windows {(v.Major == 10 && v.Build >= 22000 ? 11 : v.Major)} ({v.Build})";
        }
    }

    /// <summary>First thing on start, right after the log's start line: looks at how the last run ended and marks this one.</summary>
    public static void BeginRun(string version)
    {
        _version = version;
        try
        {
            Directory.CreateDirectory(Folder);
            lock (Gate)
            {
                _state = Load();
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (File.Exists(RunningFile)) LookForCrash(today);
                foreach (var back in _state.ResumeAfterUpdate(version)) Log.Info($"Safe mode: {back} is back on in {version}");
                _state.Prune(today, DateTime.UtcNow);
                Save();
            }
        }
        catch (Exception ex)
        {
            // Whatever went wrong here, Shammy starts: a report is never worth a start. Starting over means the same
            // odd file can't stop the next start too.
            Log.Warn("Checking how Shammy last closed failed; starting the reports state over", ex);
            lock (Gate) _state = new();
            TryDelete(StateFile);
        }
        try
        {
            File.WriteAllText(RunningFile, JsonSerializer.Serialize(new Running(DateTime.UtcNow, version, Environment.ProcessId)));
            TryDelete(CrashFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Marking this run failed", ex);
        }
        Log.Problem += OnProblem;
    }

    /// <summary>A clean exit: the run's mark goes (unless this run is dying of an error), and today's counts are kept.</summary>
    public static void EndRun()
    {
        Log.Problem -= OnProblem;
        lock (Gate)
        {
            if (_dirty) Save();
        }
        if (!_fatal) TryDelete(RunningFile);
    }

    /// <summary>
    /// Shammy is going down with this exception (unhandled, or too many in a row): write it down now, while we still
    /// can, so the next start can offer it. Windows' own record is the fallback for crashes nothing can catch.
    /// </summary>
    public static void RecordFatal(Exception ex)
    {
        _fatal = true;
        try { File.WriteAllText(CrashFile, $"{DateTime.UtcNow:o}\n{ex}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static bool IsPaused(string featureKey)
    {
        lock (Gate) return _state.IsPaused(featureKey);
    }

    /// <summary>A module safe mode has switched off (it isn't started).</summary>
    public static bool IsModulePaused(string? moduleNamespace)
    {
        lock (Gate) return _state.Paused.Keys.Any(k => ProblemPlace.ModuleNamespace(k) is { } ns && Related(moduleNamespace, ns));
    }

    /// <summary>A module in this folder's namespace, or one inside it (Shammy.Modules.Productivity.Alarms), is paused with it.</summary>
    static bool Related(string? moduleNamespace, string crashNamespace) =>
        moduleNamespace is not null && (moduleNamespace == crashNamespace || moduleNamespace.StartsWith(crashNamespace + ".", StringComparison.Ordinal));

    /// <summary>Some module can be paused for a crash in this folder.</summary>
    static bool HasModule(string crashNamespace) => typeof(ProblemWatch).Assembly.GetTypes()
        .Any(t => t is { IsClass: true, IsAbstract: false } && typeof(Modules.IShammyModule).IsAssignableFrom(t) && Related(t.Namespace, crashNamespace));

    public static IReadOnlyList<(string Key, PausedFeature Feature)> Paused
    {
        get { lock (Gate) return _state.Paused.Select(p => (p.Key, p.Value)).ToList(); }
    }

    /// <summary>Turns a paused feature back on (a module after a restart, dictation at once).</summary>
    public static void Resume(string featureKey)
    {
        lock (Gate)
        {
            _state.Resume(featureKey);
            Save();
        }
        Log.Info($"Safe mode: {featureKey} turned back on");
    }

    /// <summary>The crash report waiting to be offered, and the features newly paused for it.</summary>
    public static (Report? Crash, IReadOnlyList<PausedFeature> NewlyPaused) Waiting
    {
        get
        {
            lock (Gate) return (_state.PendingCrash, _state.PauseNotices.Where(_state.Paused.ContainsKey).Select(k => _state.Paused[k]).ToList());
        }
    }

    /// <summary>The user saw (or answered) what was waiting: the crash and the safe-mode notice are done.</summary>
    public static void Seen(bool crash, bool pauseNotices)
    {
        lock (Gate)
        {
            if (crash) _state.PendingCrash = null;
            if (pauseNotices) _state.PauseNotices.Clear();
            Save();
        }
    }

    /// <summary>The user sent or declined this repeating error: asked again only if a newer version still has it.</summary>
    public static void Answered(Report report)
    {
        if (report.IsCrash) return;
        lock (Gate)
        {
            _state.Answer(report.Signature, _version);
            Save();
        }
    }

    /// <summary>Pipe diagnostics.</summary>
    public static string State()
    {
        lock (Gate)
        {
            return $"version={_version} pending={_state.PendingCrash?.Signature ?? "-"} paused=[{string.Join(",", _state.Paused.Keys)}] " +
                   $"notices={_state.PauseNotices.Count} errors={_state.Errors.Count} answered={_state.Answered.Count}";
        }
    }

    // ---- Crashes ------------------------------------------------------------------------------------------------

    static void LookForCrash(DateOnly today)
    {
        var run = ReadRunning();
        var since = run?.StartedUtc ?? DateTime.UtcNow.AddDays(-1);
        if ((OwnCrashFile(since) ?? WindowsCrash(since, run?.ProcessId ?? 0)) is not { } found)
        {
            Log.Info("The last run ended without a clean exit, and no crash was recorded (a restart or a power cut)");
            return;
        }
        var (stack, when) = found;
        var place = ProblemPlace.Find(stack.Frames);
        // The version that crashed: an update staged before the crash may have been applied on this start.
        var crashed = run?.Version ?? _version;
        string? healing = null;
        if (place.FeatureKey is { } feature
            && (ProblemPlace.ModuleNamespace(feature) is not { } ns || HasModule(ns))
            && _state.RecordCrash(feature, place.FeatureName!, DateOnly.FromDateTime(when.ToLocalTime()), crashed, DateTime.UtcNow))
        {
            healing = $"{place.FeatureName} paused on this PC (2nd crash today)";
            Log.Warn($"Safe mode: {place.FeatureName} crashed Shammy twice today; it's paused until the next update");
        }
        var report = new Report(Report.CrashKind, ProblemRules.Signature(Report.CrashKind, place, stack.Type), crashed,
            Clean(stack.ShortType), place.AreaName, Clean(place.Where), WindowsVersion, when, 1, healing,
            stack.Frames.Take(StackLines).Select(Clean).ToList(), PreviousRunLog());
        _state.PendingCrash = report;
        Log.Info($"The last run crashed: {report.Error} in {report.Area} ({report.Signature})");
    }

    static (CrashStack, DateTime)? OwnCrashFile(DateTime since)
    {
        try
        {
            if (!File.Exists(CrashFile)) return null;
            var text = File.ReadAllText(CrashFile);
            var newline = text.IndexOf('\n');
            if (newline < 0 || !DateTime.TryParse(text[..newline], null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)) return null;
            if (when < since.AddMinutes(-1)) return null;
            return CrashStack.FromText(text[(newline + 1)..]) is { } stack ? (stack, when.ToUniversalTime()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows' record of that run dying: the .NET one (with the stack) over the bare one. Matched by process id when
    /// the mark has it, so another copy of Shammy.exe (a test build) crashing meanwhile isn't taken for this one.
    /// </summary>
    static (CrashStack, DateTime)? WindowsCrash(DateTime since, int processId)
    {
        try
        {
            var query = new EventLogQuery("Application", PathType.LogName,
                "*[System[((Provider[@Name='.NET Runtime'] and EventID=1026) or (Provider[@Name='Application Error'] and EventID=1000))" +
                $" and TimeCreated[@SystemTime>='{since.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffZ}']]]") { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            (CrashStack, DateTime)? bare = null;
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    // The event's own fields, not its text: Windows words the text in the PC's language.
                    var data = record.Properties.Select(p => p.Value?.ToString() ?? "").ToList();
                    var when = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow;
                    if (record.Id == 1026)
                    {
                        // One field, the runtime's own (English) text, logged from the dying process itself.
                        var text = data.FirstOrDefault() ?? "";
                        if (!text.Contains("Shammy.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (processId != 0 && record.ProcessId is { } pid && pid != processId) continue;
                        if (CrashStack.FromRuntimeEvent(text) is { } stack) return (stack, when);
                    }
                    else if (bare is null && data.Count > 8 && data[0].Equals("Shammy.exe", StringComparison.OrdinalIgnoreCase)
                             && (processId == 0 || ParseId(data[8]) == processId))
                    {
                        // Application name, version, time stamp, module name, version, time stamp, exception code, offset, process id.
                        bare = (CrashStack.FromNativeCrash(data[3], data[6]), when);
                    }
                }
            }
            return bare;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("Reading Windows' crash records failed", ex);
            return null;
        }
    }

    /// <summary>"0x1a2b" or "6699".</summary>
    static int ParseId(string text)
    {
        text = text.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out var hex) ? hex : -1
            : int.TryParse(text, out var dec) ? dec : -1;
    }

    // ---- Repeating errors ---------------------------------------------------------------------------------------

    static void OnProblem(string message, Exception ex)
    {
        // Counting must never log its way into counting again.
        if (_counting || ProblemRules.IsNetworkBlip(ex)) return;
        _counting = true;
        try
        {
            var stack = CrashStack.FromException(ex);
            var place = ProblemPlace.Find(stack.Frames);
            var what = Clean(message);
            var signature = ProblemRules.Signature(Report.ErrorKind, place, stack.Type, what);
            var today = DateOnly.FromDateTime(DateTime.Now);
            int times;
            lock (Gate)
            {
                _dirty = true;
                if (!_state.CountError(signature, today, _version)) return;
                times = _state.TodayCount(signature, today);
                Save();
            }
            // Nobody to offer it to (offers off, no relay): don't read the log for nothing.
            if (CanOffer?.Invoke() != true) return;
            var report = new Report(Report.ErrorKind, signature, _version, $"{what} ({Clean(stack.ShortType)})", place.AreaName,
                Clean(place.Where), WindowsVersion, DateTime.UtcNow, times, null,
                stack.Frames.Take(StackLines).Select(Clean).ToList(), CurrentRunLog());
            ErrorToOffer?.Invoke(report);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Counting an error failed: {e}");
        }
        finally
        {
            _counting = false;
        }
    }

    // ---- Log lines ----------------------------------------------------------------------------------------------

    /// <summary>Yesterday's and today's log, line by line.</summary>
    static List<string> RecentLog()
    {
        // Only each file's end: a day's log can be megabytes, and a report needs the last few dozen lines.
        const int TailBytes = 256 * 1024;
        var lines = new List<string>();
        foreach (var day in new[] { DateTime.Now.AddDays(-1), DateTime.Now })
        {
            var file = Path.Combine(Paths.Logs, $"shammy-{day:yyyyMMdd}.log");
            try
            {
                if (!File.Exists(file)) continue;
                // Shared: the logger may be writing to it.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bool cut = stream.Length > TailBytes;
                if (cut) stream.Seek(-TailBytes, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                // A cut lands mid-line: that first piece isn't a whole line.
                if (cut) reader.ReadLine();
                while (reader.ReadLine() is { } line) lines.Add(line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return lines;
    }

    static IReadOnlyList<string> PreviousRunLog() => LogTail.PreviousRun(RecentLog(), LogLines).Select(CleanLine).ToList();

    static IReadOnlyList<string> CurrentRunLog() => LogTail.Last(RecentLog(), LogLines).Select(CleanLine).ToList();

    static string Clean(string text) => ReportText.Clean(text, PrivateWords);

    static string CleanLine(string line) => Clean(line.Length > 300 ? line[..300] : line);

    // ---- Files --------------------------------------------------------------------------------------------------

    sealed record Running(DateTime StartedUtc, string Version, int ProcessId);

    static Running? ReadRunning()
    {
        try { return JsonSerializer.Deserialize<Running>(File.ReadAllText(RunningFile)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    static ReportsState Load()
    {
        try
        {
            return File.Exists(StateFile) ? JsonSerializer.Deserialize<ReportsState>(File.ReadAllText(StateFile)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn("Reading the problem reports state failed; starting fresh", ex);
            return new();
        }
    }

    /// <summary>Under <see cref="Gate"/>.</summary>
    static void Save()
    {
        _dirty = false;
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = StateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, StateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not logged as a problem: that would count itself.
            System.Diagnostics.Debug.WriteLine($"Saving the problem reports state failed: {ex.Message}");
        }
    }

    static void TryDelete(string file)
    {
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
