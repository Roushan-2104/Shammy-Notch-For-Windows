using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class SmoothCornerTests
{
    [Fact]
    public void With_room_the_curve_reaches_further_than_the_radius()
    {
        var (extent, handle) = SmoothCorner.For(12, 25);
        Assert.Equal(15.36, extent, 2);
        Assert.Equal(15.36 * SmoothCorner.SmoothHandle, handle, 3);
    }

    [Theory]
    [InlineData(12, 25)]
    [InlineData(30, 200)]
    [InlineData(22, 55)]
    public void Its_middle_passes_where_the_circle_would(double radius, double room)
    {
        var (extent, handle) = SmoothCorner.For(radius, room);
        double circle = radius * (1 - 1 / Math.Sqrt(2));
        Assert.Equal(circle, SmoothCorner.Middle(extent, handle), 1);
    }

    [Fact]
    public void With_no_room_to_spare_it_is_the_plain_quarter_circle()
    {
        var (extent, handle) = SmoothCorner.For(16, 16);
        Assert.Equal(16, extent);
        Assert.Equal(16 * SmoothCorner.CircleHandle, handle, 3);
    }

    [Fact]
    public void A_little_room_gives_a_corner_in_between()
    {
        var (extent, handle) = SmoothCorner.For(10, 11.4);
        Assert.Equal(11.4, extent, 2);
        Assert.InRange(handle / extent, SmoothCorner.SmoothHandle, SmoothCorner.CircleHandle);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-3, 20)]
    public void No_radius_means_a_square_corner(double radius, double room) => Assert.Equal((0, 0), SmoothCorner.For(radius, room));

    [Fact]
    public void The_curve_never_reaches_past_the_room_it_has()
    {
        for (double r = 1; r <= 40; r += 1.5)
            for (double room = r; room <= 80; room += 3.7)
            {
                var (extent, handle) = SmoothCorner.For(r, room);
                Assert.InRange(extent, r, Math.Max(r, room));
                Assert.InRange(handle, 0, extent);
            }
    }
}

public class PopupStackTests
{
    [Fact]
    public void A_message_that_arrives_alone_has_nothing_under_it()
    {
        var stack = new PopupStack<string>();
        Assert.Equal(0, stack.Arrived(null));
        Assert.Null(stack.Dismissed());
    }

    [Fact]
    public void Each_new_message_covers_the_one_on_screen()
    {
        var stack = new PopupStack<string>();
        stack.Arrived(null);
        Assert.Equal(1, stack.Arrived("first"));
        Assert.Equal(2, stack.Arrived("second"));
        Assert.Equal(2, stack.Count);
    }

    [Fact]
    public void Sending_the_top_one_away_brings_back_the_last_one_covered()
    {
        var stack = new PopupStack<string>();
        stack.Arrived(null);
        stack.Arrived("first");
        stack.Arrived("second");
        Assert.Equal("second", stack.Dismissed());
        Assert.Equal(1, stack.Count);
        Assert.Equal("first", stack.Dismissed());
        Assert.Null(stack.Dismissed());
    }

    [Fact]
    public void A_message_arriving_on_an_empty_notch_forgets_the_old_pile()
    {
        var stack = new PopupStack<string>();
        stack.Arrived("a");
        stack.Arrived("b");
        // The top one timed out, so nothing is on screen when the next arrives.
        Assert.Equal(0, stack.Arrived(null));
        Assert.Null(stack.Dismissed());
    }

    [Fact]
    public void Only_the_newest_nine_wait_underneath()
    {
        var stack = new PopupStack<string>();
        for (int i = 0; i < 15; i++) stack.Arrived("m" + i);
        Assert.Equal(PopupStack<string>.Limit, stack.Count);
        Assert.Equal("m14", stack.Dismissed());
        for (int i = 0; i < 7; i++) stack.Dismissed();
        Assert.Equal("m6", stack.Dismissed());
        Assert.Null(stack.Dismissed());
    }

    [Fact]
    public void Muting_an_app_takes_its_messages_out_of_the_pile()
    {
        var stack = new PopupStack<string>();
        foreach (var m in new[] { "chat:1", "mail:1", "chat:2" }) stack.Arrived(m);
        Assert.Equal(2, stack.RemoveAll(m => m.StartsWith("chat")));
        Assert.Equal("mail:1", stack.Dismissed());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(7, 2)]
    [InlineData(-1, 0)]
    public void One_or_two_cards_peek_out_from_under(int waiting, int peeking) => Assert.Equal(peeking, PopupStack<string>.Peeking(waiting));
}

public class SwipeAwayTests
{
    [Theory]
    [InlineData(90, 0, true)]
    [InlineData(-140, 0, true)]
    [InlineData(89, 0, false)]
    [InlineData(20, 0, false)]
    // A quick flick leaves from closer in, but only in the direction it was dragged.
    [InlineData(40, 1200, true)]
    [InlineData(-40, -1200, true)]
    [InlineData(40, -1200, false)]
    [InlineData(20, 3000, false)]
    [InlineData(40, 500, false)]
    public void A_long_drag_or_a_quick_flick_sends_it_away(double dx, double speed, bool leaves) =>
        Assert.Equal(leaves, SwipeAway.Leaves(dx, speed));

    [Fact]
    public void The_card_fades_as_it_goes_but_stays_visible()
    {
        Assert.Equal(1, SwipeAway.Opacity(0, 460));
        Assert.Equal(SwipeAway.Opacity(-120, 460), SwipeAway.Opacity(120, 460));
        Assert.InRange(SwipeAway.Opacity(120, 460), 0.5, 0.6);
        Assert.Equal(0.15, SwipeAway.Opacity(900, 460));
        Assert.Equal(1, SwipeAway.Opacity(50, 0));
    }

    [Fact]
    public void A_click_is_not_a_swipe() => Assert.True(SwipeAway.Start < SwipeAway.FlickTravel && SwipeAway.FlickTravel < SwipeAway.Leave);
}

public class PetMomentsTests
{
    [Fact]
    public void It_yawns_only_when_it_nods_off_from_rest()
    {
        Assert.True(PetMoments.Yawns(PetMood.Idle, PetMood.Sleepy));
        Assert.False(PetMoments.Yawns(PetMood.Music, PetMood.Sleepy));
        Assert.False(PetMoments.Yawns(PetMood.Sleepy, PetMood.Idle));
    }

    [Fact]
    public void It_starts_awake_only_from_sleep()
    {
        Assert.True(PetMoments.WakesUp(PetMood.Sleepy, PetMood.Idle));
        Assert.False(PetMoments.WakesUp(PetMood.Sleepy, PetMood.Notify));
        Assert.False(PetMoments.WakesUp(PetMood.Idle, PetMood.Idle));
    }

    [Fact]
    public void It_greets_you_back_at_most_every_ten_minutes()
    {
        var now = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        Assert.True(PetMoments.Greets(now, null));
        Assert.False(PetMoments.Greets(now, now.AddMinutes(-3)));
        Assert.True(PetMoments.Greets(now, now.AddMinutes(-10)));
    }
}
