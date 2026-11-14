namespace Shammy.Infrastructure;

internal static class Paths
{
    static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string Root { get; } = ResolveRoot();

    public static string Logs => Ensure("logs");
    public static string Models => Ensure("models");
    public static string WebView => Ensure("webview");
    public static string SettingsFile => Path.Combine(Ensure(""), "settings.json");

    static string Ensure(string sub)
    {
        var dir = Path.Combine(Root, sub);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>%LOCALAPPDATA%\Shammy: settings, notes, clipboard, voice model and browser profile.</summary>
    static string ResolveRoot()
    {
        // Test pipe only: a separate data folder, so screenshots and tests never touch (or show) the user's own data.
        if (CommandServer.IsEnabled && Environment.GetEnvironmentVariable("SHAMMY_DATA_ROOT") is { Length: > 0 } testRoot) return testRoot;
        return Path.Combine(Local, "Shammy");
    }
}
