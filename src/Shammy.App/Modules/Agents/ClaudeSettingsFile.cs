using System.Text;
using Shammy.Core.Agents;

namespace Shammy.Modules.Agents;

/// <summary>What was read from a settings file: its text, or why it couldn't be read.</summary>
internal sealed record SettingsRead(bool FolderExists, bool FileExists, string? Text, string Error);

/// <summary>
/// Claude Code's user settings file on disk. Shammy only ever writes it from Connect and Disconnect (a button the
/// user pressed); everything else here reads. Writes go through a temporary file beside it and one rename, and the
/// first Connect keeps the file as it was in "settings.json.shammy-backup".
/// </summary>
internal static class ClaudeSettingsFile
{
    const long MaxBytes = 4 * 1024 * 1024;

    public const string BackupSuffix = ".shammy-backup";

    /// <summary>
    /// Where Claude Code keeps the user's settings: %CLAUDE_CONFIG_DIR%\settings.json when that is set, otherwise
    /// %USERPROFILE%\.claude\settings.json.
    /// </summary>
    public static string DefaultPath()
    {
        string? custom = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string dir = !string.IsNullOrWhiteSpace(custom)
            ? custom.Trim()
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        return Path.Combine(dir, "settings.json");
    }

    // Strict: bytes that aren't UTF-8 must not be "repaired" and written back.
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static SettingsRead Read(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return new SettingsRead(false, false, null, "");
            var info = new FileInfo(path);
            if (!info.Exists) return new SettingsRead(true, false, null, "");
            if (info.Length > MaxBytes) return new SettingsRead(true, true, null, "Claude Code’s settings file is too big for Shammy to edit.");
            // A byte-order mark comes through as the text's first character and goes back out the same way.
            return new SettingsRead(true, true, Utf8.GetString(File.ReadAllBytes(path)), "");
        }
        catch (DecoderFallbackException)
        {
            return new SettingsRead(true, true, null, "Claude Code’s settings file isn’t plain text Shammy can read, so it left it alone.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new SettingsRead(true, true, null, "Shammy couldn’t open Claude Code’s settings file.");
        }
    }

    /// <summary>
    /// Replaces the file's text in one step. <paramref name="expected"/> is what was read before editing: if the
    /// file changed meanwhile (Claude Code saved something), nothing is written and the caller starts over.
    /// </summary>
    /// <param name="backup">Keep the file as it is now beside it first, unless such a copy already exists.</param>
    /// <returns>Null when written; otherwise what went wrong ("changed" for the start-over case).</returns>
    public static string? Write(string path, string? expected, string text, bool backup)
    {
        string tmp = path + ".shammy-tmp";
        try
        {
            var now = Read(path);
            if (now.Error.Length > 0) return now.Error;
            if (now.Text != expected) return Changed;

            if (backup && now.FileExists && !File.Exists(path + BackupSuffix)) File.Copy(path, path + BackupSuffix);
            File.WriteAllBytes(tmp, Utf8.GetBytes(text));
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Infrastructure.Log.Warn("[agents] writing Claude Code's settings failed", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception) { /* nothing more to do */ }
            return "Shammy couldn’t save Claude Code’s settings file.";
        }
    }

    public const string Changed = "changed";

    /// <summary>Reads, edits and writes, starting over when the file changed in between.</summary>
    public static HookEdit Apply(string path, Func<string?, HookEdit> edit, bool backup)
    {
        string failure = "Claude Code’s settings kept changing, so Shammy left them alone. Try again.";
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var read = Read(path);
            if (!read.FolderExists) return new HookEdit(false, "", "Claude Code not found.", false);
            if (read.Error.Length > 0) return new HookEdit(false, read.Text ?? "", read.Error, false);
            var result = edit(read.Text);
            if (!result.Ok || !result.Changed) return result;
            string? error = Write(path, read.Text, result.Json, backup);
            if (error is null) return result;
            if (error != Changed) return result with { Ok = false, Error = error, Changed = false };
        }
        return new HookEdit(false, "", failure, false);
    }
}
