using Shammy.Core.Notch;

namespace Shammy.Core.Tests;

public class TuckAwayTests
{
    [Fact]
    public void It_comes_back_after_a_few_seconds() => Assert.InRange(TuckAway.Away.TotalSeconds, 3, 8);
}
