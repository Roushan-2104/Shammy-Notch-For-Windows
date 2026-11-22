using System.Diagnostics;
using System.IO;
using Shammy.Core.Agents;
using Shammy.Infrastructure;

namespace Shammy.Modules.Agents;

/// <summary>
/// Claude Code's command line, for its sign-in only: whether it's installed, whether it's signed in, and starting
/// "claude auth login" (Claude Code opens the browser and keeps its own token; Shammy never touches it). The state
/// lives here, not in a Settings row, so rebuilding Settings mid-sign-in can't start a second one.
/// Everything here runs on the UI thread except the process work it awaits.
/// </summary>
internal static class ClaudeCli
{
    static readonly TimeSpan StatusTime = TimeSpan.FromSeconds(10), KeepAnswer = TimeSpan.FromSeconds(30),
        SignInTime = TimeSpan.FromMinutes(5), Poll = TimeSpan.FromSeconds(3);

    static (bool? Value, DateTime AtUtc)? _answer;
    static Task<bool>? _signIn;

    /// <summary>A sign-in started from Shammy and still being waited for.</summary>
    public static Task<bool>? SignIn => _signIn is { IsCompleted: false } t ? t : null;

    /// <summary>The claude command, or null when it isn't installed where Claude Code puts it (or on the PATH).</summary>
    public static string? Find()
    {
        try
        {
            return ClaudeAuth.Candidates(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetEnvironmentVariable("PATH")).FirstOrDefault(File.Exists);
        }
        catch (Exception ex)
        {
            Log.Warn("Looking for Claude Code failed", ex);
            return null;
        }
    }

    /// <summary>
    /// True signed in, false signed out, null when it couldn't be told. A recent answer is reused, so opening
    /// Settings or searching it doesn't start Claude Code each time.
    /// </summary>
    public static async Task<bool?> SignedInAsync(string exe, bool fresh = false)
    {
        if (!fresh && _answer is { } a && DateTime.UtcNow - a.AtUtc < KeepAnswer) return a.Value;
        var value = await AskAsync(exe);
        _answer = (value, DateTime.UtcNow);
        return value;
    }

    static async Task<bool?> AskAsync(string exe)
    {
        try
        {
            var psi = Start(exe, "auth status --json");
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            using var p = Process.Start(psi);
            if (p is null) return null;
            using var cts = new CancellationTokenSource(StatusTime);
            try
            {
                // Both streams drained, all under the one deadline (a child holding stdout open can't hang this).
                var read = p.StandardOutput.ReadToEndAsync(cts.Token);
                var _ = p.StandardError.ReadToEndAsync(cts.Token);
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                return ClaudeAuth.SignedIn(await read.ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { }
                return null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Asking Claude Code whether it's signed in failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Opens Claude Code's sign-in in its own small window (it opens the browser and closes itself when done) and
    /// waits up to five minutes for it to take. One at a time: while one is under way, that one is returned.
    /// Null when it couldn't be started.
    /// </summary>
    public static Task<bool>? StartSignIn(string exe)
    {
        if (SignIn is { } running) return running;
        try
        {
            var psi = Start(exe, "auth login");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = false;
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Starting Claude Code's sign-in failed", ex);
            return null;
        }
        _answer = null;
        return _signIn = WaitAsync(exe);
    }

    static async Task<bool> WaitAsync(string exe)
    {
        var until = DateTime.UtcNow + SignInTime;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(Poll);
            if (await SignedInAsync(exe, fresh: true) == true) return true;
        }
        return false;
    }

    /// <summary>An .exe runs as itself; npm's .cmd goes through cmd.</summary>
    static ProcessStartInfo Start(string exe, string args) =>
        exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c \"\"{exe}\" {args}\"")
            : new ProcessStartInfo(exe, args);
}
