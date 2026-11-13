namespace Shammy.Core.Search;

/// <summary>
/// Which files in Shammy's voice-model folder go to the Recycle Bin now that voice commands are heard online and
/// only the small "base" model stays as the offline backup. Every model file goes except that one: the big
/// models (large-v3-turbo, small, the Hinglish model) and half-finished downloads ("*.part"), except a
/// half-finished download of the kept model itself, which the next download resumes. Files that are not models
/// (the verified list, anything unknown) are left alone.
/// </summary>
public static class ModelCleanup
{
    /// <summary>The one model kept.</summary>
    public const string Keep = "ggml-base.bin";

    static readonly string[] ModelExtensions = { ".bin", ".gguf", ".ggml", ".part" };

    /// <summary>The file names (no folders) to move to the Recycle Bin, in the order given.</summary>
    public static IReadOnlyList<string> ToRecycle(IEnumerable<string> fileNames)
    {
        var result = new List<string>();
        foreach (var name in fileNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            // Names only: anything that looks like a path is not ours to judge.
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) continue;
            if (IsKept(name)) continue;
            if (ModelExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) result.Add(name);
        }
        return result;
    }

    static bool IsKept(string name) =>
        string.Equals(name, Keep, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, Keep + ".part", StringComparison.OrdinalIgnoreCase);

    /// <summary>"3 files, 1.2 GB": counts only, never names, for the log and the pipe.</summary>
    public static string Describe(int files, long bytes) =>
        files == 0 ? "nothing" : $"{files} file{(files == 1 ? "" : "s")}, {Size(bytes)}";

    static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
}
