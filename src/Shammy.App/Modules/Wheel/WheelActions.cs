using Shammy.Core.Wheel;
using Shammy.Modules.SelectionTools;
using Shammy.Playback;

namespace Shammy.Modules.Wheel;

/// <summary>
/// What each wheel action does, in one place: the dial's droplets, the open island's tool row and its everything page
/// all run their actions here, so they never differ.
/// </summary>
internal static class WheelActions
{
    /// <summary>
    /// Runs a wheel action: cards open through <paramref name="openCard"/>, media keys press, and everything else runs
    /// its command text. <paramref name="say"/> shows a short message (text, whether it's an error). False for an
    /// unknown id or a card that didn't open.
    /// </summary>
    public static bool Run(string id, Func<string, bool> openCard, MediaService media, Action<string, bool> say)
    {
        if (WheelSlots.Find(id) is not { } action) return false;
        switch (id)
        {
            case "playpause": media.TogglePlayPause(); return true;
            case "next": media.Next(); return true;
            case "previous": media.Previous(); return true;
            case "ask":
                if (HostLinks.ListenUntilSilence is { } listen) listen();
                else say("Voice isn't ready", true);
                return true;
            case "translate":
                Translate(say);
                return true;
            case "timer" or "notes" or "mirror" or "clipboard" or "calendar" or "weather" or "mixer" or "snippets":
                return openCard(id);
            case "guide": return openCard(Guide.GuideModule.CardId);
            case "settings": return openCard(Settings.SettingsModule.ModuleName);
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
            _ => action.Words.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? id,
        };
        HostLinks.RunText?.Invoke(text);
        return true;
    }

    /// <summary>Translates the copied text (into the home language, or English when it isn't English), in the results card.</summary>
    static void Translate(Action<string, bool> say)
    {
        // A busy clipboard, or a copy a password manager marked private, counts as nothing copied.
        var text = (ClipboardText.TryGet() ?? "").Trim();
        if (text.Length == 0)
        {
            say("Copy some text, then pick Translate", false);
            return;
        }
        if (text.Length > 500) text = text[..500];
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var language = Core.Conversions.Translation.LanguageName(Core.Conversions.Translation.DefaultTarget(text));
        HostLinks.RunText?.Invoke($"translate {text} to {language}");
    }
}
