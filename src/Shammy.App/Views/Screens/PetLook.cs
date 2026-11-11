using System.Windows.Media;
using Shammy.Core.Notch;
using Shammy.Infrastructure;

namespace Shammy.Views.Screens;

/// <summary>
/// The pet's chosen look and name, for everything that draws the pet (the notch pet, the alarm and tour faces,
/// the Settings preview). Pushed from <see cref="AppSettings"/> by <see cref="SettingsApplier"/>; raises
/// <see cref="Changed"/> so a pet on screen restyles at once.
/// </summary>
internal static class PetLook
{
    public static PetEyeStyle Style { get; private set; } = PetEyeStyle.Round;
    public static PetEyeColor Color { get; private set; } = PetEyeColor.White;
    public static string Name { get; private set; } = PetLooks.DefaultName;

    /// <summary>A cat or the plain eyes (see <see cref="Views.CatParts"/>).</summary>
    public static PetKind Kind { get; private set; } = PetKind.Eyes;

    /// <summary>The cat purrs when the pointer rests on it.</summary>
    public static bool Purrs { get; private set; } = true;

    public static event Action? Changed;

    public static void Apply(AppSettings s)
    {
        Purrs = s.PetPurrs;
        Set(s.PetEyeStyle, s.PetEyeColor, s.PetName, s.PetKind);
    }

    public static void Set(PetEyeStyle style, PetEyeColor color, string name, PetKind? kind = null)
    {
        name = PetLooks.Name(name);
        var k = kind ?? Kind;
        if (style == Style && color == Color && name == Name && k == Kind) return;
        Style = style;
        Color = color;
        Name = name;
        Kind = k;
        Changed?.Invoke();
    }

    /// <summary>The flat eye colour (arcs, lids, mouths, the notch pet's eyes).</summary>
    public static SolidColorBrush Solid(PetEyeColor? color = null) => ScreenKit.B(PetLooks.Colors(color ?? Color).Hex);

    /// <summary>The eye's fill: a soft vertical gradient from the shade up top to the colour at 62 %.</summary>
    public static Brush Gradient(PetEyeColor? color = null)
    {
        var (hex, shade) = PetLooks.Colors(color ?? Color);
        var g = new LinearGradientBrush { StartPoint = new(0.5, 0), EndPoint = new(0.5, 1) };
        g.GradientStops.Add(new GradientStop(ScreenKit.C(shade), 0));
        g.GradientStops.Add(new GradientStop(ScreenKit.C(hex), 0.62));
        g.Freeze();
        return g;
    }
}
