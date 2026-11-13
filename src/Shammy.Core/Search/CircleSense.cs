using System.Globalization;
using System.Text.RegularExpressions;
using Shammy.Core.Music;
using Shammy.Core.Notch;

namespace Shammy.Core.Search;

/// <summary>What a circled thing is, read on this PC: a phone number, an email, a link, a date, an address, a sum or a colour.</summary>
public enum CircleKind { Phone, Email, Link, When, Address, Sum, Colour }

/// <param name="Value">What actions use: digits with a leading + for a phone, the address, a full https URL, "#RRGGBB"…</param>
/// <param name="Display">What the island shows big ("+91 98765 43210", "Fri 21 Nov · 6:30 PM", "= 84").</param>
/// <param name="Label">The quiet line above it ("Phone number", "Date and time", "12 × 7").</param>
public sealed record CircleMeaning(CircleKind Kind, string Value, string Display, string Label)
{
    /// <summary>The point in time for <see cref="CircleKind.When"/>; local time.</summary>
    public DateTime? At { get; init; }

    /// <summary>False when only a day was circled (no clock time): a reminder then lands at 9 AM, a calendar entry is all-day.</summary>
    public bool HasTime { get; init; }

    /// <summary>For a date with words around it ("Dentist · Fri 21 Nov 6 PM"): those words, as the reminder's text.</summary>
    public string? Title { get; init; }

    /// <summary>The colour for <see cref="CircleKind.Colour"/>.</summary>
    public Rgb? Colour { get; init; }
}

/// <summary>
/// Reads what was circled without sending it anywhere. Only a selection that is mostly one thing is read (a phone
/// number with "Call us:" before it still is; a paragraph that happens to contain one is not), so an ordinary circle
/// still goes to search. Never throws.
/// </summary>
public static class CircleSense
{
    const int MaxText = 240;

    static readonly RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(50);

    static readonly Regex EmailRx = new(@"(?<![\w.%+-])[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,24}(?![\w-])", Opt, Budget);

    static readonly Regex UrlRx = new(
        @"(?<![@\w.-])(?:(?:https?://|www\.)[^\s<>""'`]+|[a-z0-9][a-z0-9-]*(?:\.[a-z0-9-]+)*\.(?:com|org|net|io|dev|in|co|app|ai|gov|edu|me|info|xyz|tech|so|gg|tv|uk|us|ly|to|sh|page|site|store|blog|news)(?::\d{2,5})?(?:/[^\s<>""'`]*)?)(?![\w@])",
        Opt, Budget);

    static readonly Regex PhoneRx = new(@"(?<![\w+])(?:\+|00)?\(?\d[\d\s().\-]{5,}\d(?!\w)", Opt, Budget);

    static readonly Regex HexRx = new(@"^#?([0-9a-f]{6}|[0-9a-f]{3}|[0-9a-f]{8})$", Opt, Budget);
    static readonly Regex RgbRx = new(@"^rgba?\(\s*(\d{1,3})\s*,?\s*(\d{1,3})\s*,?\s*(\d{1,3})\s*(?:[,/]\s*[\d.]+%?\s*)?\)$", Opt, Budget);
    static readonly Regex HslRx = new(@"^hsla?\(\s*(\d{1,3}(?:\.\d+)?)(?:deg)?\s*,?\s*(\d{1,3}(?:\.\d+)?)%\s*,?\s*(\d{1,3}(?:\.\d+)?)%\s*(?:[,/]\s*[\d.]+%?\s*)?\)$", Opt, Budget);

    static readonly Regex TimeRx = new(@"(?<![\d:])(\d{1,2})(?:[:.](\d{2}))?\s*([ap])\.?\s?m\.?(?![a-z])|(?<![\d:.])([01]?\d|2[0-3]):([0-5]\d)(?::[0-5]\d)?(?![\d:])", Opt, Budget);
    static readonly Regex IsoDateRx = new(@"(?<!\d)(\d{4})-(\d{1,2})-(\d{1,2})(?:[T ](\d{1,2}):(\d{2}))?(?!\d)", Opt, Budget);
    static readonly Regex NumericDateRx = new(@"(?<![\d/.-])(\d{1,2})([/.\-])(\d{1,2})\2(\d{4}|\d{2})(?![\d/.-])", Opt, Budget);
    static readonly Regex DayMonthRx = new(@"(?<!\w)(\d{1,2})(?:st|nd|rd|th)?\s*(?:of\s+)?(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?(?:,?\s*(\d{4}))?(?!\w)", Opt, Budget);
    static readonly Regex MonthDayRx = new(@"(?<!\w)(jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s*(\d{1,2})(?:st|nd|rd|th)?(?:,?\s*(\d{4}))?(?!\w)", Opt, Budget);
    static readonly Regex RelativeDayRx = new(@"(?<!\w)(today|tonight|tomorrow|tmrw)(?!\w)", Opt, Budget);
    static readonly Regex WeekdayRx = new(@"(?<!\w)(mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun)(?:day|nesday|rsday|urday|sday)?\.?(?!\w)", Opt, Budget);
    static readonly Regex NoiseRx = new(@"(?<!\w)(?:at|on|from|by|ist|gmt|utc|pst|pdt|est|edt|cst|cdt|bst|cet|cest)(?!\w)|[·|,–—@()\-]", Opt, Budget);

    static readonly Regex PinRx = new(@"(?<!\d)(?:\d{6}|\d{3}\s\d{3}|\d{5}(?:-\d{4})?)(?!\d)|\b[A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2}\b", RegexOptions.CultureInvariant, Budget);
    static readonly Regex StreetRx = new(
        @"(?<!\w)(?:road|rd|street|st|avenue|ave|lane|ln|marg|nagar|colony|sector|block|floor|flat|building|bldg|apartment|apt|apartments|suite|near|opp|opposite|cross|main|layout|phase|plot|house|highway|hwy|blvd|boulevard|drive|dr|way|place|pl|square|sq|chowk|bazaar|market|enclave|vihar|puram|pur|ganj|society|tower|complex|residency|estate)\.?(?!\w)",
        Opt, Budget);

    static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4, ["may"] = 5, ["jun"] = 6,
        ["jul"] = 7, ["aug"] = 8, ["sep"] = 9, ["sept"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12,
    };

    /// <summary>Calling codes for local numbers (no + in front), by region; WhatsApp needs the full international number.</summary>
    static readonly Dictionary<string, string> CallingCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IN"] = "91", ["US"] = "1", ["CA"] = "1", ["GB"] = "44", ["AE"] = "971", ["AU"] = "61", ["SG"] = "65",
        ["DE"] = "49", ["FR"] = "33", ["NP"] = "977", ["BD"] = "880", ["LK"] = "94", ["PK"] = "92", ["SA"] = "966",
        ["NZ"] = "64", ["IE"] = "353", ["MY"] = "60", ["ZA"] = "27", ["NG"] = "234", ["KE"] = "254",
    };

    /// <summary>What <paramref name="text"/> is, or null for anything that should simply be searched.</summary>
    /// <param name="culture">Decides whether 03/04 is 3 April (most places) or March 4 (US); the PC's culture by default.</param>
    public static CircleMeaning? Read(string? text, DateTime now, CultureInfo? culture = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var flat = Regex.Replace(text.ReplaceLineEndings(" "), @"\s+", " ").Trim();
            if (flat.Length == 0 || flat.Length > MaxText) return null;
            flat = DigitFix(flat);
            culture ??= CultureInfo.CurrentCulture;
            return Email(flat) ?? Link(flat) ?? ColourText(flat) ?? Phone(flat) ?? When(flat, now, culture) ?? Sum(flat) ?? Address(flat);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Screen text reading mistakes inside numbers: "+9l" is "+91", "4O" is "40". Only a run that is all digits and
    /// look-alikes (l, I, |, O, o) with a + in front or at least two real digits is touched; words are left alone.
    /// </summary>
    public static string DigitFix(string text)
    {
        var words = text.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var core = w.TrimStart('+', '(').TrimEnd(')', ',', '.', ';', ':');
            if (core.Length < 2) continue;
            int digits = core.Count(char.IsAsciiDigit);
            if (digits == 0 || digits == core.Length || !core.All(c => char.IsAsciiDigit(c) || c is 'l' or 'I' or '|' or 'O' or 'o')) continue;
            if (!w.StartsWith('+') && digits < 2) continue;
            words[i] = w.Replace('l', '1').Replace('I', '1').Replace('|', '1').Replace('O', '0').Replace('o', '0');
        }
        return string.Join(' ', words);
    }

    // ---- Each kind -------------------------------------------------------------------------------------------

    /// <summary>How much of the text a match covers, ignoring spaces; "mostly one thing" is at least this.</summary>
    const double Mostly = 0.55;

    static double Cover(string match, string text)
    {
        int all = text.Count(c => !char.IsWhiteSpace(c));
        return all == 0 ? 0 : match.Count(c => !char.IsWhiteSpace(c)) / (double)all;
    }

    /// <summary>The single match that is most of the text, or with a few words of lead-in ("Email:", "Call us on").</summary>
    static Match? Single(Regex rx, string text)
    {
        var all = rx.Matches(text);
        if (all.Count != 1) return null;
        var m = all[0];
        var rest = (text[..m.Index] + " " + text[(m.Index + m.Length)..]).Trim();
        int restWords = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return Cover(m.Value, text) >= Mostly || restWords <= 3 ? m : null;
    }

    static CircleMeaning? Link(string text)
    {
        if (Single(UrlRx, text) is not { } m) return null;
        var url = m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '\'', '"');
        if (url.Length < 4) return null;
        var full = url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "https://" + url;
        if (!Uri.TryCreate(full, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.Contains('.')) return null;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return new CircleMeaning(CircleKind.Link, uri.AbsoluteUri, host + (uri.AbsolutePath.Length > 1 ? Short(uri.AbsolutePath, 28) : ""), "Link");
    }

    static CircleMeaning? Email(string text)
    {
        // OCR sometimes spaces out "name @ site.com"; read it closed up when the whole circle is that short.
        var m = Single(EmailRx, text);
        if (m is null && text.Split(' ').Length <= 4) m = Single(EmailRx, Regex.Replace(text, @"\s*([@.])\s*", "$1"));
        if (m is null) return null;
        var address = m.Value.Trim('.');
        return new CircleMeaning(CircleKind.Email, address, address, "Email address");
    }

    static CircleMeaning? Phone(string text)
    {
        if (Single(PhoneRx, text) is not { } m) return null;
        var raw = m.Value.Trim();
        if (IsoDateRx.IsMatch(raw) || NumericDateRx.IsMatch(raw) || TimeRx.IsMatch(raw)) return null;
        // A decimal number ("3.14159265") or a sum is not a phone number.
        if (Regex.IsMatch(raw, @"^\d+\.\d+$")) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        bool plus = raw.StartsWith('+') || raw.StartsWith("00", StringComparison.Ordinal);
        if (raw.StartsWith("00", StringComparison.Ordinal)) digits = digits[2..];
        bool grouped = Regex.IsMatch(raw, @"[\s().\-]");
        bool ok = plus ? digits.Length is >= 8 and <= 15 : digits.Length is >= 10 and <= 12 || (grouped && digits.Length is >= 7 and <= 12 && raw.Contains('('));
        if (!ok) return null;
        // A long run with no grouping at all ("202611211830") is more likely an id than a number to call.
        if (!plus && !grouped && digits.Length > 11) return null;
        return new CircleMeaning(CircleKind.Phone, (plus ? "+" : "") + digits, raw, "Phone number");
    }

    /// <summary>The number WhatsApp's link takes (country code, no +), or null when a local number's country is unknown.</summary>
    public static string? WhatsAppNumber(string phoneValue, string? region)
    {
        if (phoneValue.StartsWith('+')) return phoneValue[1..];
        var local = phoneValue.TrimStart('0');
        if (region is null || !CallingCodes.TryGetValue(region, out var code)) return null;
        // Already starts with the country's code at full length ("919876543210").
        if (local.Length > 10 && local.StartsWith(code, StringComparison.Ordinal)) return local;
        return local.Length is >= 7 and <= 11 ? code + local : null;
    }

    static CircleMeaning? ColourText(string text)
    {
        var t = text.Trim().TrimEnd(';', '.', ',');
        if (t.Length > 40) return null;
        Rgb? c = null;
        if (HexRx.Match(t) is { Success: true } hex && (t.StartsWith('#') || Regex.IsMatch(hex.Groups[1].Value, "[a-f]", RegexOptions.IgnoreCase)))
        {
            var h = hex.Groups[1].Value;
            if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
            if (h.Length == 8) h = h[..6];
            // A plain 6-digit number ("123456") is a PIN or an id, not a colour; it needs the # or a letter.
            c = Notch.Colour.Parse(h);
        }
        else if (RgbRx.Match(t) is { Success: true } rgb)
        {
            int r = int.Parse(rgb.Groups[1].Value, CultureInfo.InvariantCulture), g = int.Parse(rgb.Groups[2].Value, CultureInfo.InvariantCulture),
                b = int.Parse(rgb.Groups[3].Value, CultureInfo.InvariantCulture);
            if (r > 255 || g > 255 || b > 255) return null;
            c = new Rgb((byte)r, (byte)g, (byte)b);
        }
        else if (HslRx.Match(t) is { Success: true } hsl)
        {
            double hue = double.Parse(hsl.Groups[1].Value, CultureInfo.InvariantCulture), s = double.Parse(hsl.Groups[2].Value, CultureInfo.InvariantCulture),
                l = double.Parse(hsl.Groups[3].Value, CultureInfo.InvariantCulture);
            if (s > 100 || l > 100) return null;
            c = Notch.Colour.FromHsl(hue, s / 100, l / 100);
        }
        return c is { } colour ? ColourMeaning(colour) : null;
    }

    public static CircleMeaning ColourMeaning(Rgb c) =>
        new(CircleKind.Colour, "#" + c.Hex.TrimStart('#').ToUpperInvariant(), "#" + c.Hex.TrimStart('#').ToUpperInvariant(),
            string.Create(CultureInfo.InvariantCulture, $"Colour · rgb({c.R}, {c.G}, {c.B})")) { Colour = c };

    /// <summary>
    /// The colour of a circled swatch: when one colour fills most of the picture (BGRA pixels), that colour; null for a
    /// photo, an icon or text on a background.
    /// </summary>
    public static CircleMeaning? Swatch(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width < 2 || height < 2 || bgra.Length < width * height * 4) return null;
        const int Bits = 4, Shift = 8 - Bits;
        var count = new int[1 << (3 * Bits)];
        int seen = 0;
        // Every other pixel is plenty, and keeps a big circle cheap.
        int step = width * height > 40_000 ? 2 : 1;
        for (int y = 0; y < height; y += step)
        for (int x = 0; x < width; x += step)
        {
            int i = (y * width + x) * 4;
            if (bgra[i + 3] < 128) continue;
            seen++;
            count[((bgra[i + 2] >> Shift) << (2 * Bits)) | ((bgra[i + 1] >> Shift) << Bits) | (bgra[i] >> Shift)]++;
        }
        if (seen == 0) return null;
        int best = 0;
        for (int b = 1; b < count.Length; b++) if (count[b] > count[best]) best = b;
        // Neighbouring bins too, so a colour sitting on a bin edge (or a gentle gradient) still counts as one.
        int near = 0;
        int br = best >> (2 * Bits), bg = (best >> Bits) & 0xF, bb = best & 0xF;
        for (int dr = -1; dr <= 1; dr++)
        for (int dg = -1; dg <= 1; dg++)
        for (int db = -1; db <= 1; db++)
        {
            int r = br + dr, g = bg + dg, b = bb + db;
            if (r is < 0 or > 15 || g is < 0 or > 15 || b is < 0 or > 15) continue;
            near += count[(r << (2 * Bits)) | (g << Bits) | b];
        }
        if (near < seen * 0.88 || count[best] < seen * 0.5) return null;
        long sr = 0, sg = 0, sb = 0;
        int n = 0;
        for (int y = 0; y < height; y += step)
        for (int x = 0; x < width; x += step)
        {
            int i = (y * width + x) * 4;
            if (bgra[i + 3] < 128) continue;
            if (((bgra[i + 2] >> Shift) << (2 * Bits) | (bgra[i + 1] >> Shift) << Bits | bgra[i] >> Shift) != best) continue;
            sr += bgra[i + 2];
            sg += bgra[i + 1];
            sb += bgra[i];
            n++;
        }
        return ColourMeaning(new Rgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n)));
    }

    static CircleMeaning? Sum(string text)
    {
        if (text.Length > 120 || !Calculator.TryEvaluate(text, out var value, out _)) return null;
        // One number with a sign ("-5", "50%") is just a number; a sum has two, or a function.
        if (Regex.Matches(text, @"\d+(?:[.,]\d+)?").Count < 2 && !Regex.IsMatch(text, "sqrt|sin|cos|tan|log|ln|abs|round|floor|ceil", Opt)) return null;
        // "2 + 2" and "18% of 2400" yes; a sentence with a number and a dash in it no.
        if (Regex.Matches(text, @"[a-z]{3,}", Opt).Count(m => !Regex.IsMatch(m.Value, "^(?:of|sqrt|sin|cos|tan|log|abs|round|floor|ceil|lakhs?|crores?|plus|minus|times|divided|by|what|is|calculate)$", Opt)) > 0)
            return null;
        return new CircleMeaning(CircleKind.Sum, Calculator.Format(value), Calculator.Format(value), Calculator.DisplayForm(text));
    }

    static CircleMeaning? Address(string text)
    {
        if (text.Length is < 14 or > 220 || text.Contains('@') || text.Contains("://", StringComparison.Ordinal)) return null;
        if (!Regex.IsMatch(text, "[a-z]{3,}", Opt)) return null;
        int score = 0;
        if (PinRx.IsMatch(text)) score += 2;
        score += Math.Min(2, StreetRx.Matches(text).Count);
        if (text.Count(c => c == ',') >= 2) score++;
        if (Regex.IsMatch(text, @"^(?:\d+[a-z]?(?:[/-]\d+)?[,\s]|no\.?\s*\d|#\s*\d|flat|house|plot)", Opt)) score++;
        // An address is a handful of lines, not a paragraph.
        if (text.Split(' ').Length > 30) score -= 2;
        if (score < 3) return null;
        return new CircleMeaning(CircleKind.Address, text, Short(text, 46), "Address");
    }

    // ---- Dates and times ---------------------------------------------------------------------------------------

    static CircleMeaning? When(string text, DateTime now, CultureInfo culture)
    {
        if (text.Length > 120) return null;
        var rest = text;
        DateTime? date = null;
        bool dayKnown = false;

        // The time first, so "18:30" is never read as part of a date.
        (int H, int M)? clock = null;
        var times = TimeRx.Matches(rest);
        if (times.Count > 1) return null;
        if (times.Count == 1)
        {
            var t = times[0];
            int h, m;
            if (t.Groups[3].Success)
            {
                h = int.Parse(t.Groups[1].Value, CultureInfo.InvariantCulture);
                m = t.Groups[2].Success ? int.Parse(t.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                if (h is < 1 or > 12 || m > 59) return null;
                h = h % 12 + (char.ToLowerInvariant(t.Groups[3].Value[0]) == 'p' ? 12 : 0);
            }
            else
            {
                h = int.Parse(t.Groups[4].Value, CultureInfo.InvariantCulture);
                m = int.Parse(t.Groups[5].Value, CultureInfo.InvariantCulture);
            }
            clock = (h, m);
            rest = Cut(rest, t);
        }

        if (IsoDateRx.Match(rest) is { Success: true } iso)
        {
            date = MakeDate(Int(iso, 1), Int(iso, 2), Int(iso, 3));
            if (iso.Groups[4].Success && clock is null) clock = (Int(iso, 4), Int(iso, 5));
            rest = Cut(rest, iso);
        }
        else if (NumericDateRx.Match(rest) is { Success: true } num)
        {
            int a = Int(num, 1), b = Int(num, 3), y = Int(num, 4);
            if (y < 100) y += 2000;
            bool monthFirst = culture.DateTimeFormat.ShortDatePattern.TrimStart().StartsWith("M", StringComparison.OrdinalIgnoreCase);
            // 25/12 can only be day first, 12/25 only month first, whatever the PC says.
            if (a > 12) monthFirst = false;
            else if (b > 12) monthFirst = true;
            date = monthFirst ? MakeDate(y, a, b) : MakeDate(y, b, a);
            rest = Cut(rest, num);
        }
        else if (DayMonthRx.Match(rest) is { Success: true } dm)
        {
            date = Upcoming(now, MonthOf(dm.Groups[2].Value),
                Int(dm, 1), dm.Groups[3].Success ? Int(dm, 3) : null);
            rest = Cut(rest, dm);
        }
        else if (MonthDayRx.Match(rest) is { Success: true } md)
        {
            date = Upcoming(now, MonthOf(md.Groups[1].Value),
                Int(md, 2), md.Groups[3].Success ? Int(md, 3) : null);
            rest = Cut(rest, md);
        }
        else if (RelativeDayRx.Match(rest) is { Success: true } rel)
        {
            var word = rel.Value.ToLowerInvariant();
            date = now.Date.AddDays(word is "tomorrow" or "tmrw" ? 1 : 0);
            if (word == "tonight" && clock is { H: < 12 } c) clock = (c.H + 12, c.M);
            rest = Cut(rest, rel);
        }
        if (date is not null) dayKnown = true;

        // A weekday on its own ("Fri 6 PM") is the next one; next to a date it's just decoration.
        if (WeekdayRx.Match(rest) is { Success: true } wd)
        {
            if (date is null)
            {
                var day = WeekdayOf(wd.Groups[1].Value);
                int ahead = ((int)day - (int)now.DayOfWeek + 7) % 7;
                date = now.Date.AddDays(ahead);
                dayKnown = true;
            }
            rest = Cut(rest, wd);
        }

        if (!dayKnown && clock is null) return null;
        if (dayKnown && date is null) return null;

        // Whatever is left is a few words of title ("Dentist"), or noise; too much and it's a sentence, not a date.
        var title = Regex.Replace(NoiseRx.Replace(rest, " "), @"\s+", " ").Trim(' ', '.', ':', ';');
        int titleWords = title.Length == 0 ? 0 : title.Split(' ').Length;
        if (titleWords > 6) return null;
        // A bare clock time with words around it ("version 2:30 of the build") is too weak a signal.
        if (!dayKnown && titleWords > 3) return null;
        if (Regex.IsMatch(title, @"\d")) return null;

        DateTime at;
        if (date is { } d)
        {
            at = clock is { } c ? d.Date.AddHours(c.H).AddMinutes(c.M) : d.Date;
        }
        else
        {
            var (h, m) = clock!.Value;
            at = now.Date.AddHours(h).AddMinutes(m);
            if (at <= now) at = at.AddDays(1);
        }

        var shown = clock is null
            ? at.ToString(at.Year == now.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture)
            : (at.Date == now.Date ? "Today" : at.Date == now.Date.AddDays(1) ? "Tomorrow" : at.ToString(at.Year == now.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture))
              + " · " + at.ToString(at.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture);
        return new CircleMeaning(CircleKind.When, at.ToString("s", CultureInfo.InvariantCulture), shown, title.Length > 0 ? title : clock is null ? "Date" : "Date and time")
        {
            At = at,
            HasTime = clock is not null,
            Title = title.Length > 0 ? title : null,
        };
    }

    static int MonthOf(string word) => Months[word[..3]];

    static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);

    static string Cut(string text, Match m) => text[..m.Index] + " " + text[(m.Index + m.Length)..];

    static DateTime? MakeDate(int year, int month, int day) =>
        year is >= 1900 and <= 2200 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, day) : null;

    /// <summary>A day and month with no year: this year's, unless that was more than a few weeks ago (then next year's).</summary>
    static DateTime? Upcoming(DateTime now, int month, int day, int? year)
    {
        if (year is { } y) return MakeDate(y, month, day);
        var thisYear = MakeDate(now.Year, month, day);
        if (thisYear is { } t && t < now.Date.AddDays(-30)) return MakeDate(now.Year + 1, month, day);
        return thisYear;
    }

    static DayOfWeek WeekdayOf(string word) => word.ToLowerInvariant()[..2] switch
    {
        "mo" => DayOfWeek.Monday,
        "tu" => DayOfWeek.Tuesday,
        "we" => DayOfWeek.Wednesday,
        "th" => DayOfWeek.Thursday,
        "fr" => DayOfWeek.Friday,
        "sa" => DayOfWeek.Saturday,
        _ => DayOfWeek.Sunday,
    };

    static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    // ---- Calendar file -----------------------------------------------------------------------------------------

    /// <summary>
    /// A one-event calendar file (.ics) for <paramref name="m"/>: Outlook, the Windows calendar or whichever app opens
    /// .ics files adds it after the user confirms there. An hour long, or all day when no time was circled.
    /// </summary>
    public static string CalendarFile(CircleMeaning m, DateTime nowUtc, string uid)
    {
        if (m.At is not { } at) throw new ArgumentException("Not a date", nameof(m));
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").ReplaceLineEndings("\\n");
        var title = Esc(m.Title ?? "Reminder");
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Shammy//Circle to search//EN", "CALSCALE:GREGORIAN", "BEGIN:VEVENT",
            "UID:" + uid,
            "DTSTAMP:" + nowUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
        };
        if (m.HasTime)
        {
            lines.Add("DTSTART:" + at.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
            lines.Add("DTEND:" + at.AddHours(1).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
        }
        else
        {
            lines.Add("DTSTART;VALUE=DATE:" + at.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            lines.Add("DTEND;VALUE=DATE:" + at.AddDays(1).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }
        lines.Add("SUMMARY:" + title);
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines) + "\r\n";
    }
}
