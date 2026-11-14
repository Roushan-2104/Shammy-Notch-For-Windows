using System.Globalization;
using System.Text.RegularExpressions;

namespace Shammy.Core.Conversions;

public enum UnitKind { Length, Mass, Temperature, Volume, Speed, Area, Data, Time }

/// <summary>A unit as a linear map to its kind's base unit (metre, kilogram, °C, litre, m/s, m², byte, second).</summary>
public sealed record Unit(string Symbol, UnitKind Kind, double Factor, double Offset = 0, string? Plural = null)
{
    public double ToBase(double value) => value * Factor + Offset;
    public double FromBase(double value) => (value - Offset) / Factor;

    /// <summary>"152.4 cm", "350°F", "3 cups".</summary>
    public string Format(double value)
    {
        var n = NumberText.Format(value);
        if (Symbol.StartsWith('°')) return n + Symbol;
        return $"{n} {(Plural is not null && n != "1" ? Plural : Symbol)}";
    }

    public override string ToString() => Symbol;
}

/// <summary>An amount in one unit; compound input ("5 ft 10 in") is summed into its first unit.</summary>
public sealed record Quantity(double Value, Unit Unit, string Display);

/// <summary>A finished unit conversion, ready to show.</summary>
public sealed record UnitConversion(Quantity From, Unit To, double Value, bool FeetAndInches = false)
{
    public string Title => FeetAndInches ? Units.FeetAndInches(From.Unit.ToBase(From.Value)) : To.Format(Value);

    /// <summary>The input, plus feet-and-inches when the answer is in feet (how heights are said).</summary>
    public string Detail => To == Units.Foot && !FeetAndInches ? $"{From.Display} · {Units.FeetAndInches(From.Unit.ToBase(From.Value))}" : From.Display;
}

/// <summary>
/// Unit conversion for typed or spoken phrases ("5 ft in cm", "convert 70 kg to pounds", "how many cups
/// in a gallon") and for circled measurements ("5 ft 10 in", "350°F"). Everything is invariant-culture.
/// </summary>
public static class Units
{
    static readonly Dictionary<string, Unit> Aliases = new(StringComparer.Ordinal);

    // Length (metre)
    public static readonly Unit Millimetre = Add(new("mm", UnitKind.Length, 0.001), "mm", "millimeter", "millimeters", "millimetre", "millimetres");
    public static readonly Unit Centimetre = Add(new("cm", UnitKind.Length, 0.01), "cm", "cms", "centimeter", "centimeters", "centimetre", "centimetres");
    public static readonly Unit Metre = Add(new("m", UnitKind.Length, 1), "m", "meter", "meters", "metre", "metres", "mtr", "mtrs");
    public static readonly Unit Kilometre = Add(new("km", UnitKind.Length, 1000), "km", "kms", "kilometer", "kilometers", "kilometre", "kilometres");
    public static readonly Unit Inch = Add(new("in", UnitKind.Length, 0.0254), "in", "inch", "inches", "\"", "''");
    public static readonly Unit Foot = Add(new("ft", UnitKind.Length, 0.3048), "ft", "foot", "feet", "'");
    public static readonly Unit Yard = Add(new("yd", UnitKind.Length, 0.9144), "yd", "yds", "yard", "yards");
    public static readonly Unit Mile = Add(new("mi", UnitKind.Length, 1609.344), "mi", "mile", "miles");
    public static readonly Unit NauticalMile = Add(new("nmi", UnitKind.Length, 1852), "nmi", "nautical mile", "nautical miles");

    // Mass (kilogram)
    public static readonly Unit Milligram = Add(new("mg", UnitKind.Mass, 1e-6), "mg", "milligram", "milligrams", "milligramme", "milligrammes");
    public static readonly Unit Gram = Add(new("g", UnitKind.Mass, 0.001), "g", "gm", "gms", "gram", "grams", "gramme", "grammes");
    public static readonly Unit Kilogram = Add(new("kg", UnitKind.Mass, 1), "kg", "kgs", "kilo", "kilos", "kilogram", "kilograms", "kilogramme", "kilogrammes");
    public static readonly Unit Tonne = Add(new("t", UnitKind.Mass, 1000), "t", "tonne", "tonnes", "ton", "tons", "metric ton", "metric tons");
    public static readonly Unit Ounce = Add(new("oz", UnitKind.Mass, 0.028349523125), "oz", "ounce", "ounces");
    public static readonly Unit Pound = Add(new("lb", UnitKind.Mass, 0.45359237), "lb", "lbs", "pound", "pounds");
    public static readonly Unit Stone = Add(new("st", UnitKind.Mass, 6.35029318), "st", "stone", "stones");

    // Temperature (°C)
    public static readonly Unit Celsius = Add(new("°C", UnitKind.Temperature, 1), "c", "°c", "celsius", "centigrade", "degree c", "degrees c", "deg c",
        "degree celsius", "degrees celsius", "degree centigrade", "degrees centigrade");
    public static readonly Unit Fahrenheit = Add(new("°F", UnitKind.Temperature, 5.0 / 9, -160.0 / 9), "f", "°f", "fahrenheit", "degree f", "degrees f", "deg f",
        "degree fahrenheit", "degrees fahrenheit");
    public static readonly Unit Kelvin = Add(new("K", UnitKind.Temperature, 1, -273.15), "k", "°k", "kelvin", "kelvins");

    // Volume (litre); cups, pints, quarts and gallons are US customary
    public static readonly Unit Millilitre = Add(new("mL", UnitKind.Volume, 0.001), "ml", "mls", "cc", "milliliter", "milliliters", "millilitre", "millilitres");
    public static readonly Unit Litre = Add(new("L", UnitKind.Volume, 1), "l", "lt", "ltr", "ltrs", "liter", "liters", "litre", "litres");
    public static readonly Unit Teaspoon = Add(new("tsp", UnitKind.Volume, 0.00492892159375), "tsp", "tsps", "teaspoon", "teaspoons");
    public static readonly Unit Tablespoon = Add(new("tbsp", UnitKind.Volume, 0.01478676478125), "tbsp", "tbsps", "tbs", "tablespoon", "tablespoons");
    public static readonly Unit FluidOunce = Add(new("fl oz", UnitKind.Volume, 0.0295735295625), "fl oz", "floz", "fluid ounce", "fluid ounces", "fl ounce", "fl ounces");
    public static readonly Unit Cup = Add(new("cup", UnitKind.Volume, 0.2365882365, Plural: "cups"), "cup", "cups");
    public static readonly Unit Pint = Add(new("pt", UnitKind.Volume, 0.473176473), "pt", "pint", "pints");
    public static readonly Unit Quart = Add(new("qt", UnitKind.Volume, 0.946352946), "qt", "quart", "quarts");
    public static readonly Unit Gallon = Add(new("gal", UnitKind.Volume, 3.785411784), "gal", "gals", "gallon", "gallons");

    // Speed (m/s)
    public static readonly Unit MetresPerSecond = Add(new("m/s", UnitKind.Speed, 1), "m/s", "mps", "meter per second", "meters per second", "metre per second", "metres per second");
    public static readonly Unit KilometresPerHour = Add(new("km/h", UnitKind.Speed, 1 / 3.6), "km/h", "kmh", "kph", "kmph", "km/hr", "km per hour", "kms per hour",
        "kilometer per hour", "kilometers per hour", "kilometre per hour", "kilometres per hour");
    public static readonly Unit MilesPerHour = Add(new("mph", UnitKind.Speed, 0.44704), "mph", "mi/h", "mile per hour", "miles per hour");
    public static readonly Unit Knot = Add(new("knot", UnitKind.Speed, 1852 / 3600.0, Plural: "knots"), "knot", "knots", "kn", "kt", "kts");
    public static readonly Unit FeetPerSecond = Add(new("ft/s", UnitKind.Speed, 0.3048), "ft/s", "fps", "foot per second", "feet per second");

    // Area (m²)
    public static readonly Unit SquareCentimetre = Add(new("cm²", UnitKind.Area, 1e-4), "cm²", "cm2", "sq cm", "square centimeter", "square centimeters", "square centimetre", "square centimetres");
    public static readonly Unit SquareMetre = Add(new("m²", UnitKind.Area, 1), "m²", "m2", "sq m", "sqm", "square meter", "square meters", "square metre", "square metres");
    public static readonly Unit SquareKilometre = Add(new("km²", UnitKind.Area, 1e6), "km²", "km2", "sq km", "square kilometer", "square kilometers", "square kilometre", "square kilometres");
    public static readonly Unit SquareInch = Add(new("in²", UnitKind.Area, 0.00064516), "in²", "sq in", "square inch", "square inches");
    public static readonly Unit SquareFoot = Add(new("ft²", UnitKind.Area, 0.09290304), "ft²", "ft2", "sq ft", "sqft", "square foot", "square feet");
    public static readonly Unit SquareYard = Add(new("yd²", UnitKind.Area, 0.83612736), "yd²", "yd2", "sq yd", "square yard", "square yards");
    public static readonly Unit SquareMile = Add(new("mi²", UnitKind.Area, 2589988.110336), "mi²", "mi2", "sq mi", "square mile", "square miles");
    public static readonly Unit Acre = Add(new("acre", UnitKind.Area, 4046.8564224, Plural: "acres"), "acre", "acres", "ac");
    public static readonly Unit Hectare = Add(new("ha", UnitKind.Area, 10000), "ha", "hectare", "hectares");

    // Data (byte); KB/MB/GB are decimal like Google's answers, KiB/MiB/GiB binary
    public static readonly Unit Bit = Add(new("bit", UnitKind.Data, 0.125, Plural: "bits"), "bit", "bits");
    public static readonly Unit Byte = Add(new("B", UnitKind.Data, 1), "byte", "bytes");
    public static readonly Unit Kilobyte = Add(new("KB", UnitKind.Data, 1e3), "kb", "kilobyte", "kilobytes");
    public static readonly Unit Megabyte = Add(new("MB", UnitKind.Data, 1e6), "mb", "megabyte", "megabytes");
    public static readonly Unit Gigabyte = Add(new("GB", UnitKind.Data, 1e9), "gb", "gig", "gigs", "gigabyte", "gigabytes");
    public static readonly Unit Terabyte = Add(new("TB", UnitKind.Data, 1e12), "tb", "terabyte", "terabytes");
    public static readonly Unit Petabyte = Add(new("PB", UnitKind.Data, 1e15), "pb", "petabyte", "petabytes");
    public static readonly Unit Kibibyte = Add(new("KiB", UnitKind.Data, 1024), "kib", "kibibyte", "kibibytes");
    public static readonly Unit Mebibyte = Add(new("MiB", UnitKind.Data, 1024.0 * 1024), "mib", "mebibyte", "mebibytes");
    public static readonly Unit Gibibyte = Add(new("GiB", UnitKind.Data, 1024.0 * 1024 * 1024), "gib", "gibibyte", "gibibytes");
    public static readonly Unit Tebibyte = Add(new("TiB", UnitKind.Data, 1024.0 * 1024 * 1024 * 1024), "tib", "tebibyte", "tebibytes");

    // Time (second); a month and a year are Gregorian averages
    public static readonly Unit Millisecond = Add(new("ms", UnitKind.Time, 0.001), "ms", "msec", "millisecond", "milliseconds");
    public static readonly Unit Second = Add(new("s", UnitKind.Time, 1), "s", "sec", "secs", "second", "seconds");
    public static readonly Unit Minute = Add(new("min", UnitKind.Time, 60), "min", "mins", "minute", "minutes");
    public static readonly Unit Hour = Add(new("h", UnitKind.Time, 3600), "h", "hr", "hrs", "hour", "hours");
    public static readonly Unit Day = Add(new("day", UnitKind.Time, 86400, Plural: "days"), "d", "day", "days");
    public static readonly Unit Week = Add(new("week", UnitKind.Time, 604800, Plural: "weeks"), "wk", "wks", "week", "weeks");
    public static readonly Unit Month = Add(new("month", UnitKind.Time, 2629746, Plural: "months"), "month", "months");
    public static readonly Unit Year = Add(new("year", UnitKind.Time, 31556952, Plural: "years"), "yr", "yrs", "year", "years");

    static readonly HashSet<string> FeetInchesTargets = new(StringComparer.Ordinal)
        { "ft in", "ft and in", "feet and inches", "feet inches", "foot and inches", "feet and inch" };

    // Single letters that read as something else on a web page: "100K followers", "1M views", "2T market cap".
    static readonly HashSet<string> AmbiguousOnScreen = new(StringComparer.Ordinal) { "k", "t", "s", "h", "d" };
    static readonly Regex MillionSuffix = new(@"\d\s?M\b");

    static readonly Regex Compound = new(
        @"^(?:(?<num>-?(?:\d+ \d+/\d+|\d+/\d+|\d[\d,]*(?:\.\d+)?|\.\d+)) ?(?<unit>[^\d\s][^\d]*?) ?)+$");
    static readonly Regex HowMany = new(@"^how many (?<rest>.+)$");

    static Unit Add(Unit unit, params string[] aliases)
    {
        foreach (var a in aliases) Aliases[a] = unit;
        return unit;
    }

    /// <summary>"centimeters", "sq. ft.", "°F", "km per hour" → the unit.</summary>
    public static bool TryFind(string text, out Unit unit)
    {
        var key = Phrase.Normalize(text).Replace(".", "").Trim();
        return Aliases.TryGetValue(key, out unit!);
    }

    /// <summary>"2.5 kg", "350°f", "5 ft 10 in", "5'10\"", "1 1/2 cups", "five feet", "an inch".</summary>
    public static bool TryParseQuantity(string text, out Quantity quantity) => TryParseQuantity(text, out quantity, out _);

    static bool TryParseQuantity(string text, out Quantity quantity, out List<string> rawUnits)
    {
        quantity = null!;
        rawUnits = new List<string>();
        var s = Phrase.Normalize(text);
        if (s.Length == 0) return false;

        var parts = new List<(double Value, Unit Unit)>();
        var m = Compound.Match(s);
        if (m.Success)
        {
            var nums = m.Groups["num"].Captures;
            var units = m.Groups["unit"].Captures;
            for (int i = 0; i < nums.Count; i++)
            {
                var raw = units[i].Value.Trim();
                if (!NumberText.TryParse(nums[i].Value, out var v) || !TryFind(raw, out var u)) return false;
                parts.Add((v, u));
                rawUnits.Add(raw);
            }
        }
        else
        {
            // Number words: "five feet", "an inch", "twenty five kg", "half a cup".
            var words = s.Split(' ');
            for (int k = Math.Min(2, words.Length - 1); k >= 1 && parts.Count == 0; k--)
            {
                var num = string.Join(' ', words, 0, k);
                var raw = string.Join(' ', words, k, words.Length - k);
                if (NumberText.TryParse(num, out var v) && char.IsLetter(num[0]) && TryFind(raw, out var u))
                {
                    parts.Add((v, u));
                    rawUnits.Add(raw);
                }
            }
        }
        if (parts.Count == 0) return false;

        var first = parts[0].Unit;
        if (parts.Count > 1)
        {
            // Compound amounts only go big to small in one kind: "5 ft 10 in", "1 lb 4 oz", "1 h 30 min".
            if (first.Kind == UnitKind.Temperature) return false;
            for (int i = 1; i < parts.Count; i++)
                if (parts[i].Unit.Kind != first.Kind || parts[i].Unit.Factor >= parts[i - 1].Unit.Factor) return false;
        }
        double total = parts.Sum(p => p.Unit.ToBase(p.Value));
        double value = parts.Count == 1 ? parts[0].Value : first.FromBase(total);
        var display = string.Join(' ', parts.Select(p => p.Unit.Format(p.Value)));
        quantity = new Quantity(value, first, display);
        return true;
    }

    /// <summary>
    /// A typed or spoken conversion: "5 ft in cm", "convert 70 kg to pounds", "100 f to c", "feet to meters",
    /// "how many cups in a gallon", "178 cm in feet and inches". False for anything else, so searches pass.
    /// </summary>
    public static bool TryParseQuery(string text, out UnitConversion conversion)
    {
        conversion = null!;
        var s = Phrase.StripLead(Phrase.Normalize(text));
        if (s.Length == 0 || s.Length > 60) return false;

        var how = HowMany.Match(s);
        if (how.Success)
        {
            // "how many cm in an inch": the target comes first.
            foreach (var (target, source) in Phrase.Splits(how.Groups["rest"].Value.Replace(" are in ", " in ").Replace(" is in ", " in "), "in"))
                if (TryBuild(source, target, requireAmount: true, out conversion)) return true;
            return false;
        }

        foreach (var (left, right) in Phrase.Splits(s, Phrase.ToWords))
            if (TryBuild(left, right, requireAmount: false, out conversion)) return true;
        return false;
    }

    static bool TryBuild(string left, string right, bool requireAmount, out UnitConversion conversion)
    {
        conversion = null!;
        bool feetInches = FeetInchesTargets.Contains(right.Replace(".", ""));
        Unit to;
        if (feetInches) to = Foot;
        else if (!TryFind(right, out to)) return false;

        if (!TryParseQuantity(left, out var source))
        {
            // "feet to meters" means one foot; single letters ("c to f", "m to s") are too often something else.
            var key = left.Replace(".", "");
            if (requireAmount || key.Length < 2 || !TryFind(key, out var unit)) return false;
            source = new Quantity(1, unit, unit.Format(1));
        }

        var fromUnit = source.Unit;
        if (!Reconcile(ref fromUnit, ref to)) return false;
        if (fromUnit == to && !feetInches) return false; // "month to month", "day to day" are searches
        if (feetInches && fromUnit.Kind != UnitKind.Length) return false;
        source = source with { Unit = fromUnit };
        conversion = new UnitConversion(source, to, Convert(source.Value, fromUnit, to), feetInches);
        return true;
    }

    /// <summary>"oz" is weight unless the other side is a volume ("12 oz in ml"), then it's fluid ounces.</summary>
    static bool Reconcile(ref Unit from, ref Unit to)
    {
        if (from.Kind == to.Kind) return true;
        if (from == Ounce && to.Kind == UnitKind.Volume) from = FluidOunce;
        else if (to == Ounce && from.Kind == UnitKind.Volume) to = FluidOunce;
        return from.Kind == to.Kind;
    }

    public static double Convert(double value, Unit from, Unit to)
    {
        if (from.Kind != to.Kind) throw new ArgumentException($"Can't convert {from} to {to}");
        return to.FromBase(from.ToBase(value));
    }

    /// <summary>
    /// A circled measurement ("5 ft 10 in", "350°F", "2.5 kg", "60 mph") converted to metric, or to imperial
    /// if it's already metric. False when the text is anything more than a measurement.
    /// </summary>
    public static bool TryReadMeasurement(string text, out UnitConversion conversion)
    {
        conversion = null!;
        var s = Phrase.CleanSelection(text);
        var digits = s.TrimStart('-');
        if (digits.Length == 0 || !char.IsDigit(digits[0])) return false;
        if (!TryParseQuantity(s, out var q, out var raw)) return false;
        if (raw.Any(AmbiguousOnScreen.Contains)) return false;
        // A lone "in" is the word, not inches ("Top 10 in India", "1 in 3 people"); 5" and "5 ft 10 in" still read.
        if (raw is ["in"]) return false;
        // "1M views" is a million, "2 m" a length: only the capital is ambiguous.
        if (raw.Contains("m") && MillionSuffix.IsMatch(text)) return false;
        var to = Counterpart(q.Unit, q.Value);
        if (to is null) return false;
        conversion = new UnitConversion(q, to, Convert(q.Value, q.Unit, to));
        return true;
    }

    /// <summary>The unit people on the other side of the metric/imperial divide would use.</summary>
    public static Unit? Counterpart(Unit unit, double value)
    {
        if (unit == Millimetre || unit == Centimetre) return Inch;
        if (unit == Metre) return Foot;
        if (unit == Kilometre) return Mile;
        if (unit == Inch) return Centimetre;
        if (unit == Foot) return Math.Abs(value) < 10 ? Centimetre : Metre;
        if (unit == Yard) return Metre;
        if (unit == Mile || unit == NauticalMile) return Kilometre;

        if (unit == Milligram || unit == Gram) return Ounce;
        if (unit == Kilogram || unit == Tonne) return Pound;
        if (unit == Ounce) return Gram;
        if (unit == Pound || unit == Stone) return Kilogram;

        if (unit == Celsius) return Fahrenheit;
        if (unit == Fahrenheit || unit == Kelvin) return Celsius;

        if (unit == Millilitre) return FluidOunce;
        if (unit == Litre) return Math.Abs(value) < 2 ? FluidOunce : Gallon;
        if (unit == Teaspoon || unit == Tablespoon || unit == FluidOunce || unit == Cup || unit == Pint) return Millilitre;
        if (unit == Quart || unit == Gallon) return Litre;

        if (unit == KilometresPerHour || unit == MetresPerSecond) return MilesPerHour;
        if (unit == MilesPerHour || unit == Knot) return KilometresPerHour;
        if (unit == FeetPerSecond) return MetresPerSecond;

        if (unit == SquareCentimetre) return SquareInch;
        if (unit == SquareMetre) return SquareFoot;
        if (unit == SquareKilometre) return SquareMile;
        if (unit == Hectare) return Acre;
        if (unit == SquareInch) return SquareCentimetre;
        if (unit == SquareFoot || unit == SquareYard) return SquareMetre;
        if (unit == SquareMile) return SquareKilometre;
        if (unit == Acre) return Hectare;

        return null; // data and time read the same everywhere
    }

    /// <summary>Metres as "5 ft 10 in" (inches to one decimal when needed).</summary>
    public static string FeetAndInches(double metres)
    {
        var totalInches = Math.Round(Math.Abs(metres) / 0.0254, 1, MidpointRounding.AwayFromZero);
        var feet = Math.Floor(totalInches / 12);
        var inches = Math.Round(totalInches - feet * 12, 1);
        if (inches >= 12) { feet++; inches -= 12; }
        var sign = metres < 0 ? "-" : "";
        var inText = inches.ToString("0.#", CultureInfo.InvariantCulture);
        return feet == 0 ? $"{sign}{inText} in" : $"{sign}{feet.ToString("#,0", CultureInfo.InvariantCulture)} ft {inText} in";
    }
}
