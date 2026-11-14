using System.Text.RegularExpressions;

namespace Shammy.Core.Search;

// Exact things with a known shape: UPI IDs, PNRs, IFSCs, number plates, PIN codes, parcels and flights.
public static partial class CircleSense
{
    // ---- UPI ID ------------------------------------------------------------------------------------------------

    /// <summary>A UPI ID has no dot after the @ ("name@ybl"), which is what tells it from an email.</summary>
    static readonly Regex UpiRx = new(@"(?<![\w.@-])([a-z0-9][a-z0-9._-]{1,63})@([a-z]{2,20})(?![\w@-])(?!\.[a-z])", Opt, Budget);

    static readonly Dictionary<string, string> UpiHandles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ybl"] = "PhonePe", ["ibl"] = "PhonePe", ["axl"] = "PhonePe",
        ["okhdfcbank"] = "Google Pay", ["okicici"] = "Google Pay", ["oksbi"] = "Google Pay", ["okaxis"] = "Google Pay",
        ["paytm"] = "Paytm", ["ptyes"] = "Paytm", ["ptaxis"] = "Paytm", ["pthdfc"] = "Paytm", ["ptsbi"] = "Paytm",
        ["upi"] = "BHIM", ["apl"] = "Amazon Pay", ["yapl"] = "Amazon Pay", ["rapl"] = "Amazon Pay",
        ["waicici"] = "WhatsApp", ["wahdfcbank"] = "WhatsApp", ["waaxis"] = "WhatsApp", ["wasbi"] = "WhatsApp",
        ["kotak"] = "Kotak Mahindra Bank", ["kmbl"] = "Kotak Mahindra Bank", ["hdfcbank"] = "HDFC Bank", ["hdfc"] = "HDFC Bank",
        ["icici"] = "ICICI Bank", ["sbi"] = "State Bank of India", ["axisbank"] = "Axis Bank", ["axis"] = "Axis Bank",
        ["idfcbank"] = "IDFC FIRST Bank", ["idfcfirst"] = "IDFC FIRST Bank", ["freecharge"] = "Freecharge", ["slice"] = "slice",
        ["ikwik"] = "MobiKwik", ["airtel"] = "Airtel Payments Bank", ["jupiteraxis"] = "Jupiter", ["fam"] = "FamPay",
        ["pnb"] = "Punjab National Bank", ["barodampay"] = "Bank of Baroda", ["yesbank"] = "Yes Bank", ["yesbankltd"] = "Yes Bank",
        ["indus"] = "IndusInd Bank", ["fbl"] = "Federal Bank", ["federal"] = "Federal Bank", ["aubank"] = "AU Small Finance Bank",
        ["rbl"] = "RBL Bank", ["cnrb"] = "Canara Bank", ["unionbank"] = "Union Bank of India", ["boi"] = "Bank of India",
        ["dbs"] = "DBS Bank", ["citi"] = "Citibank", ["hsbc"] = "HSBC", ["sc"] = "Standard Chartered", ["kbl"] = "Karnataka Bank",
        ["superyes"] = "super.money", ["naviaxis"] = "Navi", ["cred"] = "CRED", ["axb"] = "CRED", ["timecosmos"] = "Times Pay",
    };

    static CircleMeaning? Upi(string text)
    {
        var m = Single(UpiRx, text);
        // Screen text sometimes spaces it out ("name @ ybl").
        if (m is null && text.Split(' ').Length <= 4) m = Single(UpiRx, Regex.Replace(text, @"\s*@\s*", "@"));
        if (m is null || !UpiHandles.TryGetValue(m.Groups[2].Value, out var app)) return null;
        var id = m.Groups[1].Value + "@" + m.Groups[2].Value.ToLowerInvariant();
        return new CircleMeaning(CircleKind.Upi, id, id, "UPI ID · " + app) { Detail = app };
    }

    // ---- Indian Railways PNR -----------------------------------------------------------------------------------

    static readonly Regex PnrRx = new(@"(?<!\w)PNR(?:\s*(?:no|num|number|status))?\.?\s*[:#\-]?\s*(\d{3})[\s-]?(\d{7})(?!\d)", Opt, Budget);

    static CircleMeaning? Pnr(string text)
    {
        if (Single(PnrRx, text) is not { } m) return null;
        var pnr = m.Groups[1].Value + m.Groups[2].Value;
        return new CircleMeaning(CircleKind.Pnr, pnr, m.Groups[1].Value + "-" + m.Groups[2].Value, "PNR · Indian Railways") { Carrier = "Indian Railways" };
    }

    // ---- IFSC --------------------------------------------------------------------------------------------------

    static readonly Regex IfscRx = new(@"(?<![A-Za-z0-9])(?:(?i:IFSC(?:\s*code)?)\s*[:\-]?\s*)?([A-Z]{4}0[A-Z0-9]{6})(?![A-Za-z0-9])", RegexOptions.CultureInvariant, Budget);

    static readonly Dictionary<string, string> IfscBanks = new(StringComparer.Ordinal)
    {
        ["HDFC"] = "HDFC Bank", ["ICIC"] = "ICICI Bank", ["SBIN"] = "State Bank of India", ["UTIB"] = "Axis Bank",
        ["KKBK"] = "Kotak Mahindra Bank", ["PUNB"] = "Punjab National Bank", ["BARB"] = "Bank of Baroda", ["CNRB"] = "Canara Bank",
        ["UBIN"] = "Union Bank of India", ["IDIB"] = "Indian Bank", ["IOBA"] = "Indian Overseas Bank", ["YESB"] = "Yes Bank",
        ["INDB"] = "IndusInd Bank", ["IDFB"] = "IDFC FIRST Bank", ["FDRL"] = "Federal Bank", ["KARB"] = "Karnataka Bank",
        ["BKID"] = "Bank of India", ["MAHB"] = "Bank of Maharashtra", ["CBIN"] = "Central Bank of India", ["UCBA"] = "UCO Bank",
        ["PSIB"] = "Punjab & Sind Bank", ["IBKL"] = "IDBI Bank", ["KVBL"] = "Karur Vysya Bank", ["TMBL"] = "Tamilnad Mercantile Bank",
        ["SIBL"] = "South Indian Bank", ["CIUB"] = "City Union Bank", ["RATN"] = "RBL Bank", ["AUBL"] = "AU Small Finance Bank",
        ["DBSS"] = "DBS Bank India", ["HSBC"] = "HSBC", ["SCBL"] = "Standard Chartered", ["CITI"] = "Citibank",
        ["ESFB"] = "Equitas Small Finance Bank", ["UJVN"] = "Ujjivan Small Finance Bank", ["BDBL"] = "Bandhan Bank",
        ["JAKA"] = "J&K Bank", ["DLXB"] = "Dhanlaxmi Bank", ["PYTM"] = "Paytm Payments Bank", ["AIRP"] = "Airtel Payments Bank",
        ["IPOS"] = "India Post Payments Bank", ["FINO"] = "Fino Payments Bank", ["NSPB"] = "NSDL Payments Bank",
        ["KLGB"] = "Kerala Gramin Bank", ["APGB"] = "Andhra Pragathi Grameena Bank", ["SVCB"] = "SVC Co-operative Bank",
        ["COSB"] = "Cosmos Bank", ["SRCB"] = "Saraswat Bank", ["ALLA"] = "Indian Bank (Allahabad)", ["ANDB"] = "Union Bank (Andhra)",
        ["CORP"] = "Union Bank (Corporation)", ["VIJB"] = "Bank of Baroda (Vijaya)", ["SYNB"] = "Canara Bank (Syndicate)",
        ["ORBC"] = "Punjab National Bank (OBC)",
    };

    static CircleMeaning? Ifsc(string text)
    {
        if (Single(IfscRx, text) is not { } m) return null;
        var code = m.Groups[1].Value;
        bool named = m.Value.StartsWith("ifsc", StringComparison.OrdinalIgnoreCase);
        if (!IfscBanks.TryGetValue(code[..4], out var bank))
        {
            if (!named) return null;
            bank = null;
        }
        return new CircleMeaning(CircleKind.Ifsc, code, code, bank is null ? "IFSC code" : "IFSC · " + bank) { Detail = bank };
    }

    // ---- Number plates -----------------------------------------------------------------------------------------

    static readonly Regex PlateRx = new(@"(?<![A-Za-z0-9])([A-Z]{2})[\s.-]?(\d{1,2})[\s.-]?([A-Z]{1,3})[\s.-]?(\d{4})(?![A-Za-z0-9])", Opt, Budget);
    static readonly Regex BharatRx = new(@"(?<![A-Za-z0-9])(\d{2})[\s.-]?BH[\s.-]?(\d{4})[\s.-]?([A-Z]{1,2})(?![A-Za-z0-9])", Opt, Budget);

    static readonly Dictionary<string, string> PlateStates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AN"] = "Andaman & Nicobar", ["AP"] = "Andhra Pradesh", ["AR"] = "Arunachal Pradesh", ["AS"] = "Assam", ["BR"] = "Bihar",
        ["CG"] = "Chhattisgarh", ["CH"] = "Chandigarh", ["DD"] = "Daman & Diu", ["DL"] = "Delhi", ["DN"] = "Dadra & Nagar Haveli",
        ["GA"] = "Goa", ["GJ"] = "Gujarat", ["HP"] = "Himachal Pradesh", ["HR"] = "Haryana", ["JH"] = "Jharkhand",
        ["JK"] = "Jammu & Kashmir", ["KA"] = "Karnataka", ["KL"] = "Kerala", ["LA"] = "Ladakh", ["LD"] = "Lakshadweep",
        ["MH"] = "Maharashtra", ["ML"] = "Meghalaya", ["MN"] = "Manipur", ["MP"] = "Madhya Pradesh", ["MZ"] = "Mizoram",
        ["NL"] = "Nagaland", ["OD"] = "Odisha", ["OR"] = "Odisha", ["PB"] = "Punjab", ["PY"] = "Puducherry", ["RJ"] = "Rajasthan",
        ["SK"] = "Sikkim", ["TN"] = "Tamil Nadu", ["TR"] = "Tripura", ["TS"] = "Telangana", ["TG"] = "Telangana",
        ["UK"] = "Uttarakhand", ["UA"] = "Uttarakhand", ["UP"] = "Uttar Pradesh", ["WB"] = "West Bengal",
    };

    static CircleMeaning? Vehicle(string text)
    {
        if (Single(BharatRx, text) is { } bh)
        {
            var plate = $"{bh.Groups[1].Value} BH {bh.Groups[2].Value} {bh.Groups[3].Value.ToUpperInvariant()}";
            return new CircleMeaning(CircleKind.Vehicle, plate.Replace(" ", ""), plate, "Vehicle · Bharat series") { Detail = "Bharat series" };
        }
        if (Single(PlateRx, text) is not { } m) return null;
        var state = m.Groups[1].Value.ToUpperInvariant();
        if (!PlateStates.TryGetValue(state, out var name)) return null;
        string district = m.Groups[2].Value, series = m.Groups[3].Value.ToUpperInvariant(), number = m.Groups[4].Value;
        // Delhi puts a category letter after the district ("DL 3C AF 0001").
        if (state == "DL" && series.Length >= 2) (district, series) = (district + series[0], series[1..]);
        var shown = $"{state} {district} {series} {number}";
        return new CircleMeaning(CircleKind.Vehicle, shown.Replace(" ", ""), shown, "Vehicle · " + name) { Detail = name };
    }

    // ---- PIN codes ---------------------------------------------------------------------------------------------

    static readonly Regex PinLeadRx = new(@"(?<!\w)(?:pin\s*code|pincode|pin|postal\s*code|post\s*code)(?:\s*no)?\.?\s*[:\-]?\s*([1-9]\d{2})\s?(\d{3})(?!\d)", Opt, Budget);
    static readonly Regex PinPlaceRx = new(@"^((?:[A-Z][a-z]+\.?[\s,]*){1,3}?)\s*[,\-–]?\s*([1-9]\d{2})\s?(\d{3})$", RegexOptions.CultureInvariant, Budget);

    /// <summary>Words that come before a 6-digit number that is not a PIN code ("OTP 482913").</summary>
    static readonly HashSet<string> NotPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "order", "invoice", "otp", "code", "id", "ref", "txn", "account", "no", "number", "ticket", "booking", "pnr", "amount",
        "total", "rs", "inr", "room", "flat", "plot", "version", "build", "page", "item", "sku", "serial", "customer", "user",
        "employee", "roll", "pin", "your", "the", "is", "verification", "password", "passcode", "token",
    };

    static CircleMeaning? Pin(string text)
    {
        string digits;
        if (Single(PinLeadRx, text) is { } lead) digits = lead.Groups[1].Value + lead.Groups[2].Value;
        else if (PinPlaceRx.Match(text) is { Success: true } place && !StreetRx.IsMatch(text)
                 && place.Groups[1].Value.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries) is { Length: >= 1 and <= 3 } words
                 && !words.Any(NotPlaces.Contains))
            digits = place.Groups[2].Value + place.Groups[3].Value;
        else return null;
        var region = PinRegion(digits);
        return new CircleMeaning(CircleKind.Pin, digits, digits, "PIN code · " + region) { Detail = region };
    }

    /// <summary>The state or region of an Indian PIN code, from India Post's postal zones (the first two or three digits).</summary>
    public static string PinRegion(string pin)
    {
        if (pin.Length < 3 || !pin.All(char.IsAsciiDigit)) return "India";
        int two = (pin[0] - '0') * 10 + (pin[1] - '0'), three = two * 10 + (pin[2] - '0');
        if (three == 403) return "Goa";
        if (three is 605 or 607 or 609) return "Puducherry";
        if (three == 682 && pin.StartsWith("6825", StringComparison.Ordinal)) return "Lakshadweep";
        if (three == 744) return "Andaman & Nicobar";
        if (three is 737) return "Sikkim";
        if (three is 194) return "Ladakh";
        if (three is 160) return "Chandigarh";
        if (three is >= 246 and <= 249 || three is 262 or 263) return "Uttarakhand";
        if (three is 396) return "Dadra & Nagar Haveli and Daman & Diu";
        return two switch
        {
            11 => "Delhi",
            12 or 13 => "Haryana",
            14 or 15 or 16 => "Punjab",
            17 => "Himachal Pradesh",
            18 or 19 => "Jammu & Kashmir",
            >= 20 and <= 28 => "Uttar Pradesh",
            >= 30 and <= 34 => "Rajasthan",
            >= 36 and <= 39 => "Gujarat",
            >= 40 and <= 44 => "Maharashtra",
            >= 45 and <= 48 => "Madhya Pradesh",
            49 => "Chhattisgarh",
            50 => "Telangana",
            >= 51 and <= 53 => "Andhra Pradesh",
            >= 56 and <= 59 => "Karnataka",
            >= 60 and <= 66 => "Tamil Nadu",
            >= 67 and <= 69 => "Kerala",
            >= 70 and <= 74 => "West Bengal",
            >= 75 and <= 77 => "Odisha",
            78 => "Assam",
            79 => "North East",
            80 or 81 or 84 or 85 => "Bihar",
            82 or 83 => "Jharkhand",
            >= 90 => "Army Postal Service",
            _ => pin[0] switch
            {
                '5' => "South India",
                '3' => "West India",
                _ => "India",
            },
        };
    }

    // ---- Parcels -----------------------------------------------------------------------------------------------

    static readonly Regex UpsRx = new(@"(?<![A-Za-z0-9])1Z\s?[0-9A-Z]{3}\s?[0-9A-Z]{3}\s?[0-9A-Z]{2}\s?[0-9A-Z]{4}\s?[0-9A-Z]{4}(?![A-Za-z0-9])", Opt, Budget);
    static readonly Regex AmazonRx = new(@"(?<![A-Za-z0-9])TBA\d{12}(?![A-Za-z0-9])", Opt, Budget);
    static readonly Regex PostRx = new(@"(?<![A-Za-z0-9])([A-Z]{2})\s?(\d{3})\s?(\d{3})\s?(\d{3})\s?([A-Z]{2})(?![A-Za-z0-9])", RegexOptions.CultureInvariant, Budget);
    static readonly Regex FedExRx = new(@"(?<!\w)(?:fedex|tracking)(?:\s*(?:no|number|id|#))?\.?\s*[:#\-]?\s*(\d{12}|\d{15})(?!\d)", Opt, Budget);
    static readonly Regex DhlRx = new(@"(?<!\w)DHL(?:\s*(?:express|tracking|waybill|no|number|id|#))*\.?\s*[:#\-]?\s*(\d{10})(?!\d)", Opt, Budget);
    static readonly Regex AnyTrackRx = new(
        @"(?<!\w)(?:tracking|awb|airway\s*bill|consignment|docket|shipment)(?:\s*(?:no|num|number|id|code|#))?\.?\s*[:#\-]?\s*([A-Z0-9][A-Z0-9-]{6,22}[A-Z0-9])(?![A-Za-z0-9])",
        Opt, Budget);
    static readonly Regex CourierRx = new(@"(?<!\w)(blue\s*dart|delhivery|dtdc|ekart|xpressbees|shadowfax|ecom\s*express|india\s*post|speed\s*post|usps|royal\s*mail|aramex|dhl|fedex|ups)(?!\w)", Opt, Budget);

    static readonly Dictionary<string, string> PostCountries = new(StringComparer.Ordinal)
    {
        ["IN"] = "India Post", ["US"] = "USPS", ["GB"] = "Royal Mail", ["CN"] = "China Post", ["CA"] = "Canada Post",
        ["AU"] = "Australia Post", ["DE"] = "Deutsche Post", ["FR"] = "La Poste", ["JP"] = "Japan Post", ["SG"] = "SingPost",
        ["AE"] = "Emirates Post", ["NL"] = "PostNL", ["HK"] = "Hongkong Post", ["LK"] = "Sri Lanka Post", ["NP"] = "Nepal Post",
        ["BD"] = "Bangladesh Post", ["MY"] = "Pos Malaysia", ["NZ"] = "NZ Post", ["IT"] = "Poste Italiane", ["ES"] = "Correos",
    };

    static CircleMeaning? Tracking(string text)
    {
        if (Single(UpsRx, text) is { } ups)
        {
            var n = Squash(ups.Value);
            return Parcel(n, "UPS", "https://www.ups.com/track?tracknum=" + n);
        }
        if (Single(AmazonRx, text) is { } amazon)
        {
            var n = amazon.Value.ToUpperInvariant();
            return Parcel(n, "Amazon", "https://track.amazon.com/tracking/" + n);
        }
        if (Single(PostRx, text) is { } post)
        {
            var n = Squash(post.Value);
            var carrier = PostCountries.TryGetValue(post.Groups[5].Value, out var c) ? c : "Post";
            return Parcel(n, carrier, SearchTrack(n));
        }
        if (Single(DhlRx, text) is { } dhl)
        {
            var n = dhl.Groups[1].Value;
            return Parcel(n, "DHL", "https://www.dhl.com/global-en/home/tracking/tracking-express.html?submit=1&tracking-id=" + n);
        }
        if (Single(FedExRx, text) is { } fedex && (Courier(text) is null or "FedEx"))
        {
            var n = fedex.Groups[1].Value;
            return Parcel(n, "FedEx", "https://www.fedex.com/fedextrack/?trknbr=" + n);
        }
        if (Single(AnyTrackRx, text) is { } any && any.Groups[1].Value.Any(char.IsDigit))
        {
            var n = any.Groups[1].Value.ToUpperInvariant();
            return Parcel(n, Courier(text), SearchTrack(n));
        }
        return null;
    }

    static string? Courier(string text)
    {
        if (CourierRx.Match(text) is not { Success: true } m) return null;
        var w = Regex.Replace(m.Value.ToLowerInvariant(), @"\s+", " ");
        return w switch
        {
            "blue dart" or "bluedart" => "Blue Dart",
            "delhivery" => "Delhivery",
            "dtdc" => "DTDC",
            "ekart" => "Ekart",
            "xpressbees" => "Xpressbees",
            "shadowfax" => "Shadowfax",
            "ecom express" or "ecomexpress" => "Ecom Express",
            "india post" or "indiapost" or "speed post" or "speedpost" => "India Post",
            "usps" => "USPS",
            "royal mail" or "royalmail" => "Royal Mail",
            "aramex" => "Aramex",
            "dhl" => "DHL",
            "fedex" => "FedEx",
            _ => "UPS",
        };
    }

    static CircleMeaning Parcel(string number, string? carrier, string url) =>
        new(CircleKind.Tracking, url, number, carrier is null ? "Tracking number" : "Tracking · " + carrier) { Carrier = carrier, Detail = number };

    static string SearchTrack(string number) => "https://www.google.com/search?q=" + Uri.EscapeDataString("track " + number);

    static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // ---- Flights -----------------------------------------------------------------------------------------------

    static readonly Regex FlightRx = new(@"(?<![A-Za-z0-9])([A-Z0-9]{2})\s?-?(\d{1,4})(?![A-Za-z0-9])", RegexOptions.CultureInvariant, Budget);

    static readonly Dictionary<string, string> Airlines = new(StringComparer.Ordinal)
    {
        ["6E"] = "IndiGo", ["AI"] = "Air India", ["UK"] = "Vistara", ["IX"] = "Air India Express", ["SG"] = "SpiceJet",
        ["QP"] = "Akasa Air", ["I5"] = "AirAsia India", ["G8"] = "Go First", ["9I"] = "Alliance Air", ["S5"] = "Star Air",
        ["EK"] = "Emirates", ["EY"] = "Etihad", ["QR"] = "Qatar Airways", ["SQ"] = "Singapore Airlines", ["BA"] = "British Airways",
        ["LH"] = "Lufthansa", ["AF"] = "Air France", ["KL"] = "KLM", ["UA"] = "United", ["AA"] = "American Airlines",
        ["DL"] = "Delta", ["TK"] = "Turkish Airlines", ["CX"] = "Cathay Pacific", ["QF"] = "Qantas", ["NH"] = "ANA",
        ["JL"] = "Japan Airlines", ["VS"] = "Virgin Atlantic", ["LX"] = "SWISS", ["SV"] = "Saudia", ["WY"] = "Oman Air",
        ["GF"] = "Gulf Air", ["KU"] = "Kuwait Airways", ["FZ"] = "flydubai", ["G9"] = "Air Arabia", ["MH"] = "Malaysia Airlines",
        ["TG"] = "Thai Airways", ["AK"] = "AirAsia", ["UL"] = "SriLankan Airlines", ["BG"] = "Biman Bangladesh",
        ["KE"] = "Korean Air", ["OZ"] = "Asiana", ["CA"] = "Air China", ["MU"] = "China Eastern", ["CZ"] = "China Southern",
        ["AC"] = "Air Canada", ["WN"] = "Southwest", ["B6"] = "JetBlue", ["AS"] = "Alaska Airlines", ["IB"] = "Iberia",
        ["AZ"] = "ITA Airways", ["ET"] = "Ethiopian Airlines", ["MS"] = "EgyptAir", ["VN"] = "Vietnam Airlines",
        ["PR"] = "Philippine Airlines", ["GA"] = "Garuda Indonesia", ["NZ"] = "Air New Zealand", ["FR"] = "Ryanair",
        ["U2"] = "easyJet", ["W6"] = "Wizz Air", ["OS"] = "Austrian", ["SK"] = "SAS", ["AY"] = "Finnair", ["EI"] = "Aer Lingus",
        ["TR"] = "Scoot", ["PG"] = "Bangkok Airways", ["RA"] = "Nepal Airlines", ["H9"] = "Himalaya Airlines",
    };

    static readonly Regex FlightWordRx = new(@"(?<!\w)(?:flight|flt|fl)(?!\w)", Opt, Budget);

    static CircleMeaning? Flight(string text)
    {
        var all = FlightRx.Matches(text).Where(x => Airlines.ContainsKey(x.Groups[1].Value) && !x.Groups[1].Value.All(char.IsDigit)).ToList();
        if (all.Count != 1) return null;
        var m = all[0];
        var rest = (text[..m.Index] + " " + text[(m.Index + m.Length)..]).Trim();
        if (Cover(m.Value, text) < Mostly && rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3) return null;
        string code = m.Groups[1].Value, number = m.Groups[2].Value;
        bool said = FlightWordRx.IsMatch(text);
        // "UK 2024" is a year more often than a flight; say "flight" and it counts.
        if (!said && number.Length == 4 && (number.StartsWith("19", StringComparison.Ordinal) || number.StartsWith("20", StringComparison.Ordinal))) return null;
        // Digits elsewhere mean it's part of something else (a date, an address, a sum).
        if (Regex.IsMatch(rest, @"\d")) return null;
        var airline = Airlines[code];
        var shown = code + " " + number;
        return new CircleMeaning(CircleKind.Flight, "https://www.google.com/search?q=" + Uri.EscapeDataString(shown + " flight status"), shown, "Flight · " + airline)
        {
            Carrier = airline,
            Detail = code + number,
        };
    }
}
