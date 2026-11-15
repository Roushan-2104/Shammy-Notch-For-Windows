using Shammy.Core.Calendar;
using Xunit;

namespace Shammy.Core.Tests;

public class CalendarWingTests
{
    [Fact]
    public void Month_grid_is_six_monday_first_weeks()
    {
        var grid = CalendarAgenda.MonthGrid(2026, 11);
        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateTime(2026, 10, 26), grid[0]); // 1 Nov 2026 is a Sunday
        Assert.Equal(DayOfWeek.Monday, grid[0].DayOfWeek);
        Assert.Equal(new DateTime(2026, 11, 1), grid[6]);
        Assert.Equal(new DateTime(2026, 12, 6), grid[41]);
    }

    [Fact]
    public void Month_starting_on_monday_starts_on_the_first()
    {
        var grid = CalendarAgenda.MonthGrid(2027, 3); // 1 Mar 2027 is a Monday
        Assert.Equal(new DateTime(2027, 3, 1), grid[0]);
    }

    [Fact]
    public void Fetch_window_covers_this_week_this_month_and_next()
    {
        var (from, to) = CalendarAgenda.FetchWindow(new DateTime(2026, 11, 15, 15, 24, 0));
        Assert.Equal(new DateTime(2026, 11, 1), from);
        Assert.Equal(new DateTime(2027, 1, 1), to);
        // Early in a month, this week's Monday can be in the month before.
        (from, _) = CalendarAgenda.FetchWindow(new DateTime(2026, 12, 2));
        Assert.Equal(new DateTime(2026, 11, 30), from);
        // Late in a month, the card's two weeks still fit.
        (_, to) = CalendarAgenda.FetchWindow(new DateTime(2026, 12, 25));
        Assert.Equal(new DateTime(2027, 2, 1), to);
    }

    [Fact]
    public void Wing_keeps_the_island_scale_when_it_fits_and_shrinks_when_it_does_not()
    {
        // Your screen at Small: the room the neck and wing share is the screen less the island and one margin.
        Assert.Equal(0.88, CalendarWingFit.Scale(1536 - CalendarWingFit.Margin - 760 * 0.88, 0.88));
        // A notch smaller than the floor keeps its own scale rather than throwing.
        Assert.Equal(0.6, CalendarWingFit.Scale(100, 0.6));
        double tight = CalendarWingFit.Scale(300, 1.0);
        Assert.Equal((300 - CalendarWingFit.Margin) / (CalendarWingFit.Neck + CalendarWingFit.Width), tight, 6);
        Assert.Equal(CalendarWingFit.MinScale, CalendarWingFit.Scale(100, 1.0));
    }
}
