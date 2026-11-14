namespace Shammy.Core.Notch;

/// <summary>The pet's eye shape, chosen in Settings (Notch &amp; pet). It shapes the eyes in every mood.</summary>
public enum PetEyeStyle { Round, Oval, Pixel }

/// <summary>The pet's eye colour, chosen in Settings. White is the default.</summary>
public enum PetEyeColor { White, Peach, Mint, Sky, Lilac }

/// <summary>
/// The pet's look and name as the design spells them: eye sizes per style at the design's 1× face (the notch
/// draws its own smaller eyes and only borrows the proportions), the two tones of each eye colour (the eye is a
/// soft vertical gradient from the shade to the colour) and the name rules.
/// </summary>
public static class PetLooks
{
    public const string DefaultName = "Shammy";
    public const int MaxNameLength = 12;

    /// <summary>The names the tour and Settings offer as one-click picks.</summary>
    public static readonly IReadOnlyList<string> NamePresets = new[] { "Shammy", "Momo", "Pixel", "Golu" };

    /// <summary>Trimmed, inner runs of spaces collapsed, at most 12 characters; empty means the default.</summary>
    public static string Name(string? name)
    {
        var parts = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var clean = string.Join(' ', parts);
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].TrimEnd();
        return clean.Length == 0 ? DefaultName : clean;
    }

    /// <summary>The colour and its darker shade ("#F5F5F7", "#A3A6B2").</summary>
    public static (string Hex, string Shade) Colors(PetEyeColor color) => color switch
    {
        PetEyeColor.Peach => ("#FFB585", "#D97B45"),
        PetEyeColor.Mint => ("#7DE3C3", "#3FA987"),
        PetEyeColor.Sky => ("#9CC9FF", "#5B8FD8"),
        PetEyeColor.Lilac => ("#C9B5FF", "#8B72DE"),
        _ => ("#F5F5F7", "#A3A6B2"),
    };

    /// <summary>One eye at the design's 1× face: width, height and corner radius.</summary>
    public static (double Width, double Height, double Radius) Eye(PetEyeStyle style) => style switch
    {
        PetEyeStyle.Oval => (9, 15, 4.5),
        PetEyeStyle.Pixel => (10, 10, 2),
        _ => (11, 13, 6),
    };

    /// <summary>
    /// The notch pet's own eye (a <paramref name="width"/>×<paramref name="height"/> pill) in another style:
    /// oval is narrower and taller, pixel squarer with hard corners. Returns the new size and corner radius.
    /// </summary>
    public static (double Width, double Height, double Radius) Restyle(PetEyeStyle style, double width, double height) => style switch
    {
        PetEyeStyle.Oval => (Math.Round(width * 0.82, 1), Math.Round(height * 1.12, 1), Math.Round(width * 0.41, 1)),
        PetEyeStyle.Pixel => (width, Math.Min(height, Math.Round(width * 1.1, 1)), 1.5),
        _ => (width, height, width / 2),
    };
}
