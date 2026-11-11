using Shammy.Core.Wheel;

namespace Shammy.Core.Tests;

public class WheelGestureTests
{
    [Fact]
    public void A_quick_click_goes_back_to_the_app()
    {
        var g = new WheelGesture();
        Assert.Equal(WheelStep.Swallow, g.Down(100, 100, 0, enabled: true));
        Assert.Equal(WheelStep.Pass, g.Move(102, 99));
        Assert.Equal(WheelStep.SwallowAndReplayClick, g.Up(120));
        Assert.Equal(WheelGesture.Phase.Idle, g.Current);
    }

    [Fact]
    public void Moving_first_is_a_drag_the_app_gets()
    {
        var g = new WheelGesture();
        g.Down(100, 100, 0, true);
        Assert.Equal(WheelStep.ReplayDownThenPass, g.Move(120, 100));
        Assert.Equal(WheelStep.Pass, g.Move(140, 100));
        Assert.False(g.HoldElapsed(300));
        Assert.Equal(WheelStep.Pass, g.Up(400));
    }

    [Fact]
    public void Holding_still_opens_the_wheel_and_the_release_is_ours()
    {
        var g = new WheelGesture();
        g.Down(100, 100, 0, true);
        Assert.False(g.HoldElapsed(100));
        Assert.True(g.HoldElapsed(250));
        Assert.Equal(WheelGesture.Phase.Open, g.Current);
        Assert.Equal(WheelStep.Pass, g.Move(180, 40)); // moves always reach the screen
        Assert.Equal(WheelStep.SwallowAndRelease, g.Up(900));
    }

    [Fact]
    public void A_late_timer_still_gives_the_app_its_click() // the wheel never showed
    {
        var g = new WheelGesture();
        g.Down(5, 5, 0, true);
        Assert.Equal(WheelStep.SwallowAndReplayClick, g.Up(600));
    }

    [Fact]
    public void Switched_off_or_in_a_game_nothing_is_held_back()
    {
        var g = new WheelGesture();
        Assert.Equal(WheelStep.Pass, g.Down(1, 1, 0, enabled: false));
        Assert.False(g.HoldElapsed(1000));
        Assert.Equal(WheelStep.Pass, g.Up(1000));
    }

    [Fact]
    public void Reset_drops_an_open_wheel()
    {
        var g = new WheelGesture();
        g.Down(1, 1, 0, true);
        g.HoldElapsed(300);
        g.Reset();
        Assert.Equal(WheelStep.Pass, g.Up(400));
    }

    [Theory]
    [InlineData(0, -50, 0)]
    [InlineData(50, -50, 1)]
    [InlineData(50, 0, 2)]
    [InlineData(40, 40, 3)]
    [InlineData(0, 60, 4)]
    [InlineData(-40, 40, 5)]
    [InlineData(-60, 0, 6)]
    [InlineData(-40, -40, 7)]
    [InlineData(5, -10, -1)]
    [InlineData(-8, -60, 0)]
    public void Points_pick_beads_clockwise_from_the_top(double dx, double dy, int bead) => Assert.Equal(bead, WheelGeometry.Pick(dx, dy));
}

public class WheelSlotsTests
{
    static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0);
    static IReadOnlyDictionary<string, IReadOnlyList<DateTime>> Uses(params (string Id, int Count, int DaysAgo)[] uses) =>
        uses.GroupBy(u => u.Id).ToDictionary(g => g.Key, g => (IReadOnlyList<DateTime>)g.SelectMany(u => Enumerable.Repeat(Now.AddDays(-u.DaysAgo), u.Count)).ToList());

    [Fact]
    public void Starts_with_the_eight_drawn() =>
        Assert.Equal(WheelSlots.Defaults, WheelSlots.Arrange(null, Uses(), Now).Select(s => s.ActionId));

    [Fact]
    public void Something_used_a_lot_takes_the_weakest_unpinned_bead()
    {
        var slots = WheelSlots.Arrange(null, Uses(("captions", 6, 0), ("screenshot", 3, 0), ("record", 2, 0), ("timer", 2, 0), ("music", 4, 0), ("notes", 1, 0), ("ask", 2, 0), ("phone", 1, 0)), Now);
        Assert.Contains(slots, s => s.ActionId == "captions");
        Assert.DoesNotContain(slots, s => s.ActionId == "awake"); // never used: it made room
        Assert.Equal("screenshot", slots[0].ActionId); // the rest stay where they were
    }

    [Fact]
    public void A_little_use_doesnt_shuffle_the_wheel()
    {
        var slots = WheelSlots.Arrange(null, Uses(("captions", 1, 0), ("awake", 1, 0)), Now);
        Assert.Equal(WheelSlots.Defaults, slots.Select(s => s.ActionId));
    }

    [Fact]
    public void Pinned_beads_never_move()
    {
        var saved = WheelSlots.Defaults.Select((id, i) => new WheelSlot(id, Pinned: true)).ToList();
        var slots = WheelSlots.Arrange(saved, Uses(("captions", 50, 0)), Now);
        Assert.Equal(WheelSlots.Defaults, slots.Select(s => s.ActionId));
        Assert.All(slots, s => Assert.True(s.Pinned));
    }

    [Fact]
    public void Old_use_fades()
    {
        Assert.Equal(0.5, WheelSlots.Weight(Now.AddDays(-14), Now), 3);
        var slots = WheelSlots.Arrange(null, Uses(("captions", 6, 60)), Now); // two months ago: worth ~0.3
        Assert.DoesNotContain(slots, s => s.ActionId == "captions");
    }

    [Fact]
    public void A_damaged_save_is_repaired()
    {
        var saved = new[] { new WheelSlot("screenshot", true), new WheelSlot("screenshot", false), new WheelSlot("nonsense", false) };
        var slots = WheelSlots.Arrange(saved, Uses(), Now);
        Assert.Equal(8, slots.Count);
        Assert.Equal(8, slots.Select(s => s.ActionId).Distinct().Count());
        Assert.True(slots[0].Pinned);
    }

    [Theory]
    [InlineData("timer 10 minutes", "timer")]
    [InlineData("screenshot", "screenshot")]
    [InlineData("send this to my phone", "phone")]
    [InlineData("turn on captions", "captions")]
    [InlineData("play kesariya", "music")]
    [InlineData("keep my pc awake", "awake")]
    [InlineData("how tall is the eiffel tower", null)]
    public void Commands_count_for_their_action(string text, string? id) => Assert.Equal(id, WheelSlots.ActionFor(text));
}

public class ActivityExpiryTests
{
    [Fact]
    public void The_next_expiry_is_the_soonest_one_in_utc()
    {
        var m = new Shammy.Core.Activities.ActivityManager();
        Assert.Null(m.NextExpiryUtc);
        var soon = DateTime.UtcNow.AddSeconds(3);
        m.Post(new Shammy.Core.Activities.Activity("a", Shammy.Core.Activities.ActivityKind.Custom, 1, ExpiresAt: DateTime.UtcNow.AddSeconds(10)));
        m.Post(new Shammy.Core.Activities.Activity("b", Shammy.Core.Activities.ActivityKind.Custom, 1, ExpiresAt: soon.ToLocalTime()));
        m.Post(new Shammy.Core.Activities.Activity("c", Shammy.Core.Activities.ActivityKind.Custom, 1));
        Assert.Equal(soon, m.NextExpiryUtc!.Value, TimeSpan.FromMilliseconds(1));
        m.Remove("b");
        Assert.True(m.NextExpiryUtc > soon);
    }
}

public class WheelContextTests
{
    [Theory]
    [InlineData("Zoom", "Zoom Meeting", "Zoom", "captions,record,notes")]
    [InlineData("ms-teams", "Standup | Microsoft Teams", "Teams", "captions,record,notes")]
    [InlineData("chrome", "Daily sync - Google Meet - Google Chrome", "Google Meet", "captions,record,notes")]
    [InlineData("chrome.exe", "Kesariya - YouTube - Google Chrome", "Youtube", "captions,mixer")]
    [InlineData("msedge", "Wikipedia", "This page", "screenshot,phone,clipboard")]
    [InlineData("Code", "Program.cs - Halo", "Coding", "timer,notes,clipboard")]
    [InlineData("Spotify", "Spotify Premium", "Spotify", "music,mixer")]
    [InlineData("POWERPNT", "Deck.pptx", "Slides", "prompter,record,timer")]
    public void The_app_in_front_suggests_actions(string process, string title, string label, string ids)
    {
        var s = WheelContext.For(process, title)!;
        Assert.Equal(label, s.Label);
        Assert.Equal(ids.Split(','), s.ActionIds);
    }

    [Theory]
    [InlineData("explorer", "Downloads")]
    [InlineData("notepad", "notes.txt")]
    [InlineData("", "")]
    public void Other_apps_suggest_nothing(string process, string title) => Assert.Null(WheelContext.For(process, title));

    [Fact]
    public void Only_actions_not_already_on_the_dock()
    {
        var extra = WheelContext.Extra(WheelContext.For("chrome", "News"), WheelSlots.Defaults);
        Assert.Equal(new[] { "clipboard" }, extra); // screenshot and phone are already there
        Assert.Empty(WheelContext.Extra(null, WheelSlots.Defaults));
    }
}

public class SpokenChainTests
{
    [Theory]
    [InlineData("screenshot le lo aur phone pe bhej", "screenshot le lo|phone pe bhej")]
    [InlineData("take a screenshot and send it to my phone", "take a screenshot|send it to my phone")]
    [InlineData("start a 25 minute timer then pause the music", "start a 25 minute timer|pause the music")]
    [InlineData("volume up, and then brightness down.", "volume up|brightness down")]
    [InlineData("timer 5 minutes aur phir music chalao", "timer 5 minutes|music chalao")]
    public void Splits_at_joining_words(string said, string parts) => Assert.Equal(parts.Split('|'), Search.SpokenChain.Split(said));

    [Theory]
    [InlineData("take a screenshot")]
    [InlineData("")]
    [InlineData("a and b and c and d and e")] // more than four is not a chain
    public void One_request_is_not_split(string said) => Assert.Empty(Search.SpokenChain.Split(said));

    [Theory]
    [InlineData("send it to my phone", "send this to my phone")]
    [InlineData("phone pe bhej", "send this to my phone")]
    [InlineData("isko mobile pe bhej do", "send this to my phone")]
    public void A_later_step_that_says_it_means_this(string said, string english) =>
        Assert.Equal(english, Search.SpokenRewrite.Rewrite(Search.SpokenChain.Carry(said)) ?? Search.SpokenChain.Carry(said));
}

public class HealthTextTests
{
    static readonly Shammy.Core.Quick.DriveSpace Roomy = new("C:", 120L << 30, 476L << 30), Full = new("C:", 11L << 30, 476L << 30);

    [Fact]
    public void One_line_most_useful_first()
    {
        var r = new Shammy.Core.Quick.HealthReport(Full, TimeSpan.FromDays(9), 7, new Shammy.Core.Quick.BatteryWear(50000, 43000), false);
        Assert.Equal("Health · 11 GB free · restarted 9 days ago · battery 86% · 7 apps at startup", Shammy.Core.Quick.HealthText.Line(r));
        Assert.True(Shammy.Core.Quick.HealthText.NeedsAttention(r)); // 11 GB free is low
    }

    [Fact]
    public void Updates_waiting_beat_the_uptime()
    {
        var r = new Shammy.Core.Quick.HealthReport(Roomy, TimeSpan.FromDays(9), 3, null, true);
        Assert.Equal("Health · 120 GB free · restart to finish updates · 3 apps at startup", Shammy.Core.Quick.HealthText.Line(r));
        Assert.True(Shammy.Core.Quick.HealthText.NeedsAttention(r));
    }

    [Fact]
    public void A_healthy_laptop_needs_no_attention() =>
        Assert.False(Shammy.Core.Quick.HealthText.NeedsAttention(new(Roomy, TimeSpan.FromDays(2), 5, new(50000, 47000), false)));

    [Theory]
    [InlineData(0, "Restarted today")]
    [InlineData(1, "Restarted 1 day ago")]
    [InlineData(12, "Restarted 12 days ago")]
    public void Uptime_in_words(int days, string said) => Assert.Equal(said, Shammy.Core.Quick.HealthText.Uptime(TimeSpan.FromDays(days) + TimeSpan.FromHours(3)));

    [Fact]
    public void Battery_report_parsing()
    {
        var xml = "<BatteryReport><Batteries><Battery><DesignCapacity>56000</DesignCapacity><FullChargeCapacity>48160</FullChargeCapacity></Battery></Batteries></BatteryReport>";
        Assert.Equal(86, Shammy.Core.Quick.HealthText.ParseBatteryReport(xml)!.Percent);
        Assert.Null(Shammy.Core.Quick.HealthText.ParseBatteryReport("<BatteryReport><Batteries /></BatteryReport>"));
        Assert.Null(Shammy.Core.Quick.HealthText.ParseBatteryReport(""));
    }

    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1536L, "2 KB")]
    [InlineData(5L << 20, "5 MB")]
    [InlineData((3L << 30) / 2, "1.5 GB")]
    [InlineData(476L << 30, "476 GB")]
    public void Sizes_in_words(long bytes, string said) => Assert.Equal(said, Shammy.Core.Quick.StorageText.Size(bytes));

    [Theory]
    [InlineData("power mode performance", Shammy.Core.Quick.PowerModeKind.Performance)]
    [InlineData("battery saver mode", Shammy.Core.Quick.PowerModeKind.Efficiency)]
    [InlineData("balanced mode", Shammy.Core.Quick.PowerModeKind.Balanced)]
    public void Power_mode_words(string said, Shammy.Core.Quick.PowerModeKind kind) => Assert.Equal(kind, Shammy.Core.Quick.PowerMode.Parse(said));
}

public class OtpFinderTests
{
    [Theory]
    [InlineData("VM-HDFCBK", "482913 is your OTP for login. Do not share it with anyone.", "482913")]
    [InlineData("Swiggy", "Your verification code is 4821", "4821")]
    [InlineData("Google", "G-123456 is your Google verification code.", "123456")]
    [InlineData("Microsoft", "Use 739 201 to sign in. Security code", "739201")]
    [InlineData("Amazon", "OTP 55210839 for your order of Rs 1,299. Valid 10 min.", "55210839")]
    [InlineData("Bank", "Rs.500 debited. OTP for txn is 662211, card ending 4321", "662211")]
    [InlineData("Zomato", "आपका OTP 5521 है", "5521")]
    [InlineData("Paytm", "Your login code: 908-776", "908776")]
    public void Finds_the_code(string title, string body, string code) => Assert.Equal(code, Shammy.Core.Notifications.OtpFinder.Find(title, body));

    [Theory]
    [InlineData("Riya", "Meet at 7, table for 4 at 2026 Brigade Road")]
    [InlineData("Bank", "Rs 4500 credited to your account ending 4321")]
    [InlineData("Swiggy", "Your order #482913 is on the way")]
    [InlineData("Mom", "Call me on 9876543210")]
    [InlineData("", "")]
    public void Leaves_ordinary_messages_alone(string title, string body) => Assert.Null(Shammy.Core.Notifications.OtpFinder.Find(title, body));

    [Theory]
    [InlineData("482913", "482 913")]
    [InlineData("55210839", "5521 0839")]
    [InlineData("4821", "4821")]
    public void Spaced_for_reading(string code, string shown) => Assert.Equal(shown, Shammy.Core.Notifications.OtpFinder.Spaced(code));
}
