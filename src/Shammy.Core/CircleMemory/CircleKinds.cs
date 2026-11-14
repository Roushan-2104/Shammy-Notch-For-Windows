namespace Shammy.Core.CircleMemory;

/// <summary>The Circles card's filters.</summary>
public enum CircleGroup { All, Times, Translations, Products, Pictures, Questions }

/// <summary>
/// What a remembered circle's kind means: which of the card's filters it belongs to, the words that find it
/// ("flights", "times", "photo"), and the short tag its row shows when there's no picture ("PST", "✈", "?").
/// </summary>
public static class CircleKinds
{
    /// <summary>The kinds the circle flow adds besides the CircleKind names.</summary>
    public const string Picture = "Picture", Translation = "Translation", Product = "Product", Question = "Question", Text = "Text";

    /// <summary>The card's filter capsules, in order, with their labels.</summary>
    public static readonly IReadOnlyList<(CircleGroup Group, string Label)> Filters = new[]
    {
        (CircleGroup.All, "All"), (CircleGroup.Times, "Times"), (CircleGroup.Translations, "Translations"),
        (CircleGroup.Products, "Products"), (CircleGroup.Pictures, "Pictures"), (CircleGroup.Questions, "Questions"),
    };

    /// <summary>The filter a kind belongs to; <see cref="CircleGroup.All"/> when it only shows under All.</summary>
    public static CircleGroup GroupOf(string? kind) => Norm(kind) switch
    {
        "when" or "zonetime" => CircleGroup.Times,
        "foreign" or "translation" => CircleGroup.Translations,
        "product" => CircleGroup.Products,
        "picture" => CircleGroup.Pictures,
        "question" or "term" => CircleGroup.Questions,
        _ => CircleGroup.All,
    };

    /// <summary>Words someone might use for this kind of circle ("flight plane"), matched like the circle's own words.</summary>
    public static string Words(string? kind) => Norm(kind) switch
    {
        "phone" => "phone number call mobile contact",
        "email" => "email mail address",
        "link" => "link url website site web",
        "when" => "time date day when meeting schedule",
        "address" => "address place location map",
        "sum" => "sum math maths calculation number",
        "colour" => "colour color",
        "foreign" or "translation" => "translation translated translate language",
        "zonetime" => "time zone timezone clock",
        "tracking" => "tracking parcel package courier delivery order",
        "flight" => "flight plane airline",
        "pnr" => "pnr train ticket booking railway",
        "upi" => "upi payment pay",
        "ifsc" => "ifsc bank",
        "pin" => "pin pincode postcode",
        "vehicle" => "vehicle car bike number plate",
        "code" => "code otp",
        "term" => "word term meaning question",
        "picture" => "picture image photo pic screenshot",
        "product" => "product shopping shop price buy",
        "question" => "question answer ask",
        _ => "",
    };

    /// <summary>
    /// The short tag a row shows when it has no picture to show: the zone for a time ("PST"), the first characters of
    /// a foreign line ("明日"), a plane for a flight, or a few letters for the kind.
    /// </summary>
    public static string Tag(string? kind, string? words)
    {
        var w = (words ?? "").Trim();
        switch (Norm(kind))
        {
            case "foreign" or "translation":
                var letters = new string(w.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).Take(2).ToArray());
                return letters.Length > 0 ? letters : "Aa";
            case "zonetime":
                var zone = w.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .LastOrDefault(t => t.Length is >= 2 and <= 5 && t.All(char.IsLetter) && t.All(char.IsUpper));
                return zone ?? "TZ";
            case "flight": return "✈";
            case "when": return "Date";
            case "phone": return "Tel";
            case "email": return "@";
            case "link": return "URL";
            case "address": return "Map";
            case "sum": return "=";
            case "colour": return "#";
            case "tracking": return "Box";
            case "pnr": return "PNR";
            case "upi": return "UPI";
            case "ifsc": return "IFSC";
            case "pin": return "PIN";
            case "vehicle": return "Car";
            case "code": return "</>";
            case "term": return "Aa";
            case "question": return "?";
            case "picture": return "Pic";
            case "product": return "Buy";
            default: return "Txt";
        }
    }

    static string Norm(string? kind) => (kind ?? "").Trim().ToLowerInvariant();
}
