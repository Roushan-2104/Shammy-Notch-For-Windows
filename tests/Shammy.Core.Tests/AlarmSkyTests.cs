using Shammy.Core.Productivity;
using Xunit;

namespace Shammy.Core.Tests;

public class AlarmSkyTests
{
    [Theory]
    [InlineData(4, Sky.Night)]
    [InlineData(5, Sky.Dawn)]
    [InlineData(8, Sky.Dawn)]
    [InlineData(9, Sky.Day)]
    [InlineData(15, Sky.Day)]
    [InlineData(16, Sky.Sunset)]
    [InlineData(18, Sky.Sunset)]
    [InlineData(19, Sky.Night)]
    [InlineData(0, Sky.Night)]
    public void The_sky_follows_the_hour(int hour, Sky sky) => Assert.Equal(sky, AlarmSky.At(hour));

    [Fact]
    public void The_sky_rises_over_thirty_seconds()
    {
        Assert.Equal(0, AlarmSky.Risen(TimeSpan.FromSeconds(-3)));
        Assert.Equal(0.5, AlarmSky.Risen(TimeSpan.FromSeconds(15)), 3);
        Assert.Equal(1, AlarmSky.Risen(TimeSpan.FromMinutes(4)));
    }

    static readonly DateTime Now = new(2026, 11, 15, 1, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Glows_for_the_two_minutes_before_a_ring()
    {
        Assert.Equal(Now.AddSeconds(90), AlarmSky.Glowing(new DateTime?[] { null, Now.AddMinutes(30), Now.AddSeconds(90) }, Now));
        Assert.Null(AlarmSky.Glowing(new DateTime?[] { Now.AddMinutes(3), Now.AddSeconds(-5), null }, Now));
    }

    [Theory]
    [InlineData("24° Bhubaneswar", "Bhubaneswar")]
    [InlineData("-3° New York", "New York")]
    [InlineData("24°", "")]
    [InlineData("Sunny", "")]
    [InlineData(null, "")]
    public void The_place_after_the_temperature(string? glance, string place) => Assert.Equal(place, AlarmText.Place(glance));
}
