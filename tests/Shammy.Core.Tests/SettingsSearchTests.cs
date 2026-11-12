using Shammy.Core.Palette;

namespace Shammy.Core.Tests;

public class ShammySettingSearchTests
{
    [Theory]
    [InlineData("eye br", "Eye breaks Every 20 minutes the pet rests its eyes")]
    [InlineData("EYE", "Eye style")]
    [InlineData("hide while", "Hide while using")]
    [InlineData("", "anything")]
    [InlineData("pet’s", "Motion Calm stops the pet’s extras")]
    public void Every_word_typed_begins_a_word_of_the_row(string query, string row) => Assert.True(ShammySettingSearch.Matches(query, row));

    [Theory]
    [InlineData("reaks", "Eye breaks")]
    [InlineData("eye water", "Eye breaks")]
    [InlineData("dock", "Right now line One line at the top of the open notch")]
    public void A_word_in_the_middle_or_a_missing_word_does_not_match(string query, string row) => Assert.False(ShammySettingSearch.Matches(query, row));

    [Theory]
    [InlineData("peek", "Peek", 100)]
    [InlineData("turn off peek", "Peek", 100)]
    [InlineData("peek settings", "Peek", 100)]
    [InlineData("eye b", "Eye breaks", 90)]
    [InlineData("breaks eye", "Eye breaks", 85)]
    [InlineData("nudge", "Eye breaks", 0)]
    [InlineData("pi", "Pin to the very top edge", 0)]
    [InlineData("", "Peek", 0)]
    public void Scores_rank_the_label_above_the_hint(string query, string label, int score) =>
        Assert.Equal(score, ShammySettingSearch.Score(query, label, "Every 20 minutes the pet rests its eyes", "Routines"));

    [Fact]
    public void A_hint_word_alone_is_a_weaker_match() =>
        Assert.Equal(70, ShammySettingSearch.Score("rests", "Eye breaks", "Every 20 minutes the pet rests its eyes", "Routines"));
}
