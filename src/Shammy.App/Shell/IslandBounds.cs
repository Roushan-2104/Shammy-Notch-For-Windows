using System.Windows;

namespace Shammy.Shell;

/// <summary>
/// Where the island is on screen, for other windows that want to keep clear of it or line up with it (the wheel, a
/// pop-up). Device pixels, virtual-screen coordinates; updated when the island settles into a new size or moves to
/// another screen (not on every animation frame). Empty until the island has been placed.
/// </summary>
public static class IslandBounds
{
    /// <summary>The island's rectangle right now (the visible pill or slab, without its shadow).</summary>
    public static Rect Screen { get; private set; } = Rect.Empty;

    /// <summary>The handle of the monitor the island is on (IntPtr.Zero until placed).</summary>
    public static IntPtr Monitor { get; private set; }

    /// <summary>Raised on the UI thread after <see cref="Screen"/> changed.</summary>
    public static event Action? Changed;

    internal static void Set(Rect screen, IntPtr monitor)
    {
        if (screen == Screen && monitor == Monitor) return;
        Screen = screen;
        Monitor = monitor;
        Changed?.Invoke();
    }
}
