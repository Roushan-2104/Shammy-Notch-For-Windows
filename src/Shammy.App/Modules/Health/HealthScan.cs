using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Shammy.Core.Quick;
using Shammy.Infrastructure;

namespace Shammy.Modules.Health;

/// <summary>
/// What the Health card reads and cleans on this PC. Every read fails soft (a missing key, a locked file, a
/// folder it may not list); cleaning never touches anything in use, empties only the Recycle Bin for good, and
/// moves old downloads to the Recycle Bin rather than deleting them.
/// </summary>
internal static class HealthScan
{
    // ---- Reading -----------------------------------------------------------------------------------

    public static IReadOnlyList<DriveSpace> Drives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => new DriveSpace(d.Name.TrimEnd('\\'), d.AvailableFreeSpace, d.TotalSize))
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the drives failed", ex);
            return Array.Empty<DriveSpace>();
        }
    }

    public static DriveSpace? SystemDrive()
    {
        var root = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\');
        return Drives().FirstOrDefault(d => string.Equals(d.Name, root, StringComparison.OrdinalIgnoreCase));
    }

    public static TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Windows installed updates and is waiting for a restart to finish them.</summary>
    public static bool RestartForUpdates()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            return key is not null;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Apps that start with Windows and aren't switched off in Task Manager.</summary>
    public static int StartupApps()
    {
        int count = 0;
        const string Run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string Approved = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        try
        {
            count += Enabled(Registry.CurrentUser, Run, Registry.CurrentUser, Approved + "Run");
            count += Enabled(Registry.LocalMachine, Run, Registry.LocalMachine, Approved + "Run");
            count += Enabled(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", Registry.LocalMachine, Approved + "Run32");
            count += Folder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, Approved + "StartupFolder");
            count += Folder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, Approved + "StartupFolder");
        }
        catch (Exception ex) { Log.Warn("Counting startup apps failed", ex); }
        return count;

        // Task Manager's switch: StartupApproved holds a value per entry whose first byte is odd when it is off.
        static bool Off(RegistryKey? approved, string name) => approved?.GetValue(name) is byte[] { Length: > 0 } b && (b[0] & 1) == 1;

        static int Enabled(RegistryKey hive, string runPath, RegistryKey approvedHive, string approvedPath)
        {
            using var run = hive.OpenSubKey(runPath);
            if (run is null) return 0;
            using var approved = approvedHive.OpenSubKey(approvedPath);
            return run.GetValueNames().Count(n => n.Length > 0 && !Off(approved, n));
        }

        static int Folder(string dir, RegistryKey approvedHive, string approvedPath)
        {
            if (!Directory.Exists(dir)) return 0;
            using var approved = approvedHive.OpenSubKey(approvedPath);
            return Directory.EnumerateFiles(dir).Count(f => !f.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) && !Off(approved, Path.GetFileName(f)));
        }
    }

    /// <summary>Battery wear from Windows' own battery report (a second or two of work); null without a battery.</summary>
    public static async Task<BatteryWear?> BatteryAsync()
    {
        var file = Path.Combine(Path.GetTempPath(), $"shammy-battery-{Environment.ProcessId}.xml");
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", $"/batteryreport /xml /output \"{file}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await p.WaitForExitAsync(limit.Token);
            if (p.ExitCode != 0 || !File.Exists(file)) return null;
            return HealthText.ParseBatteryReport(await File.ReadAllTextAsync(file));
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the battery report failed", ex);
            return null;
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }
    }

    // ---- Measuring what cleanup would free ---------------------------------------------------------------

    const int MaxFiles = 200_000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The Downloads folder, wherever the user moved it; the profile's own as a fallback.</summary>
    public static string DownloadsFolder
    {
        get
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            try
            {
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
                {
                    try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } path) return path; }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch (Exception) { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    /// <summary>Shammy's own temporary folder (files waiting to go to the phone and the like): never cleaned here.</summary>
    static string OwnTemp => Path.Combine(Path.GetTempPath(), "Shammy");

    /// <summary>The bytes and files (for downloads: whole items) a cleanup would touch.</summary>
    public static (long Bytes, int Files) Measure(CleanupKind kind)
    {
        switch (kind)
        {
            case CleanupKind.TempFiles: return Sum(TempFiles());
            case CleanupKind.RecycleBin: return RecycleBin();
            default:
                var old = OldDownloads();
                return (old.Sum(o => o.Bytes), old.Count);
        }
    }

    /// <summary>Temporary files from before this week, outside Shammy's own folder.</summary>
    static IEnumerable<FileInfo> TempFiles()
    {
        var own = OwnTemp + Path.DirectorySeparatorChar;
        return Files(Path.GetTempPath(), DateTime.Now - Cleanup.TempOldAfter)
            .Where(f => !f.FullName.StartsWith(own, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The top-level items in Downloads that arrived over three months ago (see <see cref="Cleanup.IsOld"/>), with
    /// their size. A folder counts as one item and only when everything in it is old; a folder too big to look
    /// through, a link, or anything hidden or system (desktop.ini) is left alone.
    /// </summary>
    public static List<(FileSystemInfo Item, long Bytes)> OldDownloads()
    {
        var found = new List<(FileSystemInfo, long)>();
        var dir = DownloadsFolder;
        if (!Directory.Exists(dir)) return found;
        var cutoff = DateTime.Now - Cleanup.OldAfter;
        const FileAttributes Skip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint;
        IEnumerable<FileSystemInfo> top;
        try { top = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = Skip }).ToList(); }
        catch (Exception ex)
        {
            Log.Warn("Listing Downloads failed", ex);
            return found;
        }
        int budget = MaxFiles;
        foreach (var item in top)
        {
            try
            {
                if (item is FileInfo file)
                {
                    if (Cleanup.IsOld(new[] { (file.CreationTime, file.LastWriteTime) }, cutoff)) found.Add((file, file.Length));
                    continue;
                }
                if (item is not DirectoryInfo folder) continue;
                var stamps = new List<(DateTime, DateTime)> { (folder.CreationTime, folder.LastWriteTime) };
                long bytes = 0;
                bool tooBig = false;
                foreach (var inside in folder.EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (--budget < 0) { tooBig = true; break; }
                    stamps.Add((inside.CreationTime, inside.LastWriteTime));
                    if (inside is FileInfo f) bytes += f.Length;
                }
                if (!tooBig && Cleanup.IsOld(stamps, cutoff)) found.Add((folder, bytes));
            }
            catch (Exception) { /* can't be read: leave it */ }
        }
        return found;
    }

    static (long, int) Sum(IEnumerable<FileInfo> files)
    {
        long bytes = 0;
        int count = 0;
        foreach (var f in files)
        {
            try { bytes += f.Length; count++; }
            catch (Exception) { }
        }
        return (bytes, count);
    }

    /// <summary>Files under <paramref name="dir"/> (all levels), only those last written before <paramref name="before"/> when given.</summary>
    static IEnumerable<FileInfo> Files(string dir, DateTime? before)
    {
        if (!Directory.Exists(dir)) yield break;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
        int seen = 0;
        foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            if (++seen > MaxFiles) yield break;
            if (before is { } b && (f.LastWriteTime >= b || f.LastAccessTime >= b)) continue;
            yield return f;
        }
    }

    // ---- Cleaning ------------------------------------------------------------------------------------

    /// <summary>
    /// Cleans one kind; says what was freed (or moved), how many went and how many couldn't (in use, or Windows
    /// said no). Files in use are skipped; Shammy's own temporary folder is never touched.
    /// </summary>
    public static CleanupOutcome Clean(CleanupKind kind)
    {
        switch (kind)
        {
            case CleanupKind.TempFiles:
            {
                long bytes = 0;
                int count = 0, failed = 0;
                var temp = Path.GetTempPath();
                foreach (var f in TempFiles().ToList())
                {
                    try
                    {
                        long size = f.Length;
                        f.Delete();
                        bytes += size;
                        count++;
                    }
                    catch (Exception) { failed++; /* in use or not ours: skip */ }
                }
                // Folders left empty go too (deepest first), never Shammy's own.
                var own = OwnTemp;
                try
                {
                    foreach (var d in new DirectoryInfo(temp).EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                                 .Where(d => !d.FullName.StartsWith(own, StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(d => d.FullName.Length))
                    {
                        try { if (!d.EnumerateFileSystemInfos().Any()) d.Delete(); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
                return new CleanupOutcome(bytes, count, failed);
            }
            case CleanupKind.RecycleBin:
            {
                var before = RecycleBin();
                if (before.Files == 0) return new CleanupOutcome(0, 0, 0);
                const uint NoConfirmation = 0x1, NoProgress = 0x2, NoSound = 0x4;
                int hr = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgress | NoSound);
                if (hr == 0) return new CleanupOutcome(before.Bytes, before.Files, 0);
                Log.Warn($"Emptying the Recycle Bin failed (0x{hr:X8})");
                return new CleanupOutcome(0, 0, 1);
            }
            default:
            {
                var old = OldDownloads();
                if (old.Count == 0) return new CleanupOutcome(0, 0, 0);
                if (!ToRecycleBin(old.Select(o => o.Item.FullName))) Log.Warn("Moving old downloads to the Recycle Bin stopped early");
                // Whatever is no longer there went to the Recycle Bin; the rest couldn't (in use, or Windows said no).
                long bytes = 0;
                int moved = 0, failed = 0;
                foreach (var (item, size) in old)
                {
                    item.Refresh();
                    if (item.Exists) failed++;
                    else
                    {
                        moved++;
                        bytes += size;
                    }
                }
                return new CleanupOutcome(bytes, moved, failed);
            }
        }
    }

    // ---- Shell calls ---------------------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    static (long Bytes, int Files) RecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, (int)Math.Min(int.MaxValue, info.i64NumItems)) : (0, 0);
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

    /// <summary>Moves files to the Recycle Bin (they can be restored from there).</summary>
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
        return SHFileOperation(ref op) == 0;
    }
}
