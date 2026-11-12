using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Quick;

/// <summary>One drive: its letter or name, free and total bytes.</summary>
public sealed record DriveSpace(string Name, long Free, long Total)
{
    public double UsedFraction => Total <= 0 ? 0 : Math.Clamp(1 - Free / (double)Total, 0, 1);

    /// <summary>Under 10 % or under 15 GB free.</summary>
    public bool Low => Total > 0 && (Free < Total / 10 || Free < 15L << 30);
}

/// <summary>Battery wear: what it holds now against what it held new (mWh).</summary>
public sealed record BatteryWear(long DesignMWh, long FullMWh)
{
    public int Percent => DesignMWh <= 0 ? 0 : (int)Math.Round(Math.Clamp(FullMWh / (double)DesignMWh, 0, 1.2) * 100);
}

/// <summary>What the daily check found.</summary>
public sealed record HealthReport(DriveSpace? System, TimeSpan Uptime, int StartupApps, BatteryWear? Battery, bool RestartForUpdates);

public static class HealthText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"Restarted today", "Restarted 1 day ago", "Restarted 9 days ago".</summary>
    public static string Uptime(TimeSpan up) => (int)up.TotalDays switch
    {
        0 => "Restarted today",
        1 => "Restarted 1 day ago",
        var d => $"Restarted {d.ToString(Inv)} days ago",
    };

    /// <summary>The one line of the daily check, most useful first: "11 GB free · restart to finish updates · battery 86%".</summary>
    public static string Line(HealthReport r)
    {
        var parts = new List<string>();
        if (r.System is { } d) parts.Add($"{StorageText.Size(d.Free)} free");
        if (r.RestartForUpdates) parts.Add("restart to finish updates");
        else if (r.Uptime.TotalDays >= 7) parts.Add(Uptime(r.Uptime).ToLowerInvariant());
        if (r.Battery is { } b) parts.Add($"battery {b.Percent.ToString(Inv)}%");
        parts.Add($"{r.StartupApps.ToString(Inv)} apps at startup");
        return "Health · " + string.Join(" · ", parts);
    }

    /// <summary>A battery holding less than this share of what it held new counts as worn.</summary>
    public const int WornBelow = 70;

    /// <summary>
    /// Whether something deserves attention (shown every day, higher in the right-now line): a nearly full drive,
    /// updates waiting on a restart, or battery wear that is news (<paramref name="batteryNews"/>, see
    /// <see cref="BatteryNews"/>). Wear is permanent, so it is told once, not every day. A long time since the last
    /// restart never counts: with Fast Startup a shut-down PC doesn't restart, so it would nag every day.
    /// </summary>
    public static bool NeedsAttention(HealthReport r, bool batteryNews = true) =>
        r.System is { Low: true } || r.RestartForUpdates || batteryNews && r.Battery is { Percent: < WornBelow };

    /// <summary>
    /// Whether today's wear reading is worth telling: worn and never told before, or 10 points worse than when it
    /// was last told (<paramref name="toldAt"/>).
    /// </summary>
    public static bool BatteryNews(int percent, int? toldAt) =>
        percent < WornBelow && (toldAt is not { } told || percent <= told - 10);

    /// <summary>Long enough since the last restart that the Health card offers one.</summary>
    public static bool LongUptime(TimeSpan up) => up.TotalDays >= 7;

    /// <summary>The daily line can be hidden for a week; true while it still is.</summary>
    public static bool Hidden(DateTime now, DateTime? hiddenUntil) => hiddenUntil is { } until && now < until;

    /// <summary>How long "hide for a week" hides the line.</summary>
    public static readonly TimeSpan HideFor = TimeSpan.FromDays(7);

    /// <summary>Design and full-charge capacity from <c>powercfg /batteryreport /xml</c>; null when there is no battery.</summary>
    public static BatteryWear? ParseBatteryReport(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        long Read(string tag) =>
            Regex.Match(xml, $"<{tag}>(\\d+)</{tag}>") is { Success: true } m && long.TryParse(m.Groups[1].Value, NumberStyles.None, Inv, out var v) ? v : 0;
        long design = Read("DesignCapacity"), full = Read("FullChargeCapacity");
        return design > 0 && full > 0 ? new BatteryWear(design, full) : null;
    }
}

/// <summary>Cleanup choices and their wording.</summary>
public enum CleanupKind { TempFiles, RecycleBin, OldDownloads }

public static class Cleanup
{
    /// <summary>Downloads untouched for this long count as old.</summary>
    public static readonly TimeSpan OldAfter = TimeSpan.FromDays(90);

    /// <summary>Temporary files newer than this are left alone: a running app may still need them (Windows' own cleanup waits a week too).</summary>
    public static readonly TimeSpan TempOldAfter = TimeSpan.FromDays(7);

    public static string Name(CleanupKind kind) => kind switch
    {
        CleanupKind.TempFiles => "Temporary files",
        CleanupKind.RecycleBin => "Recycle Bin",
        _ => "Old downloads",
    };

    public static string Explain(CleanupKind kind) => kind switch
    {
        CleanupKind.TempFiles => "Left behind by apps and installers; anything from this week or in use is skipped",
        CleanupKind.RecycleBin => "Emptied for good",
        _ => "Downloaded over 3 months ago; each file or folder moves to the Recycle Bin whole, so you can still get it back",
    };

    public static string Verb(CleanupKind kind) => kind switch
    {
        CleanupKind.RecycleBin => "Empty",
        CleanupKind.OldDownloads => "Move",
        _ => "Clean",
    };

    /// <summary>"1 file", "12 files"; downloads count whole items (a folder is one): "1 item", "3 items".</summary>
    public static string Count(CleanupKind kind, int n)
    {
        var word = kind == CleanupKind.OldDownloads ? "item" : "file";
        return $"{n.ToString(CultureInfo.InvariantCulture)} {word}{(n == 1 ? "" : "s")}";
    }

    /// <summary>
    /// When something in Downloads arrived: the later of its creation and its last write. Last access is not used:
    /// antivirus scans and the search indexer touch it without anyone opening the file.
    /// </summary>
    public static DateTime Arrived(DateTime created, DateTime written) => created > written ? created : written;

    /// <summary>
    /// A top-level item in Downloads is old when everything in it arrived before <paramref name="cutoff"/>: a
    /// folder with one new file in it stays, whole.
    /// </summary>
    public static bool IsOld(IEnumerable<(DateTime Created, DateTime Written)> stamps, DateTime cutoff)
    {
        bool any = false;
        foreach (var (created, written) in stamps)
        {
            any = true;
            if (Arrived(created, written) >= cutoff) return false;
        }
        return any;
    }

    /// <summary>
    /// What a cleanup did, said honestly: "Freed 2.1 GB", "Moved 1.2 GB to the Recycle Bin · 2 couldn’t move",
    /// "Couldn’t move them": never "Moved 0 B" after a move that failed.
    /// </summary>
    public static string Said(CleanupKind kind, CleanupOutcome o)
    {
        var size = StorageText.Size(o.Bytes);
        switch (kind)
        {
            case CleanupKind.OldDownloads:
                if (o.Done > 0)
                    return $"Moved {size} to the Recycle Bin" + (o.Failed > 0 ? $" · {o.Failed.ToString(CultureInfo.InvariantCulture)} couldn’t move" : "");
                return o.Failed > 0 ? (o.Failed == 1 ? "Couldn’t move it" : "Couldn’t move them") : "Nothing to move";
            case CleanupKind.RecycleBin:
                if (o.Failed > 0) return "Couldn’t empty the Recycle Bin";
                return o.Done > 0 ? $"Freed {size}" : "Already empty";
            default:
                if (o.Done > 0)
                    return $"Freed {size}" + (o.Failed > 0 ? $" · {o.Failed.ToString(CultureInfo.InvariantCulture)} in use" : "");
                return o.Failed > 0 ? "All in use; nothing removed" : "Nothing to clean";
        }
    }

    /// <summary>Whether <see cref="Said"/> reports a failure (drawn in red).</summary>
    public static bool Failed(CleanupOutcome o) => o.Done == 0 && o.Failed > 0;
}

/// <summary>What one cleanup did: bytes freed (or moved), items done, items that couldn't be.</summary>
public sealed record CleanupOutcome(long Bytes, int Done, int Failed);
