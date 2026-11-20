namespace Shammy.Infrastructure;

/// <summary>
/// Diagnostic log in %LOCALAPPDATA%\Shammy\logs: one file per day, kept for three days, capped in size.
/// It records what Shammy did, never what you searched, said, copied or circled.
/// </summary>
internal static class Log
{
    const long MaxBytesPerDay = 5 * 1024 * 1024;
    static readonly TimeSpan Retention = TimeSpan.FromDays(3);
    static readonly object Gate = new();
    static DateTime _day;
    static string? _file;
    static bool _capped;

    /// <summary>A warning or error that came with an exception, after it's written (Reports.ProblemWatch counts them).</summary>
    public static event Action<string, Exception>? Problem;

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null)
    {
        Write("WARN", ex is null ? message : $"{message}: {ex}");
        if (ex is not null) Problem?.Invoke(message, ex);
    }

    public static void Error(string message, Exception? ex = null)
    {
        Write("ERROR", ex is null ? message : $"{message}: {ex}");
        if (ex is not null) Problem?.Invoke(message, ex);
    }

    static void Write(string level, string message)
    {
        var now = DateTime.Now;
        var line = $"{now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                // Shammy runs for weeks from the tray, so roll over (and prune) whenever the date changes.
                if (_file is null || now.Date != _day) RollOver(now.Date);
                if (_capped) return;
                var info = new FileInfo(_file!);
                if (info.Exists && info.Length > MaxBytesPerDay)
                {
                    _capped = true;
                    File.AppendAllText(_file!, $"{now:HH:mm:ss.fff} [WARN] Log size limit reached for today; further lines dropped{Environment.NewLine}");
                    return;
                }
                File.AppendAllText(_file!, line);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
#if DEBUG
        System.Diagnostics.Debug.Write(line);
#endif
    }

    static void RollOver(DateTime day)
    {
        _day = day;
        _capped = false;
        _file = Path.Combine(Paths.Logs, $"shammy-{day:yyyyMMdd}.log");
        foreach (var old in new DirectoryInfo(Paths.Logs).GetFiles("shammy-*.log").Where(f => f.LastWriteTime < day - Retention))
        {
            try { old.Delete(); }
            catch (IOException) { }
        }
    }
}
