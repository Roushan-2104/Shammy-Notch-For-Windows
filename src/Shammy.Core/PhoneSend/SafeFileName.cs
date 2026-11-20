using System.Globalization;
using System.Text;

namespace Shammy.Core.PhoneSend;

/// <summary>
/// Turns a filename chosen by a remote browser into one that is safe to create in a single folder on
/// Windows: no directories, no device names, no spoofing characters, never clobbering an existing file.
/// </summary>
public static class SafeFileName
{
    /// <summary>Leaves room for " (123)" and a temp suffix inside NTFS's 255-unit component limit.</summary>
    public const int MaxLength = 200;

    const string Invalid = "<>:\"/\\|?*";

    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        // Windows also treats the superscript digits as device numbers.
        "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    public static string Sanitize(string? name, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var s = name;
        try { s = s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { } // unpaired surrogates; dropped below

        // Only the last path segment counts: "../../evil.exe" and "C:\x\y.txt" become their leaf.
        int slash = s.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) s = s[(slash + 1)..];

        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                // Keep astral characters (emoji), but not astral format characters (tag characters).
                if (CharUnicodeInfo.GetUnicodeCategory(s, i) != UnicodeCategory.Format) sb.Append(c).Append(s[i + 1]);
                i++;
                continue;
            }
            if (char.IsSurrogate(c)) continue;
            if (c is '\t' or '\r' or '\n') { sb.Append(' '); continue; }
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            // Controls, and format characters such as U+202E (right-to-left override) which make
            // "gpj.exe" display as "exe.jpg", or zero-width joiners hiding in a name.
            if (cat is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            if (Invalid.IndexOf(c) >= 0) { sb.Append('_'); continue; }
            sb.Append(char.IsWhiteSpace(c) ? ' ' : c);
        }

        // Windows silently drops trailing dots and spaces, which would let "a.txt." alias "a.txt".
        s = sb.ToString().Trim().TrimEnd('.', ' ');
        if (s.Length == 0 || s.All(c => c == '.')) return fallback;

        int dot = s.IndexOf('.');
        var stem = (dot < 0 ? s : s[..dot]).TrimEnd(' ');
        if (Reserved.Contains(stem)) s = "_" + s;

        return Truncate(s);
    }

    /// <summary>Shortens a long name on text-element boundaries, keeping a short extension.</summary>
    static string Truncate(string s)
    {
        if (s.Length <= MaxLength) return s;
        var ext = Path.GetExtension(s);
        if (ext.Length > 16) ext = "";
        var stem = s[..^ext.Length];
        int budget = MaxLength - ext.Length;
        var e = StringInfo.GetTextElementEnumerator(stem);
        int cut = 0;
        while (e.MoveNext())
        {
            int next = e.ElementIndex + ((string)e.Current).Length;
            if (next > budget) break;
            cut = next;
        }
        return stem[..cut].TrimEnd('.', ' ') + ext;
    }

    /// <summary>
    /// A path in <paramref name="directory"/> for <paramref name="fileName"/> that doesn't exist yet,
    /// adding " (1)", " (2)"... like Explorer does.
    /// </summary>
    public static string MakeUnique(string directory, string fileName, Func<string, bool> exists)
    {
        var first = Path.Combine(directory, fileName);
        if (!exists(first)) return first;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
        return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){ext}");
    }

    static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".msi", ".msp", ".msix", ".appx", ".appinstaller", ".cpl", ".dll", ".jar", ".lnk", ".url", ".reg",
        ".inf", ".scf", ".library-ms", ".searchconnector-ms", ".settingcontent-ms", ".application", ".gadget",
        ".msc", ".diagcab", ".chm", ".iso", ".img", ".vhd", ".vhdx",
    };

    /// <summary>
    /// Files that run code (or mount / point somewhere) when double-clicked get ".blocked" appended, so
    /// something dropped by anyone who saw the transfer link can't run by accident. Renaming is a
    /// deliberate act.
    /// </summary>
    public static string NeutralizeExecutable(string fileName) =>
        Executable.Contains(Path.GetExtension(fileName)) ? fileName + ".blocked" : fileName;

    /// <summary>
    /// Moves a received temp file into <paramref name="directory"/> as <paramref name="name"/> (already sanitized) and
    /// returns where it went. Mark-of-the-Web first: whatever arrives over the network gets SmartScreen / Protected View
    /// checks when opened, exactly like a browser download (the stream moves with the file). Programs are neutralized,
    /// and the final name is picked only now, retrying a few times if something else took it in the meantime.
    /// </summary>
    public static string MoveIn(string temp, string directory, string name)
    {
        try { File.WriteAllText(temp + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        name = NeutralizeExecutable(name);
        for (int attempt = 0; ; attempt++)
        {
            var final = MakeUnique(directory, name, p => File.Exists(p) || Directory.Exists(p));
            if (!IsDirectlyInside(directory, final)) throw new InvalidDataException("Bad upload path");
            try
            {
                File.Move(temp, final, overwrite: false);
                return final;
            }
            catch (IOException) when (attempt < 5 && File.Exists(final))
            {
            }
        }
    }

    /// <summary>True when <paramref name="path"/> resolves to a file directly inside <paramref name="directory"/>.</summary>
    public static bool IsDirectlyInside(string directory, string path)
    {
        try
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var full = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(full);
            return parent is not null && string.Equals(Path.TrimEndingDirectorySeparator(parent), dir, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).Length > 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
