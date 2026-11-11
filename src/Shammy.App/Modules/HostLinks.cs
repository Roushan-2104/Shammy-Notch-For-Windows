using Shammy.Infrastructure;

namespace Shammy.Modules;

/// <summary>
/// The few host services the designed screens reach for (the tour opens the palette, Settings shows the version
/// and checks for updates, the alarm reads the weather glance). Set by ShammyHost at start-up; null in tests and
/// before the host is up, so every caller copes without them.
/// </summary>
internal static class HostLinks
{
    /// <summary>Opens the typing palette (as a tap of Right Alt does), with its empty-state suggestions.</summary>
    public static Action? OpenPalette { get; set; }

    /// <summary>Runs text as if it had been typed into the palette (a command if one answers, a search if not).</summary>
    public static Action<string>? RunText { get; set; }

    /// <summary>A typed or spoken command just ran, with its text (the guide notes which features get used).</summary>
    public static event Action<string>? CommandRan;

    public static void RaiseCommandRan(string text)
    {
        try { CommandRan?.Invoke(text); }
        catch (Exception ex) { Infrastructure.Log.Warn("A CommandRan listener failed", ex); }
    }

    /// <summary>Whether this PC can run the Hinglish voice (it needs the graphics-card engine).</summary>
    public static Func<bool>? HinglishAvailable { get; set; }

    /// <summary>The offline voice model voice commands use; live captions borrow it between commands.</summary>
    public static Search.WhisperService? Voice { get; set; }

    /// <summary>The running version ("0.4.0").</summary>
    public static Func<string>? Version { get; set; }

    /// <summary>The update checker's last result ("up to date", "ready 0.4.1", "not checked") and whether it's an installed copy.</summary>
    public static Func<(string Result, bool Installed)>? UpdateStatus { get; set; }

    /// <summary>Checks for an update now; the result reads like <see cref="UpdateStatus"/>.</summary>
    public static Func<Task<string>>? CheckForUpdates { get; set; }

    /// <summary>The lyric line being sung right now, when the song has synced lyrics; null or empty otherwise.</summary>
    public static Func<string?>? CurrentLyric { get; set; }

    /// <summary>Restarts into a downloaded update.</summary>
    public static Action? RestartForUpdate { get; set; }

    /// <summary>The update checker's status changed; read it from <see cref="UpdateStatus"/>.</summary>
    public static event Action? UpdateChanged;

    public static void RaiseUpdateChanged() => UpdateChanged?.Invoke();

    /// <summary>
    /// Says a short line aloud ("Tea's ready!") when the user turned on online voice; does nothing otherwise.
    /// Set by the online voice module, so it is null when that feature is off. Never throws.
    /// </summary>
    public static Action<string>? Say { get; set; }

    /// <summary>
    /// Online listening: the talk key's recording (16 kHz mono) as text, or null when the local voice model
    /// should transcribe it (the mode is off, the service was slow or failed). Asked before the local model.
    /// </summary>
    public static Func<float[], CancellationToken, Task<string?>>? OnlineTranscribe { get; set; }

    /// <summary>Whether a recording made now would be sent online (the listening view then says so).</summary>
    public static Func<bool>? OnlineListening { get; set; }

    /// <summary>Every module's glance provider, by id ("weather", "calendar"…).</summary>
    public static IReadOnlyDictionary<string, Func<GlanceItem?>>? Glances { get; set; }

    /// <summary>The weather glance's text right now ("24° Bhubaneswar"), or null.</summary>
    public static string? WeatherGlance()
    {
        try
        {
            return Glances is not null && Glances.TryGetValue("weather", out var provider) ? provider()?.Text : null;
        }
        catch (Exception ex)
        {
            Log.Warn("Reading the weather glance failed", ex);
            return null;
        }
    }
}
