using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Shopping;

/// <summary>One store's offer for a product, as Google Shopping lists it.</summary>
/// <param name="Delivery">"Free delivery", "Free delivery by Fri", "₹40 delivery"…, or "" when not shown.</param>
/// <param name="Rating">The star rating (4.4), or null.</param>
/// <param name="Reviews">The review count as shown ("1.1K"), or "".</param>
public sealed record ShoppingOffer(string Title, decimal Price, string Store, string Delivery, double? Rating, string Reviews)
{
    /// <summary>"₹1,199" in Indian grouping.</summary>
    public string PriceText => ShoppingOffers.Rupees(Price);

    /// <summary>Delivery is free (with or without a date).</summary>
    public bool FreeDelivery => Delivery.StartsWith("Free delivery", StringComparison.OrdinalIgnoreCase) && !Delivery.Contains('+');
}

/// <summary>
/// Reads the offers out of the visible text of a Google Shopping results page (or Lens's product results): each offer is a
/// title line, a "₹1,199" line, perhaps a discount line, the store, perhaps "&amp; more", a delivery line and a
/// "4.8(708)" rating. Pure text work on the PC; never throws.
/// </summary>
public static class ShoppingOffers
{
    static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(300);
    static readonly Regex PriceLine = new(@"^₹\s?([\d,]+(?:\.\d{1,2})?)$", RegexOptions.None, Limit);
    static readonly Regex OldPrice = new(@"^(?:\d{1,2}% off\s*₹[\d,.]+|Usually ₹[\d,.]+|Was ₹[\d,.]+)$", RegexOptions.IgnoreCase, Limit);
    static readonly Regex RatingLine = new(@"^(\d(?:\.\d)?)\s*\(([\d.,]+[KkMm]?)\)$", RegexOptions.None, Limit);
    static readonly Regex DeliveryLine = new(@"^(?:Free delivery.*|₹[\d,]+ delivery.*|Delivery.*|.*\bdelivery\b.*)$", RegexOptions.IgnoreCase, Limit);
    static readonly Regex Noise = new(@"^(?:&\s*more|LOW PRICE|SALE|BEST SELLER|Popular options|More|In stock|Out of stock|Nearby|Sponsored)$", RegexOptions.IgnoreCase, Limit);

    /// <summary>Every offer in <paramref name="pageText"/>, in page order (the same offer can appear twice in different sections).</summary>
    public static IReadOnlyList<ShoppingOffer> Parse(string? pageText)
    {
        var offers = new List<ShoppingOffer>();
        if (string.IsNullOrWhiteSpace(pageText)) return offers;
        try
        {
            var lines = pageText.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            for (int i = 1; i < lines.Count; i++)
            {
                var price = PriceLine.Match(lines[i]);
                if (!price.Success) continue;
                string title = lines[i - 1];
                // A title is words, not another price, a discount or a rating.
                if (title.Length < 4 || PriceLine.IsMatch(title) || OldPrice.IsMatch(title) || RatingLine.IsMatch(title) || Noise.IsMatch(title)) continue;
                if (!decimal.TryParse(price.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount <= 0) continue;

                int j = i + 1;
                if (j < lines.Count && OldPrice.IsMatch(lines[j])) j++;
                if (j >= lines.Count) break;
                string store = lines[j];
                if (PriceLine.IsMatch(store) || RatingLine.IsMatch(store) || DeliveryLine.IsMatch(store) || Noise.IsMatch(store)) continue;
                j++;
                string delivery = "";
                double? rating = null;
                string reviews = "";
                // The next few lines may hold "& more", the delivery and the rating, in that order; stop at the next offer.
                for (int k = 0; k < 4 && j < lines.Count; k++, j++)
                {
                    var l = lines[j];
                    if (Noise.IsMatch(l)) continue;
                    if (delivery.Length == 0 && DeliveryLine.IsMatch(l) && !PriceLine.IsMatch(l)) { delivery = l; continue; }
                    var r = RatingLine.Match(l);
                    if (r.Success)
                    {
                        rating = double.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                        reviews = r.Groups[2].Value;
                        j++;
                    }
                    break;
                }
                offers.Add(new ShoppingOffer(title, amount, store, delivery, rating, reviews));
            }
        }
        catch (RegexMatchTimeoutException) { }
        return offers;
    }

    /// <summary>
    /// The offers for <paramref name="product"/> itself (not a case or a cable for it): titles that share most of its
    /// words, one per store, cheapest first, at most <paramref name="take"/>. With no product words, every offer counts.
    /// </summary>
    public static IReadOnlyList<ShoppingOffer> Best(IEnumerable<ShoppingOffer> offers, string? product, int take = 3)
    {
        var want = Words(product ?? "");
        bool Matches(ShoppingOffer o)
        {
            var have = Words(o.Title);
            // An accessory for the product (a case, a cable) isn't the product, unless an accessory is what was asked for.
            if (!want.Any(Accessories.Contains) && have.Any(Accessories.Contains)) return false;
            if (want.Count == 0) return true;
            int shared = want.Count(have.Contains);
            return shared >= Math.Max(1, (int)Math.Ceiling(want.Count * 0.6));
        }
        return offers.Where(Matches)
            .GroupBy(o => o.Store.ToLowerInvariant())
            .Select(g => g.MinBy(o => o.Price)!)
            .OrderBy(o => o.Price)
            .Take(take)
            .ToList();
    }

    static readonly HashSet<string> Accessories = new(StringComparer.Ordinal)
    {
        "case", "cover", "covers", "cable", "charger", "adapter", "strap", "protector", "skin", "tempered", "glass", "stand",
        "holder", "pouch", "sleeve", "tips", "refill", "replacement", "sticker", "decal",
    };

    static HashSet<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", RegexOptions.None, Limit)
            .Where(w => w.Length > 1 && w is not ("with" or "and" or "for" or "the" or "buy" or "new"))
            .ToHashSet();

    /// <summary>₹ in Indian grouping: 1,18,000 · 1,199 · 749.50.</summary>
    public static string Rupees(decimal amount)
    {
        var whole = decimal.Truncate(amount);
        var paise = amount - whole;
        var digits = ((long)whole).ToString(CultureInfo.InvariantCulture);
        string grouped;
        if (digits.Length <= 3) grouped = digits;
        else
        {
            var last3 = digits[^3..];
            var rest = digits[..^3];
            var parts = new List<string>();
            while (rest.Length > 2)
            {
                parts.Insert(0, rest[^2..]);
                rest = rest[..^2];
            }
            if (rest.Length > 0) parts.Insert(0, rest);
            grouped = string.Join(",", parts) + "," + last3;
        }
        return paise > 0 ? $"₹{grouped}.{(int)Math.Round(paise * 100):00}" : $"₹{grouped}";
    }
}
