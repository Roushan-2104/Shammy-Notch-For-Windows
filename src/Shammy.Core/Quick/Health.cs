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

    /// <summary>Whether something deserves attention (shown higher in the right-now line).</summary>
    public static bool NeedsAttention(HealthReport r) =>
        r.System is { Low: true } || r.RestartForUpdates || r.Uptime.TotalDays >= 14 || r.Battery is { Percent: < 70 };

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

    public static string Name(CleanupKind kind) => kind switch
    {
        CleanupKind.TempFiles => "Temporary files",
        CleanupKind.RecycleBin => "Recycle Bin",
        _ => "Old downloads",
    };

    public static string Explain(CleanupKind kind) => kind switch
    {
        CleanupKind.TempFiles => "Left behind by apps and installers; anything in use is skipped",
        CleanupKind.RecycleBin => "Emptied for good",
        _ => "Not opened in 3 months; moved to the Recycle Bin, so you can still get them back",
    };

    public static string Verb(CleanupKind kind) => kind switch
    {
        CleanupKind.RecycleBin => "Empty",
        CleanupKind.OldDownloads => "Move",
        _ => "Clean",
    };
}
