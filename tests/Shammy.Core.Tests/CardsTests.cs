using Shammy.Core.Ambient;
using Shammy.Core.Cards;
using Shammy.Core.Devices;
using Shammy.Core.Productivity;

namespace Shammy.Core.Tests;

public class CardMathTests
{
    [Theory]
    [InlineData(118, 268, 0.4403)]
    [InlineData(0, 268, 0)]
    [InlineData(300, 268, 1)]   // a position past the end never overfills
    [InlineData(-5, 268, 0)]
    [InlineData(10, 0, 0)]      // no duration: empty, not a division by zero
    public void Fractions_stay_in_range(double value, double total, double expected) =>
        Assert.Equal(expected, CardMath.Fraction(value, total), 3);

    [Theory]
    [InlineData(81, 0.81)]
    [InlineData(120, 1)]
    [InlineData(-3, 0)]
    public void Percent_rings(double percent, double expected) => Assert.Equal(expected, CardMath.Percent(percent), 3);

    [Fact]
    public void Hourly_bars_scale_from_the_floor_to_full()
    {
        var h = CardMath.BarHeights(new[] { 31.0, 32, 33, 33, 32, 30 });
        Assert.Equal(6, h.Length);
        Assert.Equal(1, h[2], 3);           // warmest is full height
        Assert.Equal(0.3, h[5], 3);         // coldest sits on the floor
        Assert.Equal(0.3 + 0.7 / 3, h[0], 3);
        Assert.All(h, x => Assert.InRange(x, 0.3, 1));
    }

    [Fact]
    public void Flat_and_empty_series()
    {
        Assert.All(CardMath.BarHeights(new[] { 24.0, 24, 24 }), x => Assert.Equal(1, x));
        Assert.Empty(CardMath.BarHeights(Array.Empty<double>()));
    }

    [Fact]
    public void Day_ranges_share_one_scale()
    {
        var (left, width) = CardMath.RangeSpan(21, 33, 20, 33);
        Assert.Equal(1 / 13.0, left, 3);
        Assert.Equal(12 / 13.0, width, 3);
        // A day with no spread still shows a sliver; swapped values are put right.
        Assert.Equal(0.04, CardMath.RangeSpan(25, 25, 20, 33).Width, 3);
        Assert.Equal(CardMath.RangeSpan(21, 30, 20, 33), CardMath.RangeSpan(30, 21, 20, 33));
    }

    [Fact]
    public void Glance_text_is_fitted()
    {
        Assert.Equal("31\u00B0 Pune", CardMath.Fit("31\u00B0 Pune"));
        var fitted = CardMath.Fit("A very long song title indeed");
        Assert.True(fitted.Length <= 18, fitted);
        Assert.EndsWith("\u2026", fitted);
    }
}

public class MediaTimeTests
{
    [Fact]
    public void Scrubber_times()
    {
        Assert.Equal("1:58", MediaTime.Clock(TimeSpan.FromSeconds(118.9)));
        Assert.Equal("4:28", MediaTime.Clock(TimeSpan.FromSeconds(268)));
        Assert.Equal("1:02:03", MediaTime.Clock(TimeSpan.FromSeconds(3723)));
        Assert.Equal("0:00", MediaTime.Clock(TimeSpan.FromSeconds(-4)));
        Assert.Equal("-2:30", MediaTime.Remaining(TimeSpan.FromSeconds(118), TimeSpan.FromSeconds(268)));
        Assert.Equal("-0:00", MediaTime.Remaining(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(268)));
    }

    [Fact]
    public void Scrubber_hides_without_a_timeline()
    {
        Assert.False(MediaTime.HasTimeline(TimeSpan.Zero));
        Assert.True(MediaTime.HasTimeline(TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void Music_glance()
    {
        Assert.Equal("Kesariya \u00B7 Arijit", MediaTime.Glance("Kesariya", "Arijit"));
        // Too long with the artist: the title alone, fitted.
        Assert.Equal("Kesariya", MediaTime.Glance("Kesariya", "Arijit Singh"));
        Assert.True(MediaTime.Glance("Channa Mereya (From Ae Dil Hai Mushkil)", "Arijit Singh").Length <= 18);
    }
}

public class TimerCardTextTests
{
    [Fact]
    public void Glances()
    {
        Assert.Equal("Tea 4:32", TimerCardText.Glance("Tea", "4:32"));
        Assert.Equal("Timer 4:32", TimerCardText.Glance("", "4:32"));
        var long_ = TimerCardText.Glance("Laundry in the machine downstairs", "22:10");
        Assert.True(long_.Length <= 18, long_);
        Assert.EndsWith(" 22:10", long_);
    }

    [Fact]
    public void Ring_drains_as_time_runs_out()
    {
        Assert.Equal(272 / 300.0, TimerCardText.RingLeft(TimeSpan.FromSeconds(272), TimeSpan.FromMinutes(5)), 3);
        Assert.Equal(0, TimerCardText.RingLeft(TimeSpan.FromSeconds(-2), TimeSpan.FromMinutes(5)));
        Assert.Equal(0, TimerCardText.RingLeft(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        Assert.Equal(0.5, TimerCardText.StopwatchRing(TimeSpan.FromSeconds(90)), 3);
    }

    [Fact]
    public void Pomodoro_dots()
    {
        // Step 0: first focus round is running.
        Assert.Equal(new[] { RoundDot.Current, RoundDot.Pending, RoundDot.Pending, RoundDot.Pending }, TimerCardText.PomodoroDots(0));
        // Step 2: round 2 focus, round 1 done.
        Assert.Equal(new[] { RoundDot.Done, RoundDot.Current, RoundDot.Pending, RoundDot.Pending }, TimerCardText.PomodoroDots(2));
        // Step 3: the break after round 2, so round 2 counts as done.
        Assert.Equal(new[] { RoundDot.Done, RoundDot.Done, RoundDot.Pending, RoundDot.Pending }, TimerCardText.PomodoroDots(3));
        Assert.Equal("Focus \u00B7 round 2 of 4", TimerCardText.PomodoroLine(2));
        Assert.Equal("Break after round 2", TimerCardText.PomodoroLine(3));
        Assert.Equal("Long break", TimerCardText.PomodoroLine(7));
    }

    [Fact]
    public void Ends_and_length()
    {
        Assert.Equal("Ends at 10:24 AM", TimerCardText.Ends(new DateTime(2026, 11, 3, 10, 24, 0), false, "4:32"));
        Assert.Equal("Paused \u00B7 4:32 left", TimerCardText.Ends(DateTime.Now, true, "4:32"));
        Assert.Equal("of 5:00", TimerCardText.Of(TimeSpan.FromMinutes(5)));
        Assert.Equal(new[] { 5, 10, 25 }, TimerCardText.QuickMinutes);
    }
}

public class BluetoothBatteryTests
{
    // Instance ids read on a PC with Airdopes 311 Pro connected.
    const string HandsFreeAg = @"BTHENUM\{0000111E-0000-1000-8000-00805F9B34FB}_VID&000105D6_PID&000A\7&1DA13D4F&0&25BF04C71669_C00000000";
    const string DeviceNode = @"BTHENUM\DEV_25BF04C71669\7&1DA13D4F&0&BLUETOOTHDEVICE_25BF04C71669";
    const string SerialPort = @"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000\7&7047859&0&000000000000_0000000C";

    [Fact]
    public void Addresses_from_instance_ids()
    {
        Assert.Equal("25BF04C71669", BluetoothBattery.AddressFromInstanceId(HandsFreeAg));
        Assert.Equal("25BF04C71669", BluetoothBattery.AddressFromInstanceId(DeviceNode));
        Assert.Equal("D0A4CA1B2C3D", BluetoothBattery.AddressFromInstanceId(@"BTHLE\DEV_d0a4ca1b2c3d\8&1234&0&D0A4CA1B2C3D"));
        Assert.Null(BluetoothBattery.AddressFromInstanceId(SerialPort));   // a local port, not a device
        Assert.Null(BluetoothBattery.AddressFromInstanceId(@"BTHHFENUM\BTHHFPAUDIO\8&2EE311B7&1&97"));
        Assert.Null(BluetoothBattery.AddressFromInstanceId(""));
        Assert.Equal("25BF04C71669", BluetoothBattery.AddressFromNumber(0x25BF04C71669));
        Assert.Equal("00BF04C71669", BluetoothBattery.AddressFromNumber(0xBF04C71669));
    }

    [Fact]
    public void Parses_the_level_byte()
    {
        Assert.Equal(60, BluetoothBattery.ParsePercent(BluetoothBattery.TypeByte, new byte[] { 60 }));
        Assert.Equal(100, BluetoothBattery.ParsePercent(BluetoothBattery.TypeByte, new byte[] { 100, 0, 0 }));
        Assert.Null(BluetoothBattery.ParsePercent(BluetoothBattery.TypeByte, new byte[] { 255 }));   // not a level
        Assert.Null(BluetoothBattery.ParsePercent(BluetoothBattery.TypeByte, Array.Empty<byte>()));
        Assert.Null(BluetoothBattery.ParsePercent(0x12, new byte[] { 60, 0 }));                       // a string, not a level
        Assert.Equal(80, BluetoothBattery.ParsePercent(0x07, BitConverter.GetBytes(80)));
    }

    [Fact]
    public void One_level_per_headset()
    {
        var levels = BluetoothBattery.ByAddress(new[] { (HandsFreeAg, 60), (DeviceNode, 55), (SerialPort, 40) });
        Assert.Single(levels);
        Assert.Equal(60, levels["25bf04c71669"]);
    }

    [Fact]
    public void Connect_pill()
    {
        Assert.Equal("Airdopes 311 Pro \u00B7 80%", BluetoothBattery.Pill("Airdopes 311 Pro", 80));
        Assert.Equal("Airdopes 311 Pro", BluetoothBattery.Pill("Airdopes 311 Pro", null));
    }
}

public class PowerTextTests
{
    [Theory]
    [InlineData(240, "About 4 h left")]
    [InlineData(80, "About 1 h 20 min left")]
    [InlineData(118, "About 2 h left")]   // 1 h 58 rounds to the hour, not "1 h 60 min"
    [InlineData(35, "About 35 min left")]
    [InlineData(0.5, "Almost empty")]
    public void Time_left(double minutes, string expected) => Assert.Equal(expected, PowerText.TimeLeft(TimeSpan.FromMinutes(minutes)));

    [Fact]
    public void Full_by()
    {
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        var at = PowerText.FullAt(80, 30, now);
        Assert.Equal(new DateTime(2026, 11, 3, 10, 40, 0), at);
        Assert.Equal("Full by 10:40 AM", PowerText.FullBy(at!.Value));
        Assert.Equal("Charging \u00B7 full by 10:40", PowerText.ChargingLine(at));
        Assert.Equal("Charging", PowerText.ChargingLine(null));
        Assert.Null(PowerText.FullAt(80, null, now));
        Assert.Null(PowerText.FullAt(80, 0.1, now));
        Assert.Null(PowerText.FullAt(100, 30, now));
    }

    [Fact]
    public void Status_and_low()
    {
        Assert.Equal("Charging", PowerText.Status(true, true, true, 50));
        Assert.Equal("Fully charged", PowerText.Status(true, true, false, 100));
        Assert.Equal("On battery", PowerText.Status(true, false, false, 50));
        Assert.Equal("No battery", PowerText.Status(false, true, false, 0));
        Assert.True(PowerText.IsLow(20, false));
        Assert.False(PowerText.IsLow(20, true));
        Assert.False(PowerText.IsLow(21, false));
    }
}

public class WeatherCardDataTests
{
    const string Forecast = """
        {"utc_offset_seconds":19800,
         "current":{"time":"2026-11-03T11:00","temperature_2m":31.2,"weather_code":2,"is_day":1,"apparent_temperature":34.1,
                    "relative_humidity_2m":48,"wind_speed_10m":9.4},
         "hourly":{"time":["2026-11-03T12:00"],"temperature_2m":[32.0],"precipitation_probability":[5],"weather_code":[2]},
         "daily":{"time":["2026-11-03","2026-11-04","2026-11-05","2026-11-06","2026-11-07"],
                  "temperature_2m_max":[33.0,32.1,30.0,29.4,null],"temperature_2m_min":[21.0,22.0,21.2,20.1,21.0],
                  "weather_code":[2,0,0,3,1],"precipitation_probability_max":[5,0,10,20,0]}}
        """;

    [Fact]
    public void Parses_days_humidity_and_wind()
    {
        var r = OpenMeteo.Parse(Forecast)!;
        Assert.Equal(48, r.Humidity);
        Assert.Equal(9.4, r.WindKmh);
        Assert.Equal(4, r.Days.Count);   // the day without a high is left out, not invented
        Assert.Equal(new DateTime(2026, 11, 4), r.Days[1].Date);
        Assert.Equal(32.1, r.Days[1].High);
        Assert.Equal(0, r.Days[1].Code);
        Assert.Equal(20, r.Days[3].RainChance);
        Assert.Equal(33.0, r.TodayHigh);
    }

    [Fact]
    public void Missing_extras_stay_missing()
    {
        var r = OpenMeteo.Parse("""
            {"current":{"time":"2026-11-03T11:00","temperature_2m":31,"weather_code":2},
             "daily":{"temperature_2m_max":[33],"temperature_2m_min":[21]}}
            """)!;
        Assert.Null(r.Humidity);
        Assert.Null(r.WindKmh);
        Assert.Empty(r.Days);
    }

    [Fact]
    public void Card_words()
    {
        var r = OpenMeteo.Parse(Forecast)!;
        Assert.Equal("31\u00B0 Pune", WeatherText.Glance(r, "Pune"));
        Assert.Equal("9 km/h", WeatherText.Wind(9.4));
        var today = new DateTime(2026, 11, 3);
        Assert.Equal("Today", WeatherText.DayLabel(today, today));
        Assert.Equal("Wed", WeatherText.DayLabel(today.AddDays(1), today));
        Assert.Contains("forecast_days=5", OpenMeteo.ForecastUrl(18.52, 73.86));
        Assert.Contains("relative_humidity_2m", OpenMeteo.ForecastUrl(18.52, 73.86));
    }
}
