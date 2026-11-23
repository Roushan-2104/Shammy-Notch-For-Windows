using System.Windows.Media;

namespace Shammy.Infrastructure;

/// <summary>
/// The island's surface, shared so the pieces beside it (the steps wing, the calendar, the dot between them) are made of
/// the same glass. The fill is live (never frozen): when the island's colour changes (a new wallpaper) it repaints
/// everything that uses it. UI thread only.
/// </summary>
internal static class IslandSurface
{
    /// <summary>The island's colour: its glass's colour.</summary>
    public static readonly SolidColorBrush Fill = new(Color.FromRgb(0x08, 0x08, 0x0A));

    /// <summary>The faint hairline round the island's pieces.</summary>
    public static readonly SolidColorBrush Rim = new(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
}
