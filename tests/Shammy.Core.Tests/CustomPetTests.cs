using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class CustomPetTests
{
    static readonly CircleSpec Stop = new(CircleAction.Stop, CircleTone.Neutral, true, "Stop");

    [Fact]
    public void A_module_picks_its_own_face_and_circle()
    {
        var s = new PetActivityState { Costume = PetCostume.Custom, Face = PetFace.Focused, Circle = Stop };
        Assert.Equal(PetFace.Focused, PetActivities.Face(s));
        Assert.Equal(Stop, PetActivities.Circle(s));
    }

    [Fact]
    public void With_no_face_or_circle_it_is_wide_eyes_and_nothing_to_press()
    {
        var s = new PetActivityState { Costume = PetCostume.Custom };
        Assert.Equal(PetFace.Notify, PetActivities.Face(s));
        Assert.Null(PetActivities.Circle(s));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void It_waits_as_the_dot_only_when_it_says_it_keeps_going(bool ongoing, bool expected) =>
        Assert.Equal(expected, PetActivities.IsOngoing(new PetActivityState { Costume = PetCostume.Custom, Ongoing = ongoing }));

    [Fact]
    public void Clicking_the_pet_runs_a_passing_moment_that_opens_something_but_never_an_ongoing_one()
    {
        Assert.True(PetActivities.BodyClickRunsActivity(new PetActivityState { Costume = PetCostume.Custom, CanOpen = true }));
        Assert.False(PetActivities.BodyClickRunsActivity(new PetActivityState { Costume = PetCostume.Custom, CanOpen = true, Ongoing = true }));
        Assert.False(PetActivities.BodyClickRunsActivity(new PetActivityState { Costume = PetCostume.Custom }));
    }

    [Fact]
    public void A_face_override_also_works_on_a_built_in_costume() =>
        Assert.Equal(PetFace.Happy, PetActivities.Face(new PetActivityState { Costume = PetCostume.Timer, Face = PetFace.Happy }));

    [Theory]
    [InlineData(PetFace.Disc, true)]
    [InlineData(PetFace.DiscPaused, true)]
    [InlineData(PetFace.Call, false)]
    [InlineData(PetFace.Notify, false)]
    public void Only_the_record_shows_with_the_pet_turned_off(PetFace face, bool expected) =>
        Assert.Equal(expected, PetActivities.ShowsWithoutPet(face));

    [Fact]
    public void Music_is_a_record_that_stops_when_paused()
    {
        Assert.Equal(PetFace.Disc, PetActivities.Face(new PetActivityState { Costume = PetCostume.Music, Playing = true }));
        Assert.Equal(PetFace.DiscPaused, PetActivities.Face(new PetActivityState { Costume = PetCostume.Music }));
    }
}
