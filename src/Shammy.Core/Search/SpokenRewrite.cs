using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

/// <summary>
/// Turns what people actually say into the English the commands already understand, so a spoken (or typed)
/// request reaches a Shammy action instead of a web search: Hinglish ("screenshot le lo", "gaana bajao",
/// "settings kholo"), other wordings ("grab my screen", "don't let my laptop sleep"), politeness and wake words
/// ("hey Shammy, please …"), and the doubled sentence a voice model sometimes returns ("Take a screenshot. Take a
/// screenshot."). <see cref="Candidates"/> lists what to try, best first; the caller runs the first one a command
/// accepts, and searches the web only when none does.
/// </summary>
public static class SpokenRewrite
{
    const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Hinglish verb endings, written loosely: "karo", "kar do", "kardo", "kijiye", "kar dena"…
    const string Karo = @"(?:karo|kar\s*do|kardo|kijiye|kar\s*dijiye|kar\s*dena|kar|karna)";
    const string Do = @"(?:do|dena|dijiye|o)";

    static readonly Regex Lead = new(@"^(?:(?:hey|hi|hello|ok|okay|oye|arre|suno)\s+)?(?:shammy|shami|shammi|shamy)\b(?!'s|\s+(?:ki|ke|ka|settings?|features?)\b)[\s,]*|" +
        @"^(?:hey|ok|okay|oye|arre|suno)\b[\s,]+|" +
        @"^(?:please|plz|pls|kindly|zara|jara|bhai|yaar|can you|could you|would you|will you|can u|i want you to|i want to|i'd like to|i would like to|let's|lets|just)\b[\s,]*", Opts);
    static readonly Regex Tail = new(@"[\s,]+(?:please|plz|pls|for me|na|yaar|jaldi|jaldi se|bhai|now|right now|abhi)$", Opts);

    static readonly (Regex Rx, string To)[] Rules = Build(new (string, string)[]
    {
        // ---- Screenshot
        (@"^(?:ek\s+)?(?:screenshot|screen\s*shot|ss|screen\s+(?:ki|ka)\s+(?:photo|pic|picture|tasveer|screenshot))\s+(?:le|lo|le\s*lo|lelo|le\s*do|ledo|kheecho|khicho|kheench\s*lo|khinch\s*lo|khich\s*lo|maaro|maro|lena|nikalo|" + Karo + ")$", "screenshot"),
        (@"^(?:grab|capture|snap|print|copy)\s+(?:my\s+|the\s+|this\s+)?(?:whole\s+|entire\s+)?screen$", "screenshot"),
        (@"^(?:take|get|make)\s+(?:a\s+)?(?:picture|photo|pic|snapshot|snap|capture|shot|screenshot)\s+of\s+(?:my\s+|the\s+|this\s+)?screen$", "screenshot"),
        (@"^screen\s*capture$", "screenshot"),

        // ---- Screen recording (before "… chalu karo" opens an app)
        (@"^(?:screen\s+)?recording\s+(?:shuru|start|chalu|chaalu|on)\s+" + Karo + "$", "start recording"),
        (@"^(?:screen\s+)?recording\s+(?:band|bandh|stop|rok|roko|khatam|off|ruko)(?:\s+" + Karo + ")?$", "stop recording"),
        (@"^screen\s+(?:record|recording)\s+(?:" + Karo + @"|shuru\s+" + Karo + ")$", "record my screen"),

        // ---- Live captions
        (@"^(?:live\s+)?(?:captions?|subtitles?)\s+(?:chalu|chaalu|on|shuru|start)\s+" + Karo + "$", "turn on captions"),
        (@"^(?:live\s+)?(?:captions?|subtitles?)\s+(?:band|bandh|off|stop|hatao)(?:\s+" + Karo + ")?$", "turn off captions"),
        (@"^(?:turn\s+on|start|show)\s+(?:the\s+)?subtitles?$", "turn on captions"),

        // ---- Music
        (@"^(?:koi\s+|ek\s+|kuch\s+)?(?:gaana|gana|gaane|gane|song|songs|music|geet)\s+(?:chala|chalao|chala\s*" + Do + @"|baja|bajao|baja\s*" + Do + @"|lagao|laga\s*do|play\s+" + Karo + @"|sunao|suna\s*do|chalu\s+" + Karo + @"|on\s+" + Karo + ")$", "play music"),
        (@"^(?:gaana|gana|song|music|gaane)\s+(?:band|bandh|ruko|rok|roko|pause|stop|bas)(?:\s+(?:" + Karo + @"|do))?$", "pause music"),
        (@"^(?:band|bandh|ruko|roko)\s+(?:karo\s+)?(?:gaana|gana|music|song)$", "pause music"),
        (@"^(?:gaana|gana|music|song)\s+(?:wapas|phir\s+se|dobara)\s+(?:chala|chalao|chala\s*do|shuru\s+" + Karo + "|baja|bajao)$", "resume"),
        (@"^(?:agla|agle|next|dusra|doosra)\s+(?:gaana|gana|song|track)(?:\s+(?:chala|chalao|chala\s*do|lagao|laga\s*do|baja|bajao|play\s+" + Karo + "|bajao))?$", "next song"),
        (@"^(?:pichla|pichhla|pichhle|pichle|purana|previous|last)\s+(?:gaana|gana|song|track)(?:\s+(?:chala|chalao|chala\s*do|lagao|laga\s*do|baja|bajao|play\s+" + Karo + "))?$", "previous song"),
        (@"^(?:ye|yeh|ya)\s+(?:kaunsa|konsa|kon\s*sa|kaun\s*sa)\s+(?:gaana|gana|song)\s+(?:hai|he|chal\s+raha\s+hai)$", "what song is this"),
        (@"^(?:what|which)\s+song\s+is\s+(?:this|playing|that|on)$|^what(?:'s|\s+is)\s+(?:this\s+song|playing|on)$|^(?:name\s+this|identify\s+(?:this|the)\s+song)$", "what song is this"),
        // "<artist> ke gaane bajao": the artist, without the Hinglish for "songs by"
        (@"^(.+?)\s+(?:ke|ka|ki)\s+(?:gaane|gane|gaana|gana|songs?)\s+(?:chalao|chala\s*" + Do + @"|bajao|baja\s*" + Do + @"|play\s+" + Karo + @"|sunao|suna\s*do|lagao|laga\s*do)$", "play $1"),
        // "<song> chalao/bajao" (after the music-word rules above)
        (@"^(.+?)\s+(?:chalao|chala\s*" + Do + @"|bajao|baja\s*" + Do + @"|play\s+" + Karo + @"|sunao|suna\s*do)$", "play $1"),

        // ---- Notes said in Hinglish: "note likh lo doodh lana hai", "doodh lana hai note kar lo"
        (@"^(?:ek\s+)?note\s+(?:likh\s*lo|likh\s*do|likho|bana\s*lo|banao|likh|save\s+" + Karo + @"|kar\s*lo|karo)\s+(.+)$", "note $1"),
        (@"^(.+?)\s+(?:ka\s+|ki\s+)?note\s+(?:likh\s*lo|likh\s*do|kar\s*lo|bana\s*lo|save\s+" + Karo + ")$", "note $1"),

        // ---- Weather
        (@"^(?:aaj\s+)?(?:ka\s+)?(?:mausam|mosam|weather)\s+(?:kaisa\s+(?:hai|he)|kya\s+(?:hai|he)|batao|bata\s*do|bolo)$|^(?:aaj\s+)?(?:ka\s+)?mausam$", "weather"),
        (@"^kal\s+(?:ka\s+)?(?:mausam|mosam|weather)(?:\s+(?:kaisa\s+(?:hoga|rahega)|kya\s+hoga|batao))?$", "weather tomorrow"),
        (@"^(?:aaj\s+)?(?:baarish|barish|barsaat)\s+(?:hogi|hoga|aayegi|ayegi|padegi)(?:\s+kya)?$|^kya\s+(?:aaj\s+)?(?:baarish|barish)\s+(?:hogi|aayegi)$", "will it rain today"),
        (@"^kal\s+(?:baarish|barish)\s+(?:hogi|hoga|aayegi|ayegi)(?:\s+kya)?$", "will it rain tomorrow"),
        (@"^(?:bahar\s+)?(?:kitni\s+)?(?:garmi|thand|thandi|temperature|temp)\s+(?:hai|he|kitna\s+hai|kitni\s+hai|kya\s+hai)(?:\s+(?:aaj|bahar))?$", "weather"),

        // ---- Keep awake
        (@"^(?:don'?t|do\s+not)\s+let\s+(?:my\s+|the\s+)?(?:pc|laptop|computer|screen|system|display)\s+(?:sleep|go\s+to\s+sleep|turn\s+off|lock|dim)$", "keep my pc awake"),
        (@"^(?:keep|leave)\s+(?:my\s+|the\s+)?(?:pc|laptop|computer|screen|display)\s+(?:on|awake|from\s+sleeping)$", "keep my pc awake"),
        (@"^(?:pc|laptop|computer|screen|system)\s+(?:ko\s+)?(?:jaga\s*ke|jagaye|jaagta|on|chalu)\s+rakho$|^(?:pc|laptop|screen|computer)\s+(?:ko\s+)?(?:sone|so\s*ne)\s+(?:mat|na)\s+do$", "keep my pc awake"),

        // ---- The pet
        (@"^(?:turn\s+off|hide|remove|disable|switch\s+off|get\s+rid\s+of)\s+(?:the\s+|my\s+)?(?:pet|cat|kitty|billi)$|^(?:pet|cat|billi)\s+(?:band|bandh|off|hatao|chhupao|chupao)(?:\s+" + Karo + ")?$", "pet off"),
        (@"^(?:turn\s+on|show|bring\s+back|enable|switch\s+on)\s+(?:the\s+|my\s+)?(?:pet|cat|kitty|billi)$|^(?:pet|cat|billi)\s+(?:chalu|on|wapas\s+lao|dikhao)(?:\s+" + Karo + ")?$", "pet on"),

        // ---- Cricket
        (@"^(?:(?:india|bharat|ind|match|cricket)(?:\s+(?:ka|ki|ke))?\s+)?score\s+(?:kya\s+(?:hai|hua|chal\s+raha\s+hai)|batao|bata\s*do|kitna\s+(?:hai|hua))$", "cricket score"),
        (@"^(?:what(?:'s|\s+is)\s+the\s+score|(?:india|ind|live|match)\s+score|score|match\s+(?:kaisa\s+chal\s+raha\s+hai|ka\s+haal|update))$", "cricket score"),

        // ---- Shammy's own settings, the guide, what's new
        (@"^(?:open\s+|show\s+)?(?:shammy(?:'s)?|your|apni|apne|tumhari|tumhare)\s+(?:ki\s+|ke\s+)?settings?(?:\s+(?:kholo|khol\s*do|open\s+" + Karo + "|dikhao))?$", "shammy settings"),
        (@"^(?:show\s+(?:me\s+)?)?(?:all\s+)?(?:of\s+)?(?:your|the|shammy'?s)\s+features$|^(?:show\s+(?:me\s+)?|list\s+)?all\s+(?:the\s+)?features$|^what\s+are\s+(?:your|the|shammy'?s)\s+features$|^(?:tum|aap|shammy)\s+kya\s+kya\s+kar\s+(?:sakte|sakti|sakta)\s+(?:ho|hai|hain)$|^kya\s+kya\s+kar\s+(?:sakte|sakti|sakta)\s+(?:ho|hai)$", "what can you do"),
        (@"^what(?:'?s|\s+is)\s+new(?:\s+in\s+shammy)?$|^kya\s+naya\s+(?:hai|aaya\s+hai)$|^(?:show\s+)?(?:the\s+)?(?:latest\s+)?(?:release\s+notes|changelog|updates)$", "what's new"),

        // ---- Simple questions answered on the PC
        (@"^(?:battery|charge|charging)\s+(?:kitni|kitna|kya)\s+(?:hai|he|bachi\s+hai|bacha\s+hai)$|^kitni\s+battery\s+(?:hai|bachi\s+hai)$|^(?:how\s+much\s+)?battery(?:\s+(?:is\s+)?left)?$|^(?:what(?:'s|\s+is)\s+(?:my\s+|the\s+)?)?battery(?:\s+(?:level|percentage|status))?$|^how\s+much\s+(?:charge|battery)\s+(?:is\s+left|do\s+i\s+have)$", "battery"),
        (@"^(?:abhi\s+)?(?:kitne|kitna)\s+(?:baje|bja)\s+(?:hain|hai|he)$|^(?:abhi\s+)?(?:time|samay|waqt)\s+(?:kya|kitna)\s+(?:hua|hai|he|ho\s+gaya)$|^what\s+time\s+is\s+it(?:\s+now)?$|^what(?:'s|\s+is)\s+the\s+time(?:\s+now)?$|^(?:tell\s+me\s+the\s+)?time$|^current\s+time$", "what time is it"),
        (@"^aaj\s+(?:ki\s+|ka\s+)?(?:date|tareekh|tarikh|tithi)\s*(?:kya\s+(?:hai|he))?$|^aaj\s+(?:kaunsa|konsa|kya)\s+(?:din|date)\s+(?:hai|he)$|^what(?:'s|\s+is)\s+(?:the\s+|today'?s\s+)?(?:date|day)(?:\s+today)?$|^what\s+day\s+is\s+(?:it|today)$|^(?:today'?s\s+)?date$", "what's the date"),
        (@"^(?:any|koi)\s+(?:new\s+|naya\s+|naye\s+)?(?:notifications?|messages?|msgs?)(?:\s+(?:aaya|aaye|hai|hain|aayi))?(?:\s+kya)?$|^(?:do\s+i\s+have\s+|did\s+i\s+get\s+)?(?:any\s+)?(?:new\s+)?(?:notifications?|messages?)\??$|^(?:show|check)\s+(?:my\s+)?(?:notifications?|messages?)$", "any new notifications"),
        (@"^(?:read|show|tell)\s+(?:me\s+)?(?:my\s+)?(?:the\s+)?(?:last|latest|recent|newest)\s+(?:message|notification|msg)$|^(?:last|latest|aakhri|akhri|pichla|pichhla)\s+(?:message|msg|notification)\s+(?:padho|batao|sunao|dikhao|kya\s+(?:tha|hai))$", "read my last message"),
        (@"^(?:do\s+i\s+have\s+)?(?:any\s+)?meetings?\s+today$|^do\s+i\s+have\s+(?:any\s+)?meetings?(?:\s+today)?$|^aaj\s+(?:ki\s+|koi\s+|kya\s+)?meetings?\s*(?:hai|hain|he)?(?:\s+kya)?$|^aaj\s+(?:kya|kaun\s*si)\s+meetings?\s+(?:hai|hain)$|^(?:my\s+)?(?:schedule|agenda)(?:\s+today|\s+for\s+today)?$|^aaj\s+ka\s+(?:schedule|plan)(?:\s+kya\s+hai)?$", "what's on my calendar"),

        // ---- Bluetooth devices: the Bluetooth setting, with its switch
        (@"^(?:connect|pair|disconnect|unpair|join)\s+(?:to\s+)?(?:my\s+|the\s+)?(?:earbuds|earphones|headphones|airpods|buds|headset|bluetooth\s+\w+|speaker|speakers)$|^(?:earbuds|headphones|airpods|buds|headset)\s+(?:connect|jodo|jod\s*do)(?:\s+" + Karo + ")?$", "bluetooth"),

        // ---- Opening things (last: "… kholo" is the most general Hinglish ending)
        (@"^(.+?)\s+(?:kholo|khol\s*do|kholdo|kholna|khol\s*dijiye|open\s+" + Karo + @"|launch\s+" + Karo + @"|start\s+" + Karo + ")$", "open $1"),
    });

    static readonly (Regex, string)[] Loanwords = new (string, string)[]
    {
        (@"\bs[ck]rin\s*a?\s*sh[oa]t\b", "screenshot"), (@"\bs[ck]rin\b", "screen"), (@"\bri[ck]ordi?nga?\b", "recording"),
        (@"\bmyu[zj]i[ck]\b", "music"), (@"\bseti?nga?s?\b", "settings"), (@"\b[ck]aip?shana?\b", "captions"),
        (@"\bbai?tari\b|\bbatri\b", "battery"), (@"\btaima?\b", "time"), (@"\btaimara?\b", "timer"), (@"\bnotifi[ck]eshana?\b", "notification"),
        (@"\bmesej\b|\bmaisej\b", "message"), (@"\bvolyuma?\b", "volume"), (@"\bvhatsaipa?\b|\bvhatsep\b", "whatsapp"),
    }.Select(x => (new Regex(x.Item1, Opts), x.Item2)).ToArray();

    static (Regex, string)[] Build((string Pattern, string To)[] rules) =>
        rules.Select(r => (new Regex(r.Pattern, Opts), r.To)).ToArray();

    /// <summary>
    /// What to try for <paramref name="text"/>, best first and without repeats: the text as said (tidied), then
    /// each rewrite that applies. Never empty for non-blank text.
    /// </summary>
    public static IReadOnlyList<string> Candidates(string? text)
    {
        var list = new List<string>();
        void Add(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = Spaces(s);
            if (!list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
        }
        if (string.IsNullOrWhiteSpace(text)) return list;

        var raw = Spaces(text.Trim().TrimEnd('.', '!', '?', '।').Trim());
        var sentences = Sentences(text).ToList();
        // A rule names what was meant ("note likh lo X" is "note X"), so it goes ahead of the words as said;
        // a phrase said twice is tried once.
        foreach (var sentence in sentences)
        {
            var tidy = Tidy(sentence);
            if (Rewrite(tidy) is { } english) Add(english);
            if (sentences.Count == 1 || sentence != sentences[0]) Add(tidy);
        }
        bool doubled = sentences.Count == 1 && !string.Equals(Tidy(sentences[0]), Tidy(raw), StringComparison.OrdinalIgnoreCase);
        if (!doubled) Add(raw);
        foreach (var sentence in sentences) Add(Tidy(sentence));
        return list;
    }

    /// <summary>The single best rewrite for <paramref name="text"/>, or null when no rule applies.</summary>
    public static string? Rewrite(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = Tidy(text);
        foreach (var (rx, to) in Rules)
        {
            var m = rx.Match(t);
            if (!m.Success) continue;
            var result = m.Result(to).Trim();
            return result.Length == 0 ? null : result;
        }
        return null;
    }

    /// <summary>Lower case, Devanagari spelt out, punctuation gone, wake and polite words trimmed, "screen shot" joined.</summary>
    public static string Tidy(string text)
    {
        var t = text.Trim();
        if (Devanagari.Contains(t))
        {
            t = Devanagari.ToLatin(t).Replace("aa", "a").Replace("ee", "i").Replace("oo", "u");
            // English words written in Devanagari come back sounding out ("skrinashot"): spell the common ones.
            foreach (var (rx, word) in Loanwords) t = rx.Replace(t, word);
        }
        t = t.ToLowerInvariant().Replace('’', '\'');
        t = Regex.Replace(t, @"[^\p{L}\p{N}':.\s]", " ");
        t = Regex.Replace(t, @"(?<!\d)\.|\.(?!\d)", " ");
        t = Spaces(t);
        // Trim wake and polite words from both ends until nothing changes ("hey shammy, please, …").
        for (int i = 0; i < 4; i++)
        {
            var before = t;
            t = Spaces(Lead.Replace(t, ""));
            t = Spaces(Tail.Replace(t, ""));
            if (t == before) break;
        }
        t = Regex.Replace(t, @"\bscreen\s+shot\b", "screenshot");
        return t;
    }

    /// <summary>
    /// The sentences of what was heard, each once, last first (the one people end on is usually the request):
    /// "Take a screenshot. Take a screenshot." is one sentence; "Screen shot. Take a screenshot." is two.
    /// </summary>
    static IEnumerable<string> Sentences(string text)
    {
        var parts = Regex.Split(text, @"(?<=[.!?।])\s+|[.!?।]+$")
            .Select(p => p.Trim().TrimEnd('.', '!', '?', '।').Trim())
            .Where(p => p.Length > 0)
            .ToList();
        // A whole phrase said twice without a full stop: "play sleepwalk play sleepwalk".
        if (parts.Count == 1 && Halves(parts[0]) is { } half) parts = new List<string> { half };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = parts.Where(p => seen.Add(Tidy(p))).ToList();
        if (unique.Count == 1) return unique;
        // The whole text first (it may be one request split oddly), then the sentences from the last.
        var all = new List<string> { string.Join(" ", unique) };
        for (int i = unique.Count - 1; i >= 0; i--) all.Add(unique[i]);
        return all;
    }

    static string? Halves(string s)
    {
        var w = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 2 || w.Length % 2 != 0) return null;
        int n = w.Length / 2;
        for (int i = 0; i < n; i++)
            if (!string.Equals(w[i].Trim(',', '.'), w[i + n].Trim(',', '.'), StringComparison.OrdinalIgnoreCase)) return null;
        return string.Join(' ', w.Take(n));
    }

    static string Spaces(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
