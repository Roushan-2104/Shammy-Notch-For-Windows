namespace Shammy.Core.Ambient;

/// <summary>What the sky is doing, coarse enough to pick an icon.</summary>
public enum WeatherKind
{
    Clear,
    PartlyCloudy,
    Cloudy,
    Fog,
    Drizzle,
    Rain,
    Snow,
    Thunderstorm,
    Unknown,
}

/// <summary>
/// WMO weather interpretation codes as used by Open-Meteo (0 clear ... 99 thunderstorm with hail).
/// See https://open-meteo.com/en/docs ("WMO Weather interpretation codes").
/// </summary>
public static class WeatherCodes
{
    // Segoe Fluent Icons has no rain or snow glyphs, so the closest single glyphs stand in where only
    // one character fits (command results, messages); the weather tab composes richer icons.
    public const string SunGlyph = "\uE706";
    public const string MoonGlyph = "\uE708";
    public const string CloudGlyph = "\uE753";
    public const string FogGlyph = "\uEDA8";
    public const string BoltGlyph = "\uE945";
    public const string SnowGlyph = "\uEA38";

    public static WeatherKind Kind(int code) => code switch
    {
        0 or 1 => WeatherKind.Clear,
        2 => WeatherKind.PartlyCloudy,
        3 => WeatherKind.Cloudy,
        45 or 48 => WeatherKind.Fog,
        51 or 53 or 55 or 56 or 57 => WeatherKind.Drizzle,
        61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => WeatherKind.Rain,
        71 or 73 or 75 or 77 or 85 or 86 => WeatherKind.Snow,
        95 or 96 or 99 => WeatherKind.Thunderstorm,
        _ => WeatherKind.Unknown,
    };

    public static string Describe(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 => "Fog",
        48 => "Freezing fog",
        51 => "Light drizzle",
        53 => "Drizzle",
        55 => "Heavy drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 => "Light showers",
        81 => "Showers",
        82 => "Violent showers",
        85 => "Snow showers",
        86 => "Heavy snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unknown",
    };

    /// <summary>The best single Fluent glyph for a code.</summary>
    public static string Glyph(int code, bool isDay = true) => Kind(code) switch
    {
        WeatherKind.Clear => isDay ? SunGlyph : MoonGlyph,
        WeatherKind.PartlyCloudy => isDay ? SunGlyph : CloudGlyph,
        WeatherKind.Fog => FogGlyph,
        WeatherKind.Snow => SnowGlyph,
        WeatherKind.Thunderstorm => BoltGlyph,
        _ => CloudGlyph,
    };

    /// <summary>0 = none, 1 = light, 2 = moderate, 3 = heavy; drives how many drops the tab draws.</summary>
    public static int Intensity(int code) => code switch
    {
        51 or 56 or 61 or 66 or 71 or 80 or 85 => 1,
        53 or 63 or 73 or 77 or 81 or 95 => 2,
        55 or 57 or 65 or 67 or 75 or 82 or 86 or 96 or 99 => 3,
        _ => 0,
    };
}
