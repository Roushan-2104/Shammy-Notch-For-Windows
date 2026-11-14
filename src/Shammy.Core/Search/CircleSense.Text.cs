using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

// Text as a whole: another language, an error or some code, a percentage in a sentence, a word worth explaining.
public static partial class CircleSense
{
    // ---- Another language --------------------------------------------------------------------------------------

    /// <summary>
    /// Text that isn't English: by its script (Devanagari, Japanese, Arabic…) at any length, or, when
    /// <paramref name="latin"/>, a sentence in Spanish, French, German, Portuguese, Italian, Dutch, Indonesian, Malay,
    /// Turkish or romanised Hindi, told by its little words. A single word or a name is never foreign by its words.
    /// </summary>
    static CircleMeaning? Foreign(string raw, string flat, bool latin)
    {
        var lang = ScriptLanguage(flat) ?? (latin ? LatinLanguage(flat) : null);
        if (lang is not { } l) return null;
        int lines = raw.ReplaceLineEndings("\n").Split('\n').Count(x => x.Trim().Length > 0);
        var label = l.Name + " text" + (lines > 1 ? string.Create(CultureInfo.InvariantCulture, $" · {lines} lines") : "");
        return new CircleMeaning(CircleKind.Foreign, raw, Short(flat, 60), label) { Language = l.Name, LanguageCode = l.Code };
    }

    enum Script { Latin, Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada, Malayalam, Sinhala, Thai, Lao, Myanmar, Khmer, Georgian, Armenian, Ethiopic, Hebrew, Greek, Cyrillic, Arabic, Cjk, Other }

    static Script ScriptOf(char c) => c switch
    {
        < 'ɐ' => Script.Latin,
        >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        >= 'Ͱ' and <= 'Ͽ' => Script.Greek,
        >= 'Ѐ' and <= 'ԯ' => Script.Cyrillic,
        >= '԰' and <= '֏' => Script.Armenian,
        >= '֐' and <= '׿' => Script.Hebrew,
        >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => Script.Arabic,
        >= 'ऀ' and <= 'ॿ' => Script.Devanagari,
        >= 'ঀ' and <= '৿' => Script.Bengali,
        >= '਀' and <= '੿' => Script.Gurmukhi,
        >= '઀' and <= '૿' => Script.Gujarati,
        >= '଀' and <= '୿' => Script.Oriya,
        >= '஀' and <= '௿' => Script.Tamil,
        >= 'ఀ' and <= '౿' => Script.Telugu,
        >= 'ಀ' and <= '೿' => Script.Kannada,
        >= 'ഀ' and <= 'ൿ' => Script.Malayalam,
        >= '඀' and <= '෿' => Script.Sinhala,
        >= '฀' and <= '๿' => Script.Thai,
        >= '຀' and <= '໿' => Script.Lao,
        >= 'က' and <= '႟' => Script.Myanmar,
        >= 'Ⴀ' and <= 'ჿ' => Script.Georgian,
        >= 'ᄀ' and <= 'ᇿ' => Script.Cjk,
        >= 'ሀ' and <= '፿' => Script.Ethiopic,
        >= 'ក' and <= '៿' => Script.Khmer,
        >= '぀' and <= 'ヿ' or >= '㄰' and <= '㆏' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿'
            or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= 'ｦ' and <= 'ﾟ' => Script.Cjk,
        _ => Script.Other,
    };

    static (string Name, string Code)? ScriptLanguage(string text)
    {
        var count = new int[(int)Script.Other + 1];
        int kana = 0, hangul = 0, han = 0;
        foreach (var c in text.Length > 3000 ? text[..3000] : text)
        {
            var cat = char.GetUnicodeCategory(c);
            bool letterish = char.IsLetter(c) || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (!letterish) continue;
            var s = ScriptOf(c);
            if (s == Script.Cjk)
            {
                // One CJK character carries about a word; weigh it like two letters.
                count[(int)s] += 2;
                if (c is >= '぀' and <= 'ヿ' or >= 'ｦ' and <= 'ﾟ') kana++;
                else if (c is >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏') hangul++;
                else han++;
            }
            else count[(int)s]++;
        }
        int total = count.Sum() - count[(int)Script.Other];
        int best = 1;
        for (int i = 2; i < (int)Script.Other; i++) if (count[i] > count[best]) best = i;
        // Mostly in that script: a stray "नमस्ते" in an English line, or a "π" in a formula, is not enough.
        if (count[best] < 2 || count[best] < 0.4 * total) return null;
        return (Script)best switch
        {
            Script.Devanagari => text.Contains("आहे", StringComparison.Ordinal) || text.Contains('ळ') ? ("Marathi", "mr") : ("Hindi", "hi"),
            Script.Bengali => ("Bengali", "bn"),
            Script.Gurmukhi => ("Punjabi", "pa"),
            Script.Gujarati => ("Gujarati", "gu"),
            Script.Oriya => ("Odia", "or"),
            Script.Tamil => ("Tamil", "ta"),
            Script.Telugu => ("Telugu", "te"),
            Script.Kannada => ("Kannada", "kn"),
            Script.Malayalam => ("Malayalam", "ml"),
            Script.Sinhala => ("Sinhala", "si"),
            Script.Thai => ("Thai", "th"),
            Script.Lao => ("Lao", "lo"),
            Script.Myanmar => ("Burmese", "my"),
            Script.Khmer => ("Khmer", "km"),
            Script.Georgian => ("Georgian", "ka"),
            Script.Armenian => ("Armenian", "hy"),
            Script.Ethiopic => ("Amharic", "am"),
            Script.Hebrew => ("Hebrew", "he"),
            Script.Greek => ("Greek", "el"),
            Script.Cyrillic => text.IndexOfAny(new[] { 'і', 'ї', 'є', 'ґ', 'І', 'Ї', 'Є', 'Ґ' }) >= 0 ? ("Ukrainian", "uk") : ("Russian", "ru"),
            Script.Arabic => text.IndexOfAny(new[] { 'ٹ', 'ڈ', 'ڑ', 'ں', 'ے', 'ھ' }) >= 0 ? ("Urdu", "ur")
                : text.IndexOfAny(new[] { 'پ', 'چ', 'ژ', 'گ', 'ی' }) >= 0 ? ("Persian", "fa") : ("Arabic", "ar"),
            _ => kana > 0 ? ("Japanese", "ja") : hangul >= han ? ("Korean", "ko") : ("Chinese", "zh"),
        };
    }

    static HashSet<string> Words(string list) => new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    // Little words only; ones English shares ("in", "is", "a", "on", "was", "also", "come") are left out.
    static readonly HashSet<string> EnglishWords = Words(
        "the a an and is are was were of to in on for with that this it you i we they he she be have has not but or at by from as what how why will can do does my your our their his her there which who if so all about just been would could should these those");

    static readonly (string Name, string Code, HashSet<string> Words, string Marks)[] LatinLanguages =
    {
        ("Spanish", "es", Words("el los las del que y en un una por para con es está están son pero como más muy también porque cuando hola gracias qué cómo dónde yo tú usted nosotros sí este esta estos al lo le se su sus hay todo ser fue había desde hasta ahora bien sin sobre entre puede tiene hacer mejor mi la de no ella ellos aquí"), "ñ¿¡"),
        ("French", "fr", Words("le les des du et est une un dans pour pas que qui sur avec ce cette il elle nous vous ils je au aux ne mais ou sont été être avoir très bonjour merci oui non comme plus tout fait c'est j'ai leur aussi où ça la de suis sommes êtes"), "èêëâîïôûœç"),
        ("German", "de", Words("der die das und ist nicht ein eine einen dem den des zu mit auf für von sich auch ich du er sie wir ihr es sind wird werden haben hat bei aus nach wie oder aber noch nur über kann danke bitte guten tag ja nein mein dass im am habe keine kein heute"), "ßäöü"),
        ("Portuguese", "pt", Words("o os as um uma não que e do da dos das em no na nos nas para com por é são está mais muito também mas como você eu ele ela obrigado obrigada olá sim isso este esta ao à foi ser ter tem seu sua quando onde porque então de"), "ãõç"),
        ("Italian", "it", Words("il lo la gli le di che e è un una non con del della dei delle sono mi ti ci si ma anche più molto questo questa ciao grazie sì io tu lui lei noi voi loro nel nella alla al ho ha hanno essere perché quando dove cosa"), "òìù"),
        ("Dutch", "nl", Words("de het een en van dat niet op te met voor zijn er maar ook als bij aan om dan nog wat ik je jij hij zij wij we hebben heeft wordt worden naar uit dit deze hoe waar dank bedankt hallo goed alleen kan moet geen wel weet"), ""),
        ("Indonesian", "id", Words("yang dan di ini itu dengan untuk dari tidak ada akan ke saya kami kita anda kamu dia mereka juga sudah bisa atau pada dalam karena tetapi tapi apa bagaimana terima kasih selamat pagi sangat lebih belum harus oleh seperti sebagai adalah mana tahu"), ""),
        ("Malay", "ms", Words("tak awak sahaja boleh kerana tidak saya yang dan ini itu dengan untuk dari ada akan ke kami kita anda dia mereka juga sudah atau pada dalam apa terima kasih selamat"), ""),
        ("Turkish", "tr", Words("ve bir bu da için ile çok ne mi mı mu değil var yok ben sen biz siz onlar gibi daha ama olarak kadar şey merhaba teşekkür teşekkürler evet hayır nasıl neden nerede şimdi sonra her olan oldu ki misin musun"), "ğşı"),
        ("Hinglish", "hi", Words("kya hai hain nahi nahin nhi kaise kaisa kaisi mujhe tum tumhe aap mein hum tha thi ka ki ke ko aur bhi kuch kyun kyon kab kahan yeh woh accha acha achha theek thik haan bahut bohot yaar raha rahi rahe karna karo gaya gayi hoga chahiye matlab abhi pehle sab mera meri tera teri kaun kitna kitne sakta sakte hoon hu liye wala wali kal aaj"), ""),
    };

    /// <summary>Little words that turn up inside names in any language ("Juan de la Cruz", "van Gogh"); alone they prove nothing.</summary>
    static readonly HashSet<string> NameParticles = Words("de la da di del le van von der den du dos das el al bin");

    static readonly Regex LatinWordRx = new(@"[\p{L}']+", RegexOptions.CultureInvariant, Budget);

    static (string Name, string Code)? LatinLanguage(string text)
    {
        if (text.Any(c => char.IsLetter(c) && ScriptOf(c) != Script.Latin)) return null;
        var words = LatinWordRx.Matches(text.ToLowerInvariant()).Select(m => m.Value.Trim('\'')).Where(w => w.Length > 0).ToList();
        if (words.Count < 2) return null;
        int english = words.Count(EnglishWords.Contains);
        (string Name, string Code)? best = null;
        int bestScore = 0;
        foreach (var (name, code, list, marks) in LatinLanguages)
        {
            var hits = words.Where(list.Contains).ToList();
            if (hits.Count == 0) continue;
            int score = hits.Count + (marks.Length > 0 && text.ToLowerInvariant().IndexOfAny(marks.ToCharArray()) >= 0 ? 1 : 0);
            // Romanised Hindi needs a real sentence: "Kaise" or "Rahul Sharma" alone stays English.
            if (code == "hi" && (words.Count < 3 || hits.Count < 2)) continue;
            if (hits.All(NameParticles.Contains)) continue;
            if (score < 2 || hits.Count * 5 < words.Count) continue;
            if (score > bestScore) (best, bestScore) = ((name, code), score);
        }
        return bestScore > english ? best : null;
    }

    // ---- Errors and code ---------------------------------------------------------------------------------------

    static readonly (Regex Rx, string? Name)[] ErrorSigns =
    {
        (new(@"Traceback \(most recent call last\)", RegexOptions.CultureInvariant, Budget), "Python error"),
        (new(@"npm ERR!", RegexOptions.CultureInvariant, Budget), "npm error"),
        (new(@"(?<!\w)[Ee]rror\s+([A-Z]{1,4}\d{3,5})(?!\w)", RegexOptions.CultureInvariant, Budget), null),
        (new(@"(?<!\w)(?:[A-Z][A-Za-z0-9]*\.)*((?:[A-Z][a-z0-9]+)+(?:Exception|Error))(?!\w)", RegexOptions.CultureInvariant, Budget), null),
        (new(@"(?<!\w)Exception(?: in thread|:)", RegexOptions.CultureInvariant, Budget), "Exception"),
        (new(@"(?<!\w)at\s+[\w$<>`\[\]]+(?:\.[\w$<>`\[\]]+)+\s*\([^)\n]*\)(?:\s+in\s+\S+:line\s+\d+)?", RegexOptions.CultureInvariant, Budget), "Stack trace"),
        (new(@"File ""[^""\n]+"", line \d+", RegexOptions.CultureInvariant, Budget), "Python error"),
        (new(@"(?:^|\n|\s)fatal:\s", RegexOptions.CultureInvariant, Budget), "Fatal error"),
        (new(@"(?<!\w)Uncaught(?!\w)", RegexOptions.CultureInvariant, Budget), "Uncaught error"),
        (new(@"segmentation fault|core dumped", Opt, Budget), "Segmentation fault"),
        (new(@"(?:^|\n)panic:\s", RegexOptions.CultureInvariant, Budget), "Panic"),
        (new(@"cannot read propert(?:y|ies) of (?:undefined|null)|is not a function|is not defined(?!\w)", Opt, Budget), "Error"),
        (new(@"(?:^|\n|[.!]\s)error\s*:", Opt, Budget), "Error"),
    };

    static readonly Regex[] CodeSigns =
    {
        new(@"(?<!\w)def\s+\w+\s*\(", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)function\s*\w*\s*\([^)]*\)\s*\{", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)(?:const|let|var)\s+\w+\s*=", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)return\b[^.\n]*;", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)import\s+[\w.{}*, ]+(?:\s+from\s+['""]|;|\s*$)", RegexOptions.CultureInvariant | RegexOptions.Multiline, Budget),
        new(@"#include\s*[<""]", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)(?:public|private|protected|internal|static)\s+(?:[\w<>\[\],]+\s+)+\w+\s*\(", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)class\s+\w+\s*(?:[:({]|extends|implements)", RegexOptions.CultureInvariant, Budget),
        new(@"console\.log\(|System\.out\.print|printf\(|print\(|Console\.Write", RegexOptions.CultureInvariant, Budget),
        new(@"(?<!\w)(?:if|while|for|switch)\s*\([^)]*\)\s*\{", RegexOptions.CultureInvariant, Budget),
        new(@"=>|===|!==|\+\+|&&|\|\||::", RegexOptions.CultureInvariant, Budget),
        new(@"\w+\.\w+\([^)]*\)\s*;", RegexOptions.CultureInvariant, Budget),
        new(@"</?[a-z][a-z0-9]*(?:\s+[a-z-]+=""[^""]*"")*\s*/?>", RegexOptions.CultureInvariant, Budget),
    };

    static CircleMeaning? Code(string raw, string flat)
    {
        if (raw.Length < 4) return null;
        string? name = null;
        foreach (var (rx, sign) in ErrorSigns)
        {
            if (rx.Match(raw) is not { Success: true } m) continue;
            name = sign ?? (m.Groups[1].Value.StartsWith("Error", StringComparison.Ordinal) || char.IsDigit(m.Groups[1].Value[^1])
                ? "Error " + m.Groups[1].Value
                : m.Groups[1].Value);
            break;
        }
        if (name is null)
        {
            int signs = CodeSigns.Count(rx => rx.IsMatch(raw));
            int marks = raw.Count(c => c is '{' or '}' or ';');
            int lines = raw.ReplaceLineEndings("\n").Split('\n').Count(l => l.Trim().Length > 0);
            bool code = signs >= 2 || (signs >= 1 && marks >= 2) || (marks >= 4 && lines >= 2);
            if (!code) return null;
            name = "Code";
        }
        // A Python traceback's own error, not "Python error", when it names one.
        if (name == "Python error" && Regex.Matches(raw, @"(?<!\w)((?:[A-Z][a-z0-9]+)+(?:Error|Exception))(?=:|\s*$)", RegexOptions.CultureInvariant, Budget) is { Count: > 0 } py)
            name = py[^1].Groups[1].Value;
        return new CircleMeaning(CircleKind.Code, raw, name, Headline(raw, name == "Code")) { Detail = name };
    }

    static readonly Regex FrameRx = new(@"\s+at\s+[\w$<>`\[\]]+(?:\.[\w$<>`\[\]]+)+\s*\(|\s+File ""|\s+Traceback \(", RegexOptions.CultureInvariant, Budget);

    /// <summary>The line that says what went wrong ("TypeError: cannot read properties of undefined"), or the first line of code.</summary>
    static string Headline(string raw, bool code)
    {
        var lines = raw.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return "";
        var line = code ? lines[0]
            : lines.FirstOrDefault(l => Regex.IsMatch(l, @"(?:Error|Exception|ERR!|error|fatal|panic)\b", RegexOptions.CultureInvariant, Budget) && !l.StartsWith("Traceback", StringComparison.Ordinal)
                                         && !l.StartsWith("at ", StringComparison.Ordinal) && !l.StartsWith("File \"", StringComparison.Ordinal))
              ?? lines[0];
        // Screen text comes as one line; cut it where the stack frames start.
        if (!code && FrameRx.Match(line) is { Success: true, Index: > 0 } frame) line = line[..frame.Index];
        return Short(Regex.Replace(line, @"\s+", " ").Trim(), 80);
    }

    // ---- Percentages -------------------------------------------------------------------------------------------

    const string Num = @"(?:\d{1,3}(?:,\d{2,3})+|\d+)(?:\.\d+)?|\.\d+";
    const string Cur = @"₹|\$|€|£|¥|rs\.?|inr|usd|eur|gbp|aed";
    const string CurWord = @"rupees?|rs\.?|inr|usd|dollars?|euros?|eur|gbp|pounds?|aed|dirhams?";

    static readonly Regex PercentOfRx = new(
        $@"^(?:(?:what\s+is|what's|whats|calculate|find)\s+)?(?<p>{Num})\s*%\s*(?<op>of|off|off\s+on|discount\s+on|on)\s*(?<c1>{Cur})?\s*(?<n>{Num})\s*(?<c2>{CurWord})?\s*\??$",
        Opt, Budget);

    static readonly Regex PercentAddRx = new(
        $@"^(?:(?:what\s+is|what's|whats|calculate)\s+)?(?<c1>{Cur})?\s*(?<n>{Num})\s*(?<c2>{CurWord})?\s*(?<op>\+|-|–|plus|minus|with|less)\s*(?<p>{Num})\s*%\s*(?<what>gst|tax|vat|tip|service(?:\s+charge)?|cess|discount|off)?\s*\??$",
        Opt, Budget);

    static CircleMeaning? Percent(string text)
    {
        if (text.Length > 80) return null;
        double p, n, value;
        string? currency;
        if (PercentOfRx.Match(text) is { Success: true } of)
        {
            p = Number(of.Groups["p"].Value);
            n = Number(of.Groups["n"].Value);
            currency = CurrencyOf(of.Groups["c1"].Value, of.Groups["c2"].Value);
            var op = Regex.Replace(of.Groups["op"].Value.ToLowerInvariant(), @"\s+", " ");
            value = op is "of" or "on" ? n * p / 100 : n * (1 - p / 100);
        }
        else if (PercentAddRx.Match(text) is { Success: true } add)
        {
            p = Number(add.Groups["p"].Value);
            n = Number(add.Groups["n"].Value);
            currency = CurrencyOf(add.Groups["c1"].Value, add.Groups["c2"].Value);
            var op = add.Groups["op"].Value.ToLowerInvariant();
            var what = add.Groups["what"].Value.ToLowerInvariant();
            bool less = op is "-" or "–" or "minus" or "less" || (op == "with" && what is "discount" or "off");
            value = less ? n * (1 - p / 100) : n * (1 + p / 100);
        }
        else return null;
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;
        if (currency is not null) value = Math.Round(value, 2);
        var plain = Calculator.Format(value);
        var shown = currency is null ? plain : currency + Money(value, currency);
        return new CircleMeaning(CircleKind.Sum, plain, shown, Regex.Replace(Calculator.DisplayForm(text), @"\s+", " "));
    }

    static double Number(string s) => double.Parse(s.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture);

    static string? CurrencyOf(string symbol, string word)
    {
        var s = (symbol.Length > 0 ? symbol : word).ToLowerInvariant().TrimEnd('.');
        return s switch
        {
            "" => null,
            "₹" or "rs" or "inr" or "rupee" or "rupees" => "₹",
            "$" or "usd" or "dollar" or "dollars" => "$",
            "€" or "eur" or "euro" or "euros" => "€",
            "£" or "gbp" or "pound" or "pounds" => "£",
            "¥" => "¥",
            _ => "AED ",
        };
    }

    /// <summary>Rupees in the Indian grouping (₹1,20,000), others in the usual one; paise only when there are some.</summary>
    static string Money(double value, string currency)
    {
        var format = currency == "₹" ? CultureInfo.GetCultureInfo("en-IN").NumberFormat : CultureInfo.InvariantCulture.NumberFormat;
        return value.ToString(value % 1 == 0 ? "#,0" : "#,0.00", format);
    }

    // ---- A word or name worth explaining -----------------------------------------------------------------------

    static readonly Regex TermRx = new(@"^[A-Za-zÀ-ɏ][A-Za-zÀ-ɏ'’\-]*(?:\s+[A-Za-zÀ-ɏ][A-Za-zÀ-ɏ'’\-]*){0,3}$", RegexOptions.CultureInvariant, Budget);

    static readonly HashSet<string> Pronouns = Words("i you we they he she it me us them my your our their this that these those there here please");

    /// <summary>
    /// One to four words that read like a word, a name or a phrase someone wants explained ("photosynthesis", "Ada
    /// Lovelace", "quantitative easing"). One capitalised short word ("Shammy", "Settings") is more often a name on
    /// screen or a button than something to explain, so a single word needs to be lower case or long.
    /// </summary>
    static CircleMeaning? Term(string text)
    {
        var t = text.Trim().Trim('"', '\'', '“', '”', '‘', '’', '(', ')', ',', ':', ';');
        if (t.Length is < 3 or > 48 || !TermRx.IsMatch(t)) return null;
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lower = words.Select(w => w.ToLowerInvariant().Trim('\'', '’', '-')).ToArray();
        int stop = lower.Count(EnglishWords.Contains);
        // "Thank you", "I will call": a phrase with a pronoun in it is talk, not a term.
        if (stop == words.Length || stop * 2 > words.Length || lower.Any(Pronouns.Contains)) return null;
        if (words.Length == 1)
        {
            var w = words[0];
            bool lowerCase = w.All(c => !char.IsUpper(c));
            if (!(lowerCase && w.Length >= 4) && w.Length < 7) return null;
            // ALL CAPS ("SALE", "WARNING") is a label, not a term.
            if (w.Length > 1 && w.All(c => !char.IsLower(c))) return null;
        }
        return new CircleMeaning(CircleKind.Term, t, t, "Word or term");
    }
}
