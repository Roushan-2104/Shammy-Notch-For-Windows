using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class ChipFitTests
{
    [Fact]
    public void All_chips_show_without_More_when_they_fit()
    {
        // 80 + 8 + 80 + 8 + 80 = 256: exactly the room there is.
        Assert.Equal((3, false), ChipFit.Fit(new double[] { 80, 80, 80 }, 256, 8, 60));
    }

    [Fact]
    public void Overflow_makes_room_for_More_at_the_end()
    {
        // Four chips need 344; with More (60) only two fit: 80 + 8 + 80 + 8 + 60 = 236 <= 250.
        Assert.Equal((2, true), ChipFit.Fit(new double[] { 80, 80, 80, 80 }, 250, 8, 60));
    }

    [Fact]
    public void Chips_show_in_order_so_a_small_late_chip_never_jumps_ahead()
    {
        Assert.Equal((1, true), ChipFit.Fit(new double[] { 80, 200, 20 }, 200, 8, 60));
    }

    [Fact]
    public void Without_room_for_More_it_shows_what_fits()
    {
        Assert.Equal((0, false), ChipFit.Fit(new double[] { 80 }, 40, 8, 60));
        Assert.Equal((0, false), ChipFit.Fit(Array.Empty<double>(), 400, 8, 60));
    }

}
