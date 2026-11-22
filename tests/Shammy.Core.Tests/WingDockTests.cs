using Shammy.Core.Agents;

namespace Shammy.Core.Tests;

public class WingDockTests
{
    // A notch from 800 to 1100 across, its top at 5; the wing is 300 wide.
    const double IL = 800, IR = 1100, IT = 5, Snap = 56;

    [Theory]
    [InlineData(490, 5, WingSide.Left)]      // right edge 790: just left of the notch
    [InlineData(450, 40, WingSide.Left)]     // a little off, still within reach
    [InlineData(1110, 5, WingSide.Right)]    // left edge 1110: just right of the notch
    [InlineData(1150, -20, WingSide.Right)]
    [InlineData(300, 5, WingSide.Free)]      // level with it but far away
    [InlineData(490, 300, WingSide.Free)]    // beside it but far down the screen
    [InlineData(1300, 5, WingSide.Free)]
    public void A_drop_near_the_notch_snaps_to_that_side_and_anywhere_else_floats(double left, double top, WingSide expected) =>
        Assert.Equal(expected, WingDock.Decide(left, left + 300, top, IL, IR, IT, Snap));

    [Fact]
    public void A_wing_dropped_over_the_notch_snaps_to_the_side_its_middle_is_on()
    {
        Assert.Equal(WingSide.Left, WingDock.Decide(540, 840, 5, IL, IR, IT, Snap));
        Assert.Equal(WingSide.Right, WingDock.Decide(1060, 1360, 5, IL, IR, IT, Snap));
        // Dead centre (or a wing wider than the notch covering it): neither side, it floats.
        Assert.Equal(WingSide.Free, WingDock.Decide(800, 1100, 5, IL, IR, IT, Snap));
        Assert.Equal(WingSide.Free, WingDock.Decide(700, 1200, 5, IL, IR, IT, Snap));
    }

    [Fact]
    public void The_reach_is_exactly_the_snap_distance_at_its_edges()
    {
        Assert.Equal(WingSide.Left, WingDock.Decide(444, 744, 5 + Snap, IL, IR, IT, Snap));
        Assert.Equal(WingSide.Free, WingDock.Decide(443, 743, 5, IL, IR, IT, Snap));
        Assert.Equal(WingSide.Free, WingDock.Decide(490, 790, 5 + Snap + 1, IL, IR, IT, Snap));
    }

    [Fact]
    public void On_the_right_it_snaps_past_the_circle_beside_the_notch()
    {
        // A circle and dot reaching 60 past the notch: a drop just past them snaps; one further than the snap distance floats.
        Assert.Equal(WingSide.Right, WingDock.Decide(1170, 1470, 5, IL, IR, IT, Snap, reach: 60));
        Assert.Equal(WingSide.Free, WingDock.Decide(1240, 1540, 5, IL, IR, IT, Snap, reach: 60));
    }

    [Fact]
    public void A_floating_wing_is_kept_inside_the_work_area()
    {
        Assert.Equal((100, 200), WingDock.Clamp(100, 200, 300, 150, 0, 0, 1920, 1040));
        Assert.Equal((1620, 890), WingDock.Clamp(1800, 1000, 300, 150, 0, 0, 1920, 1040));   // off the bottom right
        Assert.Equal((0, 0), WingDock.Clamp(-50, -30, 300, 150, 0, 0, 1920, 1040));
        Assert.Equal((1920, 0), WingDock.Clamp(1900, 0, 300, 150, 1920, 0, 3840, 1040));      // a second screen
    }
}
