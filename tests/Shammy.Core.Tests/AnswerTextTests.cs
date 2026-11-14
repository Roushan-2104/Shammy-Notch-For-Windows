using Shammy.Core.Search;
using Xunit;

namespace Shammy.Core.Tests;

public class AnswerTextTests
{
    [Theory]
    [InlineData("100%")]
    [InlineData("9:45 AM")]
    [InlineData("152.4 cm")]
    [InlineData("₹9,425")]
    [InlineData("$12.50")]
    [InlineData("-4 °C")]
    [InlineData("22.05 lb")]
    public void Values_are_set_as_numbers(string text) => Assert.True(AnswerText.IsValue(text));

    [Theory]
    [InlineData("Reminder set")]
    [InlineData("Alarm · 6:30 AM")]
    [InlineData("Tomorrow in Mumbai, India")]
    [InlineData("3 timers are running now")]
    [InlineData("12345678901234567890")]
    [InlineData("")]
    [InlineData(null)]
    public void Words_stay_bold(string? text) => Assert.False(AnswerText.IsValue(text));
}
