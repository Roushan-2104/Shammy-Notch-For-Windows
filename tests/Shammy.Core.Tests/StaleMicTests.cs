using Shammy.Core.MeetingSystem;

namespace Shammy.Core.Tests;

public class StaleMicTests
{
    [Fact]
    public void Windows_settings_is_named_settings_not_its_package_name()
    {
        Assert.Equal("Settings", AppNames.Friendly("windows.immersivecontrolpanel_cw5n1h2txyewy"));
        // Any other all-lowercase package name at least starts with a capital.
        Assert.Equal("Somerecorder", AppNames.Friendly("contoso.somerecorder_abcdefghjk123"));
    }

    static readonly DateTime Now = new(2026, 11, 24, 10, 42, 0, DateTimeKind.Utc);

    [Fact]
    public void An_app_capturing_sound_right_now_always_counts()
    {
        Assert.True(CapabilityUsage.TrustRecord(capturingNow: true, Now.AddHours(-3), Now));
    }

    [Fact]
    public void Settings_left_in_the_background_after_its_mic_test_is_no_call()
    {
        // Its record started 11 minutes ago and it hasn't captured since: stale, whatever else has the mic.
        Assert.False(CapabilityUsage.TrustRecord(capturingNow: false, Now.AddMinutes(-11), Now));
    }

    [Fact]
    public void A_record_gets_a_short_grace_after_it_starts_or_its_app_last_captured()
    {
        Assert.True(CapabilityUsage.TrustRecord(false, Now.AddSeconds(-5), Now));
        Assert.True(CapabilityUsage.TrustRecord(false, Now - CapabilityUsage.TrustWithoutCapture, Now));
        Assert.False(CapabilityUsage.TrustRecord(false, Now - CapabilityUsage.TrustWithoutCapture - TimeSpan.FromSeconds(1), Now));
    }
}
