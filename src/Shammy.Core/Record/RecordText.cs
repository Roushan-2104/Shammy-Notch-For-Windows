using Shammy.Core.Productivity;

namespace Shammy.Core.Record;

/// <summary>The words a recording shows: the sound line on the start card, the running time, why it stopped.</summary>
public static class RecordText
{
    public const string Title = "Record the screen", WholeScreen = "Whole screen", Window = "A window";

    /// <summary>The left half of the sound line; clicking it flips it.</summary>
    public static string PcSound(bool on) => on ? "PC sound on" : "PC sound off";

    /// <summary>The right half of the sound line; clicking it flips it.</summary>
    public static string Mic(bool on) => on ? "Mic on" : "Mic off";

    /// <summary>"PC sound on · Mic off": the whole line, as a screen reader says it.</summary>
    public static string Sound(bool pcSound, bool mic) => $"{PcSound(pcSound)} · {Mic(mic)}";

    /// <summary>"0:42", "12:05", "1:00:00": the time beside the pet, counting up like a stopwatch.</summary>
    public static string Elapsed(TimeSpan elapsed) => TimeFormat.Elapsed(elapsed);

    /// <summary>"42 sec", "3 min 05 sec", "1 h 02 min": the saved video's length on its card.</summary>
    public static string Length(TimeSpan length)
    {
        long s = (long)Math.Round(Math.Max(0, length.TotalSeconds));
        if (s < 60) return $"{s} sec";
        if (s < 3600) return $"{s / 60} min {s % 60:00} sec";
        return $"{s / 3600} h {s / 60 % 60:00} min";
    }

    /// <summary>"8.4 MB", "1.2 GB", "640 KB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => FormattableString.Invariant($"{bytes / (double)(1L << 30):0.0} GB"),
        >= 1L << 20 => FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MB"),
        >= 1L << 10 => FormattableString.Invariant($"{bytes / 1024.0:0} KB"),
        _ => FormattableString.Invariant($"{Math.Max(0, bytes)} B"),
    };

    /// <summary>"42 sec · 8.4 MB": the line under the file name.</summary>
    public static string Details(TimeSpan length, long bytes) => $"{Length(length)} · {Size(bytes)}";

    /// <summary>What the notch says when a recording ended by itself; null when the user stopped it (the card says enough).</summary>
    public static string? StoppedBecause(RecordStop reason) => reason switch
    {
        RecordStop.TimeLimit => "Stopped at 60 minutes, the longest a recording can be",
        RecordStop.DiskLow => "Stopped: the disk is almost full",
        RecordStop.SourceClosed => "Stopped: that window or screen went away",
        _ => null,
    };

    /// <summary>The saved card's small label: "Recording saved", or why it ended.</summary>
    public static string SavedLabel(RecordStop reason) => reason switch
    {
        RecordStop.TimeLimit => "Stopped at 60 minutes",
        RecordStop.DiskLow => "Stopped, disk almost full",
        RecordStop.SourceClosed => "Stopped, window closed",
        _ => "Recording saved",
    };

    /// <summary>The failure card's short title.</summary>
    public static string FailureTitle(string? code) => code switch
    {
        RecordFailure.NotSupported or RecordFailure.DiskLow or RecordFailure.NoFolder or RecordFailure.NoFile or RecordFailure.NoGraphics
            or RecordFailure.NoSource or RecordFailure.Busy => "Couldn’t start recording",
        _ => "The recording wasn’t saved",
    };

    /// <summary>Why nothing was saved, in plain words, from the short code the recorder reports.</summary>
    public static string Failure(string? code) => code switch
    {
        RecordFailure.NotSupported => "This PC can’t record the screen. It needs Windows 10 version 2004 or newer.",
        RecordFailure.DiskLow => "There is less than 500 MB of free disk space. Free some up and try again.",
        RecordFailure.NoFolder => "The Shammy folder in Videos couldn’t be created.",
        RecordFailure.NoFile => "The video file couldn’t be created in Videos › Shammy.",
        RecordFailure.NoGraphics => "Windows couldn’t reach the graphics card.",
        RecordFailure.NoSource => "That window or screen couldn’t be captured. It may have closed.",
        RecordFailure.Encoder => "Windows couldn’t make the video, so the unfinished file was removed.",
        RecordFailure.Busy => "A recording is already running.",
        _ => "Something went wrong while saving, so the unfinished file was removed.",
    };
}

/// <summary>Short codes for what went wrong; <see cref="RecordText.Failure"/> turns them into words.</summary>
public static class RecordFailure
{
    public const string NotSupported = "not-supported", DiskLow = "disk-low", NoFolder = "no-folder", NoFile = "no-file",
        NoGraphics = "no-graphics", NoSource = "no-source", Encoder = "encoder", Busy = "busy",
        Unknown = "unknown";
}
