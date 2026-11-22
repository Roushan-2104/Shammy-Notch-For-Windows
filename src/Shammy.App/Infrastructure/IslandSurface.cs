using System.Windows.Media;

namespace Shammy.Infrastructure;

/// <summary>
/// The island's surface, shared so the pieces beside it (the steps wing, the calendar, the dot between them) are made of
/// the same tinted glass. The brushes are live (never frozen): when the island's colour changes they repaint
/// everything that uses them. UI thread only.
/// </summary>
internal static class IslandSurface
{
    /// <summary>The island's colour: its glass's colour with the screen's tint over it.</summary>
    public static readonly SolidColorBrush Fill = new(Color.FromRgb(0x08, 0x08, 0x0A));

    /// <summary>The thin line round the island and its pieces: a light line of the screen's colour, or a faint hairline.</summary>
    public static readonly SolidColorBrush Rim = new(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
}
