namespace Shammy.Core.Record;

/// <summary>Why a recording ended.</summary>
public enum RecordStop
{
    /// <summary>The user stopped it (the circle, the command, the tray).</summary>
    User,
    /// <summary>It reached the length it was asked to run for (the test pipe's "seconds=N").</summary>
    Timed,
    /// <summary>It reached the longest a recording may be.</summary>
    TimeLimit,
    /// <summary>The disk it is saved on is nearly full.</summary>
    DiskLow,
    /// <summary>The window was closed or the display unplugged.</summary>
    SourceClosed,
    /// <summary>Something broke; there may be no usable file.</summary>
    Failed,
}

/// <summary>When a recording stops by itself: after an hour, or when the disk is nearly full.</summary>
public static class RecordLimits
{
    public static readonly TimeSpan MaxLength = TimeSpan.FromMinutes(60);

    /// <summary>Under this much free space a recording stops (and none starts).</summary>
    public const long MinFreeBytes = 500L * 1024 * 1024;

    /// <summary>How often the free space is looked at while recording.</summary>
    public static readonly TimeSpan DiskCheckEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether a running recording must stop now, and why; null to keep going. <paramref name="freeBytes"/> is null
    /// when the free space could not be read (a network folder): that never stops a recording.
    /// </summary>
    public static RecordStop? Check(TimeSpan elapsed, long? freeBytes, TimeSpan? runFor = null)
    {
        if (freeBytes is { } free && free < MinFreeBytes) return RecordStop.DiskLow;
        if (elapsed >= MaxLength) return RecordStop.TimeLimit;
        if (runFor is { } asked && elapsed >= asked) return RecordStop.Timed;
        return null;
    }

    /// <summary>Whether there is room to start at all (unknown free space counts as room).</summary>
    public static bool CanStart(long? freeBytes) => freeBytes is not { } free || free >= MinFreeBytes;
}
