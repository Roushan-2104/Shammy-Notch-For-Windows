using Shammy.Core.Productivity;

namespace Shammy.Core.Tests;

public class BriefLaterInTheDayTests
{
    static readonly DateTime Today = new(2026, 11, 12);

    [Fact]
    public void Afternoon_after_the_meetings()
    {
        var now = Today.AddHours(15);
        var (spoken, line) = MorningBrief.Compose(now, null, new[] { new BriefMeeting(Today.AddHours(10), "Standup") }, Array.Empty<string>(), null, true);
        Assert.Equal("Good afternoon! No more meetings today.", spoken);
        Assert.Equal("Good afternoon · no more meetings", line);
    }

    [Fact]
    public void Mid_meeting_speaks_of_the_rest()
    {
        var now = Today.AddHours(10.5);
        var (spoken, _) = MorningBrief.Compose(now, null,
            new[] { new BriefMeeting(Today.AddHours(10), "Standup"), new BriefMeeting(Today.AddHours(14), "Review") }, Array.Empty<string>(), null, true);
        Assert.Equal("Good morning! One more meeting today, at 14:00, Review.", spoken);
    }

    [Theory]
    [InlineData(8, "Good morning")]
    [InlineData(13, "Good afternoon")]
    [InlineData(19, "Good evening")]
    [InlineData(2, "Hello")]
    public void Greets_by_time_of_day(int hour, string hello) => Assert.Equal(hello, MorningBrief.Greeting(Today.AddHours(hour)));

    [Fact]
    public void Evening_clear_day() =>
        Assert.Equal("Good evening · a clear day", MorningBrief.Compose(Today.AddHours(20), null, Array.Empty<BriefMeeting>(), Array.Empty<string>(), null, true).Line);
}
