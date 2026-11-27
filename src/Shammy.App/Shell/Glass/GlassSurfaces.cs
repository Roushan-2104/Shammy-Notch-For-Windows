using System.Windows;
using System.Windows.Media;

namespace Shammy.Shell.Glass;

/// <summary>One piece of the island family that frosted glass sits behind: where it is on screen (device pixels), its
/// top and bottom corner radii (pixels), how visible it is (0-1) and how dark its tint is (0 light, 1 dark for text).</summary>
internal readonly record struct GlassShape(Rect Screen, double TopRadius, double BottomRadius, double Opacity, double Depth = 0);

/// <summary>
/// The island, its wings and their dots tell frosted glass where they are, and which windows they live in (the frosted
/// window goes just under the lowest). UI thread only.
/// </summary>
internal static class GlassSurfaces
{
    static readonly Dictionary<string, GlassShape> Shapes = new();
    static readonly HashSet<IntPtr> Windows = new();

    /// <summary>Invisible but there: what the island family is filled with under frosted glass, so it still takes clicks.</summary>
    public static readonly SolidColorBrush HitOnly = MakeHitOnly();

    static SolidColorBrush MakeHitOnly()
    {
        var b = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        b.Freeze();
        return b;
    }

    /// <summary>Frosted glass is showing now (the island family's own fills step aside).</summary>
    public static bool Running { get; private set; }

    /// <summary>Raised when <see cref="Running"/> flips.</summary>
    public static event Action? RunningChanged;

    /// <summary>Raised when any piece moves, resizes, fades or leaves.</summary>
    public static event Action? ShapesChanged;

    public static void Set(string key, GlassShape shape)
    {
        if (Shapes.TryGetValue(key, out var old) && Near(old, shape)) return;
        Shapes[key] = shape;
        ShapesChanged?.Invoke();
    }

    public static void Remove(string key)
    {
        if (Shapes.Remove(key)) ShapesChanged?.Invoke();
    }

    public static IReadOnlyList<GlassShape> Current() =>
        Shapes.Values.Where(s => s.Opacity > 0.01 && s.Screen.Width >= 1 && s.Screen.Height >= 1).ToList();

    static bool Near(GlassShape a, GlassShape b) =>
        Math.Abs(a.Screen.X - b.Screen.X) < 0.25 && Math.Abs(a.Screen.Y - b.Screen.Y) < 0.25 && Math.Abs(a.Screen.Width - b.Screen.Width) < 0.25 &&
        Math.Abs(a.Screen.Height - b.Screen.Height) < 0.25 && Math.Abs(a.TopRadius - b.TopRadius) < 0.25 && Math.Abs(a.BottomRadius - b.BottomRadius) < 0.25 &&
        Math.Abs(a.Opacity - b.Opacity) < 0.01 && Math.Abs(a.Depth - b.Depth) < 0.01;

    internal static void SetRunning(bool running)
    {
        if (running == Running) return;
        Running = running;
        RunningChanged?.Invoke();
    }

    /// <summary>A window of the island family (safe to call again).</summary>
    public static void Join(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) Windows.Add(hwnd);
    }

    /// <summary>The window is gone.</summary>
    public static void Forget(IntPtr hwnd) => Windows.Remove(hwnd);

    /// <summary>The island family's windows.</summary>
    public static IReadOnlyList<IntPtr> Family => Windows.ToList();

    internal static string Describe() =>
        $"running={Running} shapes={Shapes.Count} [{string.Join(", ", Shapes.Select(s => $"{s.Key}:{s.Value.Screen.X:0},{s.Value.Screen.Y:0} {s.Value.Screen.Width:0}x{s.Value.Screen.Height:0} r{s.Value.TopRadius:0}/{s.Value.BottomRadius:0} a{s.Value.Opacity:0.##}"))}] windows={Windows.Count}";
}
