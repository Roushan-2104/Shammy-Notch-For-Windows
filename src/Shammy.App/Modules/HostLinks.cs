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

    /// <summary>
    /// Whether something in Shammy could be what the words mean (set by the palette). Voice uses it to offer
    /// choices instead of searching the web when no command took the words.
    /// </summary>
    public static Func<string, bool>? HasChoices { get; set; }

    /// <summary>"Screenshot → Send to phone" when the text is several commands in one sentence; null otherwise.</summary>
    public static Func<string, string?>? ChainPlan { get; set; }

    /// <summary>The wheel takes 1–8 and Esc while it is open (true, with the handler; 0 is Esc) and gives them back (false).</summary>
    public static Action<bool, Action<int>?>? WheelKeys { get; set; }

    /// <summary>Starts listening as a held talk key would, and stops by itself after a short silence (the wheel's Ask bead).</summary>
    public static Action? ListenUntilSilence { get; set; }

    /// <summary>Up to three things in Shammy the words could mean, best first (set by the palette); empty for a plain question.</summary>
    public static Func<string, IReadOnlyList<Choice>>? Choices { get; set; }

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
    /// Says something the user asked for (the morning brief) whenever a Gnani key is saved, even with "Shammy
    /// speaks" off; still never in a call, a meeting or with the sounds off. Null without the online voice.
    /// </summary>
    public static Action<string>? SayAnyway { get; set; }

    /// <summary>
    /// "Watch price" (Circle to buy smarter): watches a product and pops up in the island when it gets cheaper. Returns the
    /// word back to show. Set by the price watch module.
    /// </summary>
    public static Func<string, decimal, string, string>? WatchPrice { get; set; }

    /// <summary>
    /// Online listening: the talk key's recording (16 kHz mono) as text, or null when the local voice model
    /// should transcribe it (the mode is off, the service was slow or failed). Asked before the local model.
    /// </summary>
    public static Func<float[], CancellationToken, Task<string?>>? OnlineTranscribe { get; set; }

    /// <summary>Whether a recording made now would be sent online (the listening view then says so).</summary>
    public static Func<bool>? OnlineListening { get; set; }

    /// <summary>
    /// Runs a wheel action ("playpause", "stage", "timer", a bead id…) exactly as picking its droplet on the wheel
    /// does, so the open island's tool row and the wheel never differ. Set by the wheel module; null while the wheel
    /// is off or hasn't set it, and then <see cref="RunWheelAction"/> falls back to the action's own command.
    /// </summary>
    public static Action<string>? WheelRunner { get; set; }

    /// <summary>
    /// Every live activity, most important first (the call's mic, a running clock…). Set by the host; null in tests,
    /// and then the island's home works the call out from quiet mode instead.
    /// </summary>
    public static Func<IReadOnlyList<Core.Activities.Activity>>? Activities { get; set; }

    /// <summary>
    /// Runs a wheel action: through the wheel's own runner when it has set one, otherwise as the wheel would —
    /// cards open, media keys press, and everything else runs its command text. Returns false for an unknown id.
    /// </summary>
    public static bool RunWheelAction(string id, Func<string, bool> openCard, ShammyServices services)
    {
        if (Core.Wheel.WheelSlots.Find(id) is null) return false;
        if (WheelRunner is { } runner)
        {
            runner(id);
            return true;
        }
        var media = services.Media;
        switch (id)
        {
            case "playpause": media.TogglePlayPause(); return true;
            case "next": media.Next(); return true;
            case "previous": media.Previous(); return true;
            case "ask":
                ListenUntilSilence?.Invoke();
                return true;
            case "translate": return TranslateCopied();
            case "timer" or "notes" or "mirror" or "clipboard" or "calendar" or "weather" or "mixer" or "settings" or "snippets" or "guide":
                return openCard(id);
            case "musiccard": return openCard("music");
        }
        var text = id switch
        {
            "screenshot" => "screenshot",
            "record" => "record my screen",
            "music" => media.IsPlaying ? "pause" : "play music",
            "phone" or "share" => "send this to my phone",
            "awake" => "keep my pc awake",
            "captions" => "turn on captions",
            "prompter" => "open the teleprompter",
            "focus" => $"start focus for {(int)Core.Productivity.Pomodoro.FocusLength.TotalMinutes} minutes",
            "stage" => "stage",
            "mute" => "toggle mic",
            "lyrics" => "show lyrics",
            // Anything newer: its first command phrase.
            _ => Core.Wheel.WheelSlots.Find(id)!.Words.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? id,
        };
        RunText?.Invoke(text);
        return true;
    }

    /// <summary>The wheel's Translate: the copied text into the home language (English when it isn't English).</summary>
    static bool TranslateCopied()
    {
        string text = "";
        try
        {
            if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText().Trim();
        }
        catch (Exception)
        {
            // Another app holds the clipboard open; treat it as empty.
        }
        if (text.Length == 0)
        {
            RunText?.Invoke("translate");
            return true;
        }
        if (text.Length > 500) text = text[..500];
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var language = Core.Conversions.Translation.LanguageName(Core.Conversions.Translation.DefaultTarget(text));
        RunText?.Invoke($"translate {text} to {language}");
        return true;
    }

    /// <summary>
    /// Remembers a circle after its answer showed (set by the circle memory; null while it is off). Does nothing
    /// while the screen is shared or memory is paused. Never throws.
    /// </summary>
    public static Action<CircleMemory.CircleRecord>? RememberCircle { get; set; }

    /// <summary>
    /// Shows a remembered circle again as the island's answer: its title, a label ("From your circles · yesterday")
    /// and the answer line (set by the circle flow). When null, the Circles card copies the answer instead.
    /// </summary>
    public static Action<string, string, string?>? ShowRemembered { get; set; }

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

/// <summary>One "Did you mean" chip: what it is, a glyph, and what picking it does.</summary>
internal sealed record Choice(string Title, string Detail, string Glyph, Action Run);
