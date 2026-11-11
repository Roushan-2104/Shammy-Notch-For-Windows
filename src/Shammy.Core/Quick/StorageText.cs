using System.Globalization;

namespace Shammy.Core.Quick;

/// <summary>Sizes in the few words the notch has room for.</summary>
public static class StorageText
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"1.2 GB", "540 MB", "12 KB", "0 B".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => (bytes / (double)(1L << 40)).ToString(bytes >= 10L << 40 ? "0" : "0.0", Inv) + " TB",
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.0", Inv) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", Inv) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0", Inv) + " KB",
        _ => Math.Max(0, bytes).ToString(Inv) + " B",
    };

    /// <summary>"54 GB free of 238 GB".</summary>
    public static string Free(long free, long total) => $"{Size(free)} free of {Size(total)}";
}
