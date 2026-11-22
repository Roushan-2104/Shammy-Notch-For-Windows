using Shammy.Core.Connect;
using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class MomentPriorityTests
{
    [Fact]
    public void Calls_meetings_and_focus_let_a_quiet_peek_through_but_your_own_quiet_switch_holds_everything()
    {
        Assert.True(QuietRules.PeeksThrough(new[] { QuietReason.Call }));
        Assert.True(QuietRules.PeeksThrough(new[] { QuietReason.Meeting, QuietReason.Focus }));
        Assert.False(QuietRules.PeeksThrough(new[] { QuietReason.DoNotDisturb }));
        Assert.False(QuietRules.PeeksThrough(new[] { QuietReason.Call, QuietReason.DoNotDisturb }));
        Assert.False(QuietRules.PeeksThrough(new[] { QuietReason.Test }));
        Assert.False(QuietRules.PeeksThrough(Array.Empty<QuietReason>()));
    }

    [Theory]
    [InlineData("WhatsApp", "WhatsApp", false, true)]
    [InlineData("whatsapp ", "WhatsApp", false, true)]
    [InlineData("Chrome", "WhatsApp", false, false)]
    [InlineData("WhatsApp", "WhatsApp", true, false)]   // from the phone: the laptop's front app doesn't matter
    [InlineData(null, "WhatsApp", false, false)]
    [InlineData("WhatsApp", "", false, false)]
    [InlineData("Chrome", "Google Chrome", false, true)]      // short name in front, full name on the toast
    [InlineData("Teams", "Microsoft Teams", false, true)]
    [InlineData("Teams", "Teamspeak", false, false)]          // a whole word only
    public void A_message_from_the_app_in_front_needs_no_pop_up(string? front, string app, bool fromPhone, bool expected) =>
        Assert.Equal(expected, QuietRules.FromAppInFront(front, app, fromPhone));

    [Fact]
    public void A_peek_is_who_and_the_first_words_cut_to_fit_and_nothing_while_sharing()
    {
        Assert.Equal("Riya: are you joining at 8?", PetActivities.PeekValue("Riya", "are you joining at 8?", "WhatsApp", presenting: false));
        Assert.Equal("", PetActivities.PeekValue("Riya", "are you joining at 8?", "WhatsApp", presenting: true));
        // A title that only repeats the app: just the words.
        Assert.Equal("Build finished", PetActivities.PeekValue("GitHub", "Build   finished", "GitHub", presenting: false));
        var longOne = PetActivities.PeekValue("Riya", new string('a', 100), "WhatsApp", presenting: false, max: 30);
        Assert.Equal(30, longOne.Length);
        Assert.EndsWith("…", longOne);
    }
}
