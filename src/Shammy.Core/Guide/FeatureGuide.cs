namespace Shammy.Core.Guide;

/// <summary>How a feature is switched on and off from the guide.</summary>
public enum GuideSwitch
{
    /// <summary>Always there (the keys, the palette): the guide only explains it.</summary>
    None,

    /// <summary>A whole module, off from the next start (Settings › General › Features).</summary>
    Module,

    /// <summary>One yes/no setting, at once.</summary>
    Setting,
}

/// <summary>
/// One line of the guide: what the feature is called, what it does, the words that start it, what "Try it" types
/// for you, the everyday words people use for it, and how it is switched.
/// </summary>
/// <param name="Id">Stable name; what "used" is remembered by.</param>
/// <param name="Phrase">Shown after "Say or type".</param>
/// <param name="Try">The text "Try it" runs as if typed, or "card:&lt;id&gt;" to open a card; empty when there is nothing to start.</param>
/// <param name="Words">Other words for it, for the finder.</param>
/// <param name="Starts">Typed or spoken text beginning with one of these counts as having used it.</param>
/// <param name="Aliases">Single words or short names that, typed alone, mean this feature.</param>
/// <param name="NewIn">The version that brought it ("0.6"), or empty.</param>
public sealed record GuideFeature(string Id, int Group, string Name, string Line, string Phrase, string Try, string Words,
    string[] Starts, string[] Aliases, GuideSwitch Switch = GuideSwitch.None, string SwitchName = "", string NewIn = "");

/// <summary>
/// Everything Shammy does, listed once: the guide in the open notch shows this, the finder searches it, and the
/// palette uses it to answer "what can you do". Kept by hand, next to the features it describes; a test checks
/// that every module that can be switched off appears here.
/// </summary>
public static class FeatureGuide
{
    public static readonly IReadOnlyList<string> Groups = new[]
    {
        "Ask and search", "Stay on top", "Music and sound", "Screen and calls", "Files and phone", "Your PC", "The pet", "Extras",
    };

    static GuideFeature F(string id, int group, string name, string line, string phrase, string @try, string words, string starts = "", string aliases = "",
        GuideSwitch sw = GuideSwitch.None, string switchName = "", string newIn = "") =>
        new(id, group, name, line, phrase, @try, words, Split(starts), Split(aliases), sw, switchName, newIn);

    static string[] Split(string list) => list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static readonly IReadOnlyList<GuideFeature> All = new[]
    {
        F("talk", 0, "Talk to it", "Hold the key and ask a question or say a command.", "hold Right Alt", "",
            "voice speak ask microphone say question"),
        F("hinglish", 0, "Say it your way", "Hinglish and everyday words work, like “screenshot le lo”.", "screenshot le lo", "",
            "hindi hinglish natural words understand speak way language bolo"),
        F("speaksee", 0, "Speak and see", "Your words show while you talk, with what letting go will do.", "hold Right Alt and talk", "",
            "live words preview see transcript listening while talking"),
        F("choices", 0, "Did you mean", "Not sure what you meant? It offers choices, not a search. Press 1–4.", "grab the screen and record it", "",
            "choices suggestions unsure mean pick options confused"),
        F("pcanswers", 0, "Answers on your PC", "Battery, time, date, new messages and what's playing, no search.", "battery kitni hai · what time is it", "",
            "battery charge time clock date day notifications messages last message playing song"),
        F("wheel", 0, "The wheel", "Hold the middle mouse button: eight actions around the pointer.", "hold the middle mouse button", "",
            "wheel radial menu circle middle mouse button pie quick actions shortcuts fingertips", "", "", GuideSwitch.Setting, "WheelEnabled"),
        F("palette", 0, "Command palette", "Apps, files, settings and commands rank as you type.", "tap Right Alt", "",
            "type search find anything launcher box"),
        F("circle", 0, "Circle to search", "Circle anything on screen to search it.", "Copilot key, or hold Right Ctrl", "",
            "what is this picture image screen google lens identify"),
        F("answers", 0, "Instant answers", "Units, currency, maths and time zones.", "5 ft in cm · 100 usd to inr", "",
            "calculate calculator convert rupees dollars maths units time zone", sw: GuideSwitch.Module, switchName: "selection"),
        F("translate", 0, "Translate", "Google Translate, right in the notch.", "translate good morning to hindi", "",
            "language hindi english meaning", "translate"),

        F("messages", 1, "Messages in the notch", "Notifications open here; swipe one away and the next comes up.", "hover to hold · swipe to dismiss", "",
            "notifications whatsapp chat alerts popups mute pile", sw: GuideSwitch.Setting, switchName: "ShowNotifications"),
        F("timers", 1, "Timers and stopwatch", "A live countdown with pause beside the notch.", "timer 25m tea", "timer 5m",
            "countdown cooking tea minutes stopwatch time me", "timer|stopwatch|set a timer", "timer|timers|stopwatch", GuideSwitch.Module, "productivity"),
        F("alarms", 1, "Alarms", "Repeating alarms that ring until you stop or snooze them.", "wake me up at 6:30 every weekday", "alarms",
            "wake up morning ring clock", "set an alarm|wake me|alarms|snooze", "alarm|alarms"),
        F("reminders", 1, "Reminders and notes", "A card when it is due; notes you can type into.", "remind me at 6pm to call mom", "card:notes",
            "remember forget later todo write down list", "remind me|note:|note ", "reminder|reminders|notes"),
        F("focus", 1, "Pomodoro and focus", "Focus sessions that keep the notch quiet.", "pomodoro · focus 45m", "pomodoro",
            "concentrate study work deep distraction session", "pomodoro|focus ", "pomodoro"),
        F("calendar", 1, "Calendar", "Your next meeting, with Join five minutes before.", "what's next", "calendar",
            "meeting schedule today next event agenda", "calendar|my meetings|what's next|whats next", "calendar", GuideSwitch.Module, "calendar"),
        F("eyebreaks", 1, "Eye breaks", "Every 20 minutes the pet rests its eyes with you.", "take an eye break", "take an eye break",
            "eyes tired strain rest screen health look away", "take an eye break|eye break", "eye break|eye breaks", GuideSwitch.Setting, "EyeBreaksEnabled", "0.6"),
        F("water", 1, "Water reminders", "A gentle reminder every hour and a half.", "Settings › Notch & pet", "",
            "drink thirsty hydrate health", "", "water", GuideSwitch.Setting, "WaterRemindersEnabled", "0.6"),

        F("music", 2, "Music", "The song's cover as a spinning record, with the line being sung.", "play kesariya · pause · next", "card:music",
            "song play spotify youtube album cover disc record player", "play |pause|next|previous", "music|song|songs", GuideSwitch.Module, "music", "0.6"),
        F("lyrics", 2, "Lyrics", "Synced lyrics in the Music view, and the line being sung under the record.", "lyrics", "lyrics",
            "words of the song sing along karaoke", "lyrics", "lyrics", GuideSwitch.Setting, "LyricLineEnabled"),
        F("mixer", 2, "Volume mixer", "A slider for each app that is making sound.", "volume mixer", "volume mixer",
            "loud quiet sound one app louder lower audio per app game too loud", "volume mixer|mixer", "mixer", GuideSwitch.Module, "mixer", "0.6"),
        F("levels", 2, "Volume and brightness", "Set a level directly.", "volume 50 · brightness to 70%", "",
            "louder quieter dim screen bright", "volume |brightness |mute", "", GuideSwitch.Module, "levels"),

        F("record", 3, "Screen recording", "The whole screen or one window, saved as an MP4.", "record my screen", "record my screen",
            "record save capture video film what is on my screen clip demo tutorial", "record my screen|record the screen|record screen|stop recording", "record|recording|screen recording", GuideSwitch.Module, "record", "0.6"),
        F("captions", 3, "Live captions", "Whatever the PC is playing, written under the notch. Made offline.", "live captions", "live captions",
            "subtitles words text hear deaf understand video accent transcript what are they saying", "live captions|captions", "captions|subtitles", GuideSwitch.Module, "captions", "0.6"),
        F("teleprompter", 3, "Teleprompter", "Your script under the camera, hidden from screen shares.", "teleprompter from clipboard", "teleprompter",
            "script read speech camera presentation lines notes while talking", "teleprompter|close teleprompter", "teleprompter|prompter", GuideSwitch.Module, "prompter", "0.6"),
        F("meetings", 3, "Meetings", "A mic button and the time on the call.", "mute mic", "",
            "call zoom meet teams mic mute microphone", "mute mic|unmute mic", "", GuideSwitch.Module, "meeting"),
        F("mirror", 3, "Mirror", "A quick look in the webcam before a call.", "mirror", "mirror",
            "camera how do i look webcam face", "mirror|how do i look|open camera", "mirror", GuideSwitch.Module, "mirror"),
        F("screenshots", 3, "Screenshots", "Catches what you capture: copy text, search, send.", "take a screenshot", "",
            "capture picture snip", "take a screenshot", "", GuideSwitch.Module, "screenshot"),

        F("shelf", 4, "Shelf", "Park files on the notch and drag them out later.", "drag files onto the notch", "",
            "keep files hold temporary park drag drop", "", "shelf", GuideSwitch.Module, "dropactions"),
        F("phone", 4, "Send to phone", "Over your own Wi-Fi with a QR code. No app, no account.", "send to phone", "send to phone",
            "airdrop transfer mobile share photos qr", "send to phone", "", GuideSwitch.Module, "phonesend"),
        F("clipboard", 4, "Clipboard history", "What you copied, with pinning and search.", "clipboard", "clipboard",
            "copy paste copied earlier lost history", "clipboard|clear clipboard", "clipboard", GuideSwitch.Module, "clipboard"),
        F("snippets", 4, "Snippets", "Short triggers that expand in any app.", ";date · ;shrug", "snippets",
            "shortcut typing expand signature faster abbreviation", "snippet|snippets", "snippets", GuideSwitch.Module, "snippets"),

        F("quick", 5, "Quick actions", "Keep awake, lock, sleep and mute in one card.", "quick actions · keep awake", "quick actions",
            "stop pc sleeping stay on awake lock screen away dont sleep caffeine", "quick actions|keep awake|stay awake|lock my pc", "quick actions|keep awake", GuideSwitch.Module, "quick", "0.6"),
        F("tuck", 5, "Tuck it away", "Click the small tab under the notch and it slides away for a few seconds.", "the tab under the notch", "",
            "hide move out of the way covering behind under tab title reach click underneath", "", "", GuideSwitch.Setting, "TuckAwayEnabled", "0.6"),
        F("launcher", 5, "App launcher", "Apps, sites and folders, typos forgiven.", "open spotify", "",
            "open start run program website folder", "open ", "", GuideSwitch.Module, "launcher"),
        F("system", 5, "System", "CPU, memory and battery, with alerts.", "cpu", "cpu",
            "slow battery memory performance hot fan", "cpu|system", "", GuideSwitch.Module, "system"),

        F("pet", 6, "Shammy the cat", "Ears, whiskers and a purr. Or just the eyes.", "pet on · pet off", "",
            "eyes ears purr cute animal mascot hide kitten", "pet on|pet off", "cat|pet|purr", GuideSwitch.Setting, "PetEnabled", "0.6"),
        F("cattheme", 6, "Cat theme", "A cat that listens while you talk, and paws on the choices.", "Settings › Notch & pet", "",
            "paws paw cat theme kitty listening ears", "", "", GuideSwitch.Setting, "CatTheme"),

        F("claude", 7, "Claude Code", "Sessions in the notch, with Allow and Deny.", "Settings › General › Connect", "",
            "coding agent ai approve allow deny terminal", "", "claude code", GuideSwitch.Module, "agents", "0.6"),
        F("cricket", 7, "Live cricket", "The score while India is playing.", "ticker cricket", "",
            "score india match", "ticker cricket", "cricket", GuideSwitch.Setting, "LiveCricketEnabled", "0.6"),
        F("onlinevoice", 7, "Online voice", "Speaks timers aloud with your own Gnani key.", "Settings › Voice", "",
            "speak aloud read out voice hinglish gnani", "", "", GuideSwitch.Module, "onlinevoice", "0.6"),
        F("weather", 7, "Weather and tickers", "Weather, and cricket, stocks or crypto in the resting notch.", "weather in mumbai tomorrow", "weather",
            "rain temperature forecast stocks crypto price", "weather|ticker ", "weather", GuideSwitch.Module, "ambient"),
    };

    public static GuideFeature? ById(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>"0.6.0" → "0.6": what the New filter and badges compare against.</summary>
    public static string Line(string? version)
    {
        var parts = (version ?? "").Split('.', '+', '-');
        return parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _) ? parts[0] + "." + parts[1] : "";
    }

    public static bool IsNew(GuideFeature feature, string? version) => feature.NewIn.Length > 0 && feature.NewIn == Line(version);
}

/// <summary>One match from the finder, best first.</summary>
public readonly record struct GuideMatch(GuideFeature Feature, int Score);

/// <summary>
/// Finds features from everyday words: "save what is on my screen" finds Screen recording. It matches words
/// (a feature's name counts most, then its own list of other words for it); it does not understand sentences.
/// Offline and instant.
/// </summary>
public static class FeatureFinder
{
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "i", "my", "me", "the", "a", "an", "to", "do", "how", "can", "want", "is", "on", "of", "it", "for", "and", "what", "with", "in", "that",
        "you", "shammy", "please", "are", "they", "this", "be", "there", "way", "does", "something", "some", "when", "too", "one", "cant", "t", "s",
    };

    /// <summary>The words of a request that carry meaning, lower case.</summary>
    public static IReadOnlyList<string> Words(string? text)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        foreach (char c in (text ?? "").ToLowerInvariant() + " ")
        {
            if (char.IsLetterOrDigit(c)) { word.Append(c); continue; }
            if (word.Length > 1 && !Skip.Contains(word.ToString())) words.Add(word.ToString());
            word.Clear();
        }
        return words;
    }

    /// <summary>Matching ignores a last letter, so "recording", "records" and "record" meet.</summary>
    static string Stem(string word) => word.EndsWith("ing", StringComparison.Ordinal) && word.Length > 6 ? word[..^3]
        : word.Length > 4 ? word[..^1] : word;

    static bool Has(string hay, string stem)
    {
        // Whole words or their beginnings only: "eat" must not find "feature".
        int at = 0;
        while ((at = hay.IndexOf(stem, at, StringComparison.Ordinal)) >= 0)
        {
            if (at == 0 || !char.IsLetterOrDigit(hay[at - 1])) return true;
            at += stem.Length;
        }
        return false;
    }

    public static int Score(GuideFeature feature, IReadOnlyList<string> words)
    {
        string name = feature.Name.ToLowerInvariant();
        string own = feature.Words.ToLowerInvariant();
        string hay = (feature.Line + " " + feature.Phrase + " " + FeatureGuide.Groups[feature.Group]).ToLowerInvariant();
        int score = 0;
        foreach (var word in words)
        {
            string stem = Stem(word);
            // Its name counts most, then the words chosen for it, then anything in its description.
            if (Has(name, stem)) score += 3;
            else if (Has(own, stem)) score += 2;
            else if (Has(hay, stem)) score += 1;
        }
        return score;
    }

    /// <summary>The features matching <paramref name="query"/>, best first; all of them, in guide order, for an empty query.</summary>
    public static IReadOnlyList<GuideMatch> Find(string? query, IEnumerable<GuideFeature>? among = null)
    {
        var features = among ?? FeatureGuide.All;
        var words = Words(query);
        if (words.Count == 0) return features.Select(f => new GuideMatch(f, 0)).ToList();
        return features.Select(f => new GuideMatch(f, Score(f, words))).Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>The one feature a request clearly means, or null when nothing stands out.</summary>
    public static GuideFeature? Best(string? query)
    {
        var found = Find(query);
        if (found.Count == 0 || Words(query).Count == 0) return null;
        // Two meaningful hits, or a word from its name, and ahead of the runner-up.
        if (found[0].Score < 2) return null;
        if (found.Count > 1 && found[1].Score == found[0].Score && found[0].Score < 3) return null;
        return found[0].Feature;
    }

    /// <summary>The feature whose commands this typed or spoken text belongs to ("timer 5m" → timers), if any.</summary>
    public static GuideFeature? UsedBy(string? text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        return FeatureGuide.All.FirstOrDefault(f => f.Starts.Any(s =>
            s.EndsWith(' ') || s.EndsWith(':') ? t.StartsWith(s, StringComparison.Ordinal) : t == s || t.StartsWith(s + " ", StringComparison.Ordinal)));
    }
}

/// <summary>What was asked of the guide.</summary>
/// <param name="Query">Words to search for; empty for the whole guide.</param>
/// <param name="Feature">The feature it clearly means, when there is one.</param>
public sealed record GuideRequest(string Query, GuideFeature? Feature);

/// <summary>
/// The typed and spoken ways into the guide: "help", "features", "what can you do", a feature's own short name
/// ("mixer", "captions"), and questions about Shammy itself ("how do I record my screen"). A question only counts
/// when it clearly means one feature, so "how do I make tea" still goes to the web.
/// </summary>
public static class GuidePhrases
{
    static readonly string[] Open =
    {
        "help", "guide", "features", "feature guide", "commands", "what can you do", "what can shammy do", "what do you do", "show features",
        "show me what you can do", "what all can you do", "kya kar sakte ho",
    };

    static readonly string[] Questions = { "how do i ", "how can i ", "how to ", "can you ", "can shammy ", "do you have ", "is there a way to ", "where is ", "where are " };

    public static GuideRequest? Parse(string? text)
    {
        string t = (text ?? "").Trim().TrimEnd('?', '.', '!').Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Length > 120) return null;
        if (Open.Contains(t)) return new GuideRequest("", null);

        var alias = FeatureGuide.All.FirstOrDefault(f => f.Aliases.Contains(t));
        if (alias is not null) return new GuideRequest(alias.Name, alias);

        foreach (var q in Questions)
        {
            if (!t.StartsWith(q, StringComparison.Ordinal)) continue;
            string rest = t[q.Length..];
            return FeatureFinder.Best(rest) is { } feature ? new GuideRequest(rest, feature) : null;
        }
        return null;
    }
}

/// <summary>What is going on at the PC, as far as the guide's suggestions care.</summary>
public sealed record GuideMoment(bool SoundPlaying, bool InCall, bool LateNight, bool AgentWorking);

/// <summary>One suggestion: the feature and why now.</summary>
public readonly record struct GuideSuggestion(GuideFeature Feature, string Why);

/// <summary>
/// The "Right now" line of the guide: a few features that fit the moment (a video playing, a call, late night),
/// and otherwise something you have not tried yet. Features that are switched off are never suggested.
/// </summary>
public static class GuideSuggestions
{
    public const int Max = 3;

    public static (string Moment, IReadOnlyList<GuideSuggestion> Suggestions) For(GuideMoment now, Func<string, bool> used, Func<GuideFeature, bool> isOn)
    {
        var picks = new List<(string Id, string Why)>();
        string moment;
        if (now.InCall)
        {
            moment = "You are on a call";
            picks.AddRange(new[] { ("teleprompter", "your notes under the camera"), ("record", "keep a copy of the call"), ("captions", "captions for what they say") });
        }
        else if (now.AgentWorking)
        {
            moment = "Claude Code is working";
            picks.AddRange(new[] { ("focus", "a quiet focus session"), ("eyebreaks", "rest your eyes while it works") });
        }
        else if (now.SoundPlaying)
        {
            moment = "Something is playing";
            picks.AddRange(new[] { ("captions", "captions for what is playing"), ("mixer", "turn just this app down"), ("lyrics", "the words, in time") });
        }
        else if (now.LateNight)
        {
            moment = "It is late";
            picks.AddRange(new[] { ("alarms", "set tomorrow's alarm"), ("quick", "keep the PC awake, or put it to sleep") });
        }
        else
        {
            moment = "Not tried yet";
        }

        var chosen = picks.Select(p => (Feature: FeatureGuide.ById(p.Id), p.Why)).Where(p => p.Feature is not null && isOn(p.Feature))
            .Select(p => new GuideSuggestion(p.Feature!, p.Why)).ToList();
        // Fill up with things never tried that can actually be started from here.
        foreach (var f in FeatureGuide.All)
        {
            if (chosen.Count >= Max) break;
            if (f.Try.Length == 0 || used(f.Id) || !isOn(f) || chosen.Any(c => c.Feature.Id == f.Id)) continue;
            chosen.Add(new GuideSuggestion(f, "you have not tried this yet"));
        }
        return (moment, chosen.Take(Max).ToList());
    }
}
