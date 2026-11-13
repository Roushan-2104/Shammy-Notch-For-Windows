using System.Runtime.InteropServices;
using Shammy.Core.Search;
using Shammy.Infrastructure;

namespace Shammy.Search;

/// <summary>
/// Once, after the update that made voice commands online-only: the voice models Shammy no longer uses (the big
/// ones and half-finished downloads) go from %LOCALAPPDATA%\Shammy\models to the Recycle Bin, where they can be
/// restored. Only files directly in that folder, chosen by <see cref="ModelCleanup"/>; the offline backup
/// (ggml-base.bin) stays. A marker file remembers it was done; the log gets counts only.
/// </summary>
internal static class ModelFolderCleanup
{
    static string Marker => Path.Combine(Paths.Root, "models-cleanup-1.done");

    public static bool Done => File.Exists(Marker);

    /// <summary>The files that would go, with their total size.</summary>
    static (List<FileInfo> Files, long Bytes) Plan()
    {
        var folder = new DirectoryInfo(Paths.Models);
        var root = Path.GetFullPath(folder.FullName).TrimEnd('\\') + "\\";
        // Plain files only: never a link that points somewhere else.
        var here = folder.GetFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => (f.Attributes & FileAttributes.ReparsePoint) == 0)
            .ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var files = ModelCleanup.ToRecycle(here.Keys)
            .Select(name => here[name])
            .Where(f => string.Equals(Path.GetDirectoryName(Path.GetFullPath(f.FullName)) + "\\", root, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return (files, files.Sum(f => f.Length));
    }

    /// <summary>"models-cleanup" pipe: what would go (names and sizes), without moving anything.</summary>
    public static string DryRun()
    {
        try
        {
            var (files, bytes) = Plan();
            return $"done={(Done ? "yes" : "no")} would_recycle={ModelCleanup.Describe(files.Count, bytes)}" +
                   (files.Count > 0 ? " [" + string.Join(" | ", files.Select(f => $"{f.Name} {f.Length >> 20}MB")) + "]" : "") +
                   $" keeps={ModelCleanup.Keep}:{(File.Exists(WhisperService.Base.LocalPath) ? "present" : "absent")}";
        }
        catch (Exception ex)
        {
            return "error: " + ex.GetType().Name;
        }
    }

    /// <summary>Moves the unused models to the Recycle Bin, once. Call off the UI thread.</summary>
    public static void RunOnce()
    {
        try
        {
            if (Done) return;
            var (files, bytes) = Plan();
            if (files.Count > 0)
            {
                if (!ToRecycleBin(files.Select(f => f.FullName)))
                {
                    // Something had a file open: try again next start.
                    Log.Warn($"Moving unused voice models to the Recycle Bin failed ({ModelCleanup.Describe(files.Count, bytes)}); will try again next start");
                    return;
                }
                Log.Info($"Moved unused voice models to the Recycle Bin: {ModelCleanup.Describe(files.Count, bytes)}");
            }
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception ex)
        {
            Log.Warn("Cleaning up the voice models failed", ex);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Moves files to the Recycle Bin (they can be restored from there), with no dialogs.</summary>
    static bool ToRecycleBin(IEnumerable<string> paths)
    {
        const uint Delete = 3;
        const ushort AllowUndo = 0x40, NoConfirmation = 0x10, Silent = 0x4, NoErrorUi = 0x400;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = Delete,
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = AllowUndo | NoConfirmation | Silent | NoErrorUi,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
