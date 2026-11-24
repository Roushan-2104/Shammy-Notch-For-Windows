using Shammy.Core.MeetingSystem;

namespace Shammy.Core.Tests;

public class StaleMicTests
{
    [Fact]
    public void Windows_settings_is_named_settings_not_its_package_name()
    {
        Assert.Equal("Settings", AppNames.Friendly("windows.immersivecontrolpanel_cw5n1h2txyewy"));
    }

    static readonly DateTime Now = new(2026, 11, 24, 10, 42, 0, DateTimeKind.Utc);

    [Fact]
    public void An_app_capturing_sound_right_now_always_counts()
    {
        Assert.True(CapabilityUsage.TrustRecord(capturingNow: true, null, Now.AddHours(-3), Now));
    }

    [Fact]
    public void Settings_left_in_the_background_after_its_mic_test_is_no_call()
    {
        // Its record started 11 minutes ago and it hasn't captured since: stale, whatever else has the mic.
        Assert.False(CapabilityUsage.TrustRecord(capturingNow: false, null, Now.AddMinutes(-11), Now));
    }

    [Fact]
    public void A_record_gets_a_short_grace_after_it_starts_or_its_app_last_captured()
    {
        Assert.True(CapabilityUsage.TrustRecord(false, null, Now.AddSeconds(-5), Now));
        Assert.True(CapabilityUsage.TrustRecord(false, null, Now - CapabilityUsage.TrustWithoutCapture, Now));
        Assert.False(CapabilityUsage.TrustRecord(false, null, Now - CapabilityUsage.TrustWithoutCapture - TimeSpan.FromSeconds(1), Now));
    }

    [Fact]
    public void The_grace_counts_from_the_later_of_its_last_capture_and_its_start()
    {
        // Captured 10 s ago on a record from an hour ago: still within the grace.
        Assert.True(CapabilityUsage.TrustRecord(false, Now.AddSeconds(-10), Now.AddHours(-1), Now));
        // A fresh record after an old capture from an earlier use: the grace counts from the record's start.
        Assert.True(CapabilityUsage.TrustRecord(false, Now.AddHours(-2), Now.AddSeconds(-10), Now));
        Assert.False(CapabilityUsage.TrustRecord(false, Now.AddHours(-2), Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void A_start_time_in_the_future_is_not_recent()
    {
        // The clock was 10 minutes ahead when Settings wrote its record, then was set right.
        Assert.False(CapabilityUsage.TrustRecord(false, null, Now.AddMinutes(10), Now));
        Assert.False(CapabilityUsage.TrustRecord(false, DateTime.MinValue, DateTime.MinValue, Now));
    }
}
