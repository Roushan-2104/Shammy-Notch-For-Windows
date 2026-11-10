using System.Globalization;

namespace Shammy.Core.Record;

/// <summary>What a recording is called: "Shammy 2026-11-10 22.41.07.mp4", and never the name of a file that is already there.</summary>
public static class RecordNaming
{
    public const string FolderName = "Shammy", Extension = ".mp4";

    /// <summary>The name for a recording started at this local time.</summary>
    public static string FileName(DateTime startedLocal) =>
        "Shammy " + startedLocal.ToString("yyyy-MM-dd HH.mm.ss", CultureInfo.InvariantCulture) + Extension;

    /// <summary>The user's Videos folder plus "Shammy".</summary>
    public static string Folder(string videosFolder) => Path.Combine(videosFolder, FolderName);

    /// <summary>
    /// A path in <paramref name="folder"/> that nothing has yet: the plain name, then "… (2).mp4", "… (3).mp4".
    /// <paramref name="exists"/> is asked about each candidate (File.Exists in the app).
    /// </summary>
    public static string FreePath(string folder, DateTime startedLocal, Func<string, bool> exists)
    {
        var name = FileName(startedLocal);
        var path = Path.Combine(folder, name);
        if (!exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(name);
        for (int n = 2; ; n++)
        {
            path = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem} ({n}){Extension}"));
            if (!exists(path)) return path;
        }
    }
}
