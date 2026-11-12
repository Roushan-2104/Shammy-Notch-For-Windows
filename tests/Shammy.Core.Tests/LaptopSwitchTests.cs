using Shammy.Core.Quick;

namespace Shammy.Core.Tests;

public class LaptopSwitchPhraseTests
{
    [Theory]
    [InlineData("turn off wifi", QuickKind.Wifi, false)]
    [InlineData("Turn off Wi-Fi.", QuickKind.Wifi, false)]
    [InlineData("wifi on", QuickKind.Wifi, true)]
    [InlineData("wi fi off", QuickKind.Wifi, false)]
    [InlineData("switch on the wifi", QuickKind.Wifi, true)]
    [InlineData("turn my wifi off", QuickKind.Wifi, false)]
    [InlineData("disable wifi", QuickKind.Wifi, false)]
    [InlineData("hey shammy, wifi off", QuickKind.Wifi, false)]
    [InlineData("wifi band karo", QuickKind.Wifi, false)]
    [InlineData("wifi band kar do", QuickKind.Wifi, false)]
    [InlineData("wifi ko off karo", QuickKind.Wifi, false)]
    [InlineData("wifi chalu karo", QuickKind.Wifi, true)]
    [InlineData("bluetooth off", QuickKind.Bluetooth, false)]
    [InlineData("turn on bluetooth", QuickKind.Bluetooth, true)]
    [InlineData("blue tooth on", QuickKind.Bluetooth, true)]
    [InlineData("bluetooth chalu karo", QuickKind.Bluetooth, true)]
    [InlineData("bluetooth band kardo", QuickKind.Bluetooth, false)]
    [InlineData("night light on", QuickKind.NightLight, true)]
    [InlineData("turn off night light", QuickKind.NightLight, false)]
    [InlineData("nightlight off", QuickKind.NightLight, false)]
    [InlineData("enable night light", QuickKind.NightLight, true)]
    public void A_switch_said_directly(string text, QuickKind kind, bool on) =>
        Assert.Equal(new QuickCommand(kind, On: on), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("do not disturb", null)]
    [InlineData("Don't disturb", null)]
    [InlineData("dnd", null)]
    [InlineData("turn on do not disturb", null)]
    [InlineData("quiet mode", null)]
    [InlineData("quiet mode on", null)]
    [InlineData("go quiet", null)]
    [InlineData("disturb mat karo", null)]
    [InlineData("quiet for an hour", 60)]
    [InlineData("do not disturb for 2 hours", 120)]
    [InlineData("dnd for 30 minutes", 30)]
    public void Quiet_starts(string text, int? minutes) =>
        Assert.Equal(new QuickCommand(QuickKind.Quiet, minutes, On: true), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("do not disturb off")]
    [InlineData("turn off do not disturb")]
    [InlineData("dnd off")]
    [InlineData("stop quiet mode")]
    [InlineData("quiet mode off")]
    [InlineData("end do not disturb")]
    public void Quiet_ends(string text) => Assert.Equal(new QuickCommand(QuickKind.Quiet, On: false), QuickPhrases.Parse(text));

    [Theory]
    [InlineData("performance mode", PowerModeKind.Performance)]
    [InlineData("best performance", PowerModeKind.Performance)]
    [InlineData("best battery", PowerModeKind.Efficiency)]
    [InlineData("balanced mode", PowerModeKind.Balanced)]
    [InlineData("switch to balanced mode", PowerModeKind.Balanced)]
    [InlineData("set power mode to best battery", PowerModeKind.Efficiency)]
    [InlineData("power mode performance", PowerModeKind.Performance)]
    [InlineData("battery saver mode", PowerModeKind.Efficiency)]
    public void Power_mode_said_directly(string text, PowerModeKind mode)
    {
        Assert.Equal(mode, PowerMode.Parse(text));
        Assert.Equal(new QuickCommand(QuickKind.PowerMode, Mode: mode), QuickPhrases.Parse(text));
    }

    [Theory]
    [InlineData("wifi")]
    [InlineData("bluetooth")]
    [InlineData("night light")]
    [InlineData("wifi password")]
    [InlineData("wifi off kaise kare")]
    [InlineData("how to turn off wifi")]
    [InlineData("turn on bluetooth headphones")]
    [InlineData("bluetooth speaker on sale")]
    [InlineData("night light app")]
    [InlineData("quiet")]
    [InlineData("quiet places in delhi")]
    [InlineData("do not disturb song")]
    [InlineData("performance")]
    [InlineData("battery saver")]
    [InlineData("how to change power mode")]
    [InlineData("gaming mode")]
    [InlineData("balanced diet")]
    [InlineData("restart")]
    [InlineData("shut down")]
    [InlineData("laptop")]
    [InlineData("laptop deals")]
    public void Anything_else_is_left_alone(string text) => Assert.Null(QuickPhrases.Parse(text));

    [Theory]
    [InlineData("laptop controls")]
    [InlineData("open laptop")]
    [InlineData("quick actions")]
    public void The_laptop_card_opens(string text) => Assert.Equal(new QuickCommand(QuickKind.Open), QuickPhrases.Parse(text));

    [Fact]
    public void A_very_long_quiet_is_capped() =>
        Assert.Equal(SwitchPhrases.QuietMaxMinutes, QuickPhrases.Parse("do not disturb for 20 hours")!.Minutes);
}
