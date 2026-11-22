namespace Shammy.Core.Agents;

/// <summary>Where the steps wing sits: snapped beside the notch on its left or right, or left floating where it was dropped.</summary>
/// <remarks>Saved as a number: the values must never change.</remarks>
public enum WingSide { Left = 0, Right = 1, Free = 2 }

/// <summary>Where a dragged steps wing lands when it's let go.</summary>
public static class WingDock
{
    /// <summary>How close to the notch's side (DIPs) a drop has to be to snap onto it.</summary>
    public const double Snap = 56;

    /// <summary>
    /// The side for a wing dropped with its slab spanning <paramref name="left"/>..<paramref name="right"/> and its top at
    /// <paramref name="top"/>, beside a notch spanning <paramref name="islandLeft"/>..<paramref name="islandRight"/> with
    /// its top at <paramref name="islandTop"/>, whose action circle and dot reach <paramref name="reach"/> further right
    /// (all in screen pixels; <paramref name="snap"/> already scaled). It snaps when its top is near the notch's top and
    /// its near edge near the notch's side (past the circle on the right); anywhere else, or dead centre, it floats.
    /// </summary>
    public static WingSide Decide(double left, double right, double top, double islandLeft, double islandRight, double islandTop, double snap, double reach = 0)
    {
        if (Math.Abs(top - islandTop) > snap) return WingSide.Free;
        double middle = (left + right) / 2, islandMiddle = (islandLeft + islandRight) / 2;
        if (middle < islandMiddle && Math.Abs(right - islandLeft) <= snap) return WingSide.Left;
        if (middle > islandMiddle && Math.Abs(left - (islandRight + reach)) <= snap) return WingSide.Right;
        return WingSide.Free;
    }

    /// <summary>
    /// A floating slab's top-left moved just enough that the whole slab (<paramref name="width"/>×<paramref name="height"/>)
    /// is inside the screen's work area (all in screen pixels); a slab bigger than the area keeps its top-left corner in.
    /// </summary>
    public static (int X, int Y) Clamp(int x, int y, int width, int height, int areaLeft, int areaTop, int areaRight, int areaBottom) =>
        (Math.Max(areaLeft, Math.Min(x, areaRight - width)), Math.Max(areaTop, Math.Min(y, areaBottom - height)));
}
