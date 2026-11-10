using Shammy.Core.Audio;
using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class CatTests
{
    [Fact]
    public void A_resting_cat_has_slit_pupils_and_upright_ears()
    {
        var pose = CatLooks.Pose(PetMood.Idle);
        Assert.Equal(new CatPose(0, 1, 0, 0, 0, CatLooks.Slit), pose);
        Assert.True(CatLooks.Slit < CatLooks.RoundFrom);
    }

    [Fact]
    public void A_surprise_widens_the_pupils_and_raises_the_ears()
    {
        var pose = CatLooks.Pose(PetMood.Notify);
        Assert.True(pose.Pupil >= CatLooks.RoundFrom);
        Assert.True(pose.EarHeight > 1);
        Assert.True(pose.WhiskerLift < 0);
    }

    [Theory]
    [InlineData(PetMood.Sleepy)]
    [InlineData(PetMood.Music)]
    [InlineData(PetMood.Happy)]
    [InlineData(PetMood.TimerDone)]
    public void Closed_or_smiling_eyes_have_no_pupil(PetMood mood) => Assert.Equal(0, CatLooks.Pose(mood).Pupil);

    [Fact]
    public void A_sleeping_cat_folds_its_ears_further_than_a_tired_one()
    {
        var tired = CatLooks.Pose(PetMood.Tired);
        var asleep = CatLooks.Pose(PetMood.Sleepy);
        Assert.True(asleep.EarTilt > tired.EarTilt && tired.EarTilt > 0);
        Assert.True(asleep.EarDrop > tired.EarDrop);
        Assert.True(asleep.EarHeight < tired.EarHeight);
    }

    [Fact]
    public void Only_the_listening_and_thinking_cat_swivels_one_ear()
    {
        foreach (var mood in Enum.GetValues<PetMood>())
            Assert.Equal(mood is PetMood.Listening or PetMood.Thinking, CatLooks.Pose(mood).EarTurn > 0);
    }

    [Theory]
    // The resting eye, the wide-open one, the narrowed one and the drooping one.
    [InlineData(11, 13, 1.25, 1.25, 3.9)]
    [InlineData(11, 15, 2.5, 2.5, 4.9)]
    [InlineData(11, 9, 1.25, 1.25, 2.9)]
    [InlineData(11, 8, 1.3, 1.3, 2.4)]
    // An oval eye is narrower: the pupil never crowds its edges.
    [InlineData(9, 14.6, 2.5, 2.1, 4.7)]
    public void The_pupil_fits_inside_its_eye(double w, double h, double open, double halfWidth, double halfHeight)
    {
        var pupil = CatLooks.Pupil(w, h, open);
        Assert.Equal(halfWidth, pupil.HalfWidth, 2);
        Assert.Equal(halfHeight, pupil.HalfHeight, 2);
        Assert.True(pupil.HalfWidth * 2 < w - 4);
        Assert.True(pupil.HalfHeight * 2 < h);
    }

    [Theory]
    [InlineData(12, 3, 1.25)]
    [InlineData(11, 13, 0)]
    [InlineData(4, 13, 1.25)]
    public void A_lid_a_closed_pupil_or_a_sliver_of_an_eye_shows_none(double w, double h, double open) =>
        Assert.Equal((0, 0), CatLooks.Pupil(w, h, open));

    [Fact]
    public void A_narrow_pupil_is_a_pointed_slit_and_a_wide_one_is_round()
    {
        Assert.Equal("M0,-3.9 Q2.5,0 0,3.9 Q-2.5,0 0,-3.9 Z", CatLooks.PupilPath(1.25, 3.9));
        string round = CatLooks.PupilPath(2.5, 4.9);
        Assert.StartsWith("M0,-4.9 A2.5,4.9", round);
        Assert.DoesNotContain("Q", round);
    }

    [Fact]
    public void Pupil_paths_use_a_dot_whatever_the_language()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("M0,-3.9 Q2.5,0 0,3.9 Q-2.5,0 0,-3.9 Z", CatLooks.PupilPath(1.25, 3.9));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1.2)]
    [InlineData(-2, -1.2)]
    [InlineData(10, 1.7)]
    [InlineData(-10, -1.7)]
    public void Pupils_lead_the_look_but_stay_in_the_eye(double look, double shift) => Assert.Equal(shift, CatLooks.PupilShift(look), 3);

    [Fact]
    public void The_cat_fidgets_every_four_to_eight_seconds_and_only_when_at_rest()
    {
        var random = new Random(3);
        for (int i = 0; i < 200; i++) Assert.InRange(CatLooks.NextFidget(random).TotalSeconds, 4, 8);
        Assert.True(CatLooks.Fidgets(PetMood.Idle));
        Assert.True(CatLooks.Fidgets(PetMood.Listening));
        Assert.False(CatLooks.Fidgets(PetMood.Sleepy));
        Assert.False(CatLooks.Fidgets(PetMood.Music));
        Assert.False(CatLooks.Fidgets(PetMood.Notify));
    }

    [Fact]
    public void The_purr_is_quieter_than_a_ping_and_click_free()
    {
        var purr = Ping.Render(PingKind.Purr);
        double seconds = (double)purr.Length / Ping.SampleRate;
        Assert.InRange(seconds, 1.4, 2.0);
        int peak = purr.Max(x => Math.Abs((int)x));
        Assert.InRange(peak, 5000, 7000);
        Assert.True(peak < Ping.Render(PingKind.Notification).Max(x => Math.Abs((int)x)));
        Assert.True(Math.Abs((int)purr[0]) < 200);
        Assert.True(Math.Abs((int)purr[^1]) < 200);
    }

    [Fact]
    public void The_purr_thumps_about_twenty_five_times_a_second()
    {
        var purr = Ping.Render(PingKind.Purr);
        // Loudness in 5 ms steps over the steady second in the middle; count the rises through its mean.
        int step = Ping.SampleRate / 200;
        var loud = new List<double>();
        for (int i = (int)(0.3 * Ping.SampleRate); i + step < (int)(1.3 * Ping.SampleRate); i += step)
            loud.Add(Math.Sqrt(purr.Skip(i).Take(step).Average(x => (double)x * x)));
        double mean = loud.Average();
        int rises = 0;
        for (int i = 1; i < loud.Count; i++)
            if (loud[i - 1] < mean && loud[i] >= mean) rises++;
        Assert.InRange(rises, 18, 32);
    }

    [Fact]
    public void The_purr_is_the_same_every_time() => Assert.Equal(Ping.Render(PingKind.Purr), Ping.Render(PingKind.Purr));
}
