using System.Text.Json.Nodes;
using Shammy.Core.PhoneLink;
using Xunit;

namespace Shammy.Core.Tests;

/// <summary>Reach (24 Nov): the phone controls the laptop; only listed keys, capped text and limited rates get through.</summary>
public class ReachTests
{
    static JsonObject M(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void KeysComeOnlyFromTheList()
    {
        var k = Assert.IsType<ReachKey>(Reach.Parse(M("""{"t":"key","k":"T","mods":["Ctrl","Shift"]}""")));
        Assert.Equal('T', k.Vk);
        Assert.Equal(new ushort[] { 0x11, 0x10 }, k.Mods);
        Assert.Equal(new ReachKey(0x1B, []).Vk, Assert.IsType<ReachKey>(Reach.Parse(M("""{"t":"key","k":"Esc"}"""))).Vk);
        Assert.Empty(Assert.IsType<ReachKey>(Reach.Parse(M("""{"t":"key","k":"Esc"}"""))).Mods);
        Assert.Equal((ushort)0x7B, Assert.IsType<ReachKey>(Reach.Parse(M("""{"t":"key","k":"F12"}"""))).Vk);
        // repeated modifiers count once
        Assert.Single(Assert.IsType<ReachKey>(Reach.Parse(M("""{"t":"key","k":"A","mods":["Ctrl","Ctrl"]}"""))).Mods);

        Assert.Null(Reach.Parse(M("""{"t":"key","k":"Sleep"}""")));          // not on the list
        Assert.Null(Reach.Parse(M("""{"t":"key","k":"t"}""")));              // names are exact
        Assert.Null(Reach.Parse(M("""{"t":"key","k":"A","mods":["Hyper"]}""")));
        Assert.Null(Reach.Parse(M("""{"t":"key","k":"A","mods":[3]}""")));
        Assert.Null(Reach.Parse(M("""{"t":"key","k":"A","mods":"Ctrl"}""")));    // not a list: the whole message goes
        Assert.Null(Reach.Parse(M("""{"t":"key"}""")));
        Assert.Null(Reach.Parse(M("""{"t":"run","cmd":"calc"}""")));         // nothing else is input
    }

    [Fact]
    public void ArrowsAndWinAreExtendedKeys()
    {
        foreach (var name in new[] { "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown", "Delete", "Insert" })
            Assert.Contains(Reach.Keys[name], Reach.Extended);
        Assert.Contains(Reach.Modifiers["Win"], Reach.Extended);
        Assert.DoesNotContain(Reach.Keys["A"], Reach.Extended);
    }

    [Fact]
    public void TextIsCleanedAndCapped()
    {
        Assert.Equal("a\nb\nc", Assert.IsType<ReachText>(Reach.Parse(M("""{"t":"type","text":"a\r\nb\rc"}"""))).Text);
        Assert.Equal("नमस्ते", Assert.IsType<ReachText>(Reach.Parse(M("""{"t":"type","text":"नमस्ते"}"""))).Text);
        Assert.Equal("ab\tc", Reach.Clean("a\u0007b\tc\u001B"));
        Assert.Null(Reach.Parse(M("""{"t":"type","text":""}""")));
        Assert.Null(Reach.Parse(M("""{"t":"type","text":"\u0003"}""")));
        Assert.Equal(Reach.MaxText, Reach.Clean(new string('x', 9000)).Length);
        // a cut never leaves half an emoji
        var emoji = new string('x', Reach.MaxText - 1) + "😀";
        Assert.False(char.IsHighSurrogate(Reach.Clean(emoji)[^1]));
    }

    [Fact]
    public void PointerAndScrollAreClamped()
    {
        Assert.Equal(new ReachMove(12, -3), Reach.Parse(M("""{"t":"mv","dx":12,"dy":-3}""")));
        Assert.Equal(new ReachMove(Reach.MaxMove, -Reach.MaxMove), Reach.Parse(M("""{"t":"mv","dx":99999,"dy":-99999}""")));
        Assert.Null(Reach.Parse(M("""{"t":"mv","dx":0,"dy":0}""")));
        Assert.Null(Reach.Parse(M("""{"t":"mv","dx":"x"}""")));
        // fractional lines for smooth scrolling: 0.25 of a line is 30 wheel units
        Assert.Equal(new ReachWheel(30, 0), Reach.Parse(M("""{"t":"wh","dy":0.25}""")));
        Assert.Equal(new ReachWheel(-120, 240), Reach.Parse(M("""{"t":"wh","dy":-1,"dx":2}""")));
        Assert.Equal(new ReachWheel(Reach.MaxWheel, 0), Reach.Parse(M("""{"t":"wh","dy":1e9}""")));
    }

    [Fact]
    public void ButtonsParse()
    {
        Assert.Equal(new ReachButton(ReachMouse.Right, ReachPress.Click), Reach.Parse(M("""{"t":"btn","b":"right","act":"click"}""")));
        Assert.Null(Reach.Parse(M("""{"t":"btn","b":"x1","act":"click"}""")));
        Assert.Null(Reach.Parse(M("""{"t":"btn","b":"left","act":"hold"}""")));
    }

    static readonly DateTime T0 = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NothingGetsInUntilReachIsOnAndHeldButtonsAreLetGo()
    {
        var gate = new ReachGate();
        var sent = new List<ReachInput>();
        var phone = new object();
        Assert.False(gate.Admit(M("""{"t":"key","k":"A"}"""), T0, sent.Add));             // off: ignored
        Assert.True(gate.Start(phone));
        Assert.False(gate.Start(phone));                                                     // already on
        Assert.True(gate.Admit(M("""{"t":"btn","b":"left","act":"down"}"""), T0, sent.Add));
        Assert.False(gate.Admit(M("""{"t":"btn","b":"right","act":"up"}"""), T0, sent.Add)); // never pressed: nothing to let go

        // another connection's end doesn't end this one (a reconnect took over)
        Assert.False(gate.End(new object(), _ => { }));
        IReadOnlyList<ReachMouse>? letGo = null;
        Assert.True(gate.End(phone, held => letGo = held));
        Assert.Equal(new[] { ReachMouse.Left }, letGo);
        Assert.False(gate.On);
        Assert.False(gate.Admit(M("""{"t":"key","k":"A"}"""), T0, sent.Add));             // off again
        Assert.False(gate.End(null, _ => { }));
        Assert.Single(sent);
    }

    [Fact]
    public void EachKindHasItsOwnRateAndTextCostsByLength()
    {
        var gate = new ReachGate();
        gate.Start(new object());
        bool Go(string json, DateTime at) => gate.Admit(M(json), at, _ => { });
        Assert.Equal(20, Enumerable.Range(0, 100).Count(_ => Go("""{"t":"key","k":"A"}""", T0)));   // the burst
        Assert.True(Go("""{"t":"mv","dx":1,"dy":1}""", T0));                                        // its own bucket
        Assert.True(Go("""{"t":"key","k":"A"}""", T0.AddMilliseconds(20)));                         // ~17 ms refills a key
        Assert.False(Go("""{"t":"key","k":"A"}""", T0.AddMilliseconds(20)));
        // a long idle never builds more than the burst
        Assert.Equal(20, Enumerable.Range(0, 100).Count(_ => Go("""{"t":"key","k":"A"}""", T0.AddHours(1))));

        // 4096 characters cost 65 of the 10-message text burst: one big paste can't be repeated 30 times a second
        var big = "{\"t\":\"type\",\"text\":\"" + new string('x', 4096) + "\"}";
        Assert.False(Go(big, T0));
        Assert.Equal(10, Enumerable.Range(0, 20).Count(_ => Go("""{"t":"type","text":"hi"}""", T0)));
    }

    [Fact]
    public void PointerLandsOnTheWholeDesktopScale()
    {
        // a second screen left of the main one: the desktop starts at -1920 and is 3840 wide
        Assert.Equal(0, Reach.Absolute(-1920, -1920, 3840));
        Assert.Equal(65535, Reach.Absolute(1919, -1920, 3840));
        Assert.Equal(0, Reach.Absolute(-5000, -1920, 3840));        // clamped at the left edge
        Assert.Equal(65535, Reach.Absolute(9000, -1920, 3840));     // and the right
        Assert.Equal(32776, Reach.Absolute(0, -1920, 3840));        // the main screen's left edge, about half way
        Assert.Equal(0, Reach.Absolute(0, 0, 0));                   // no screen size reported: no crash
    }

    [Fact]
    public void ATapOnTheScreenPictureIsASpotOnThatScreen()
    {
        Assert.Equal(new ReachAt(0.25, 1), Reach.Parse(M("""{"t":"at","x":0.25,"y":1}""")));
        Assert.Null(Reach.Parse(M("""{"t":"at","x":1.5,"y":0.2}""")));   // off the picture
        Assert.Null(Reach.Parse(M("""{"t":"at","x":-0.1,"y":0.2}""")));
        var gate = new ReachGate();
        gate.Start(new object());
        Assert.Equal(8, Enumerable.Range(0, 20).Count(_ => gate.Admit(M("""{"t":"at","x":0.5,"y":0.5}"""), T0, _ => { })));
    }

    [Fact]
    public void LettingGoOfAHeldButtonIsNeverLostToTheRate()
    {
        var gate = new ReachGate();
        gate.Start(new object());
        bool Go(string json) => gate.Admit(M(json), T0, _ => { });
        Assert.True(Go("""{"t":"btn","b":"left","act":"down"}"""));
        while (Go("""{"t":"btn","b":"right","act":"click"}""")) { }            // the button rate is used up
        Assert.False(Go("""{"t":"btn","b":"right","act":"down"}"""));
        Assert.True(Go("""{"t":"btn","b":"left","act":"up"}"""));             // still lets go
        Assert.False(Go("""{"t":"btn","b":"left","act":"up"}"""));            // once
        IReadOnlyList<ReachMouse>? held = null;
        gate.End(null, h => held = h);
        Assert.Empty(held!);
    }

    [Fact]
    public void AReconnectTakesReachOverAndTheOldConnectionCantEndIt()
    {
        var gate = new ReachGate();
        object first = new(), second = new();
        Assert.True(gate.Start(first));
        Assert.False(gate.Start(second));                                       // already on: now the new connection's
        Assert.False(gate.End(first, _ => { }));                               // the old one closing later changes nothing
        Assert.True(gate.On);
        Assert.True(gate.End(second, _ => { }));
        Assert.False(gate.On);
    }

    [Fact]
    public void TheClockGoingBackNeitherMintsInputNorFreezesIt()
    {
        var gate = new ReachGate();
        gate.Start(new object());
        bool Go(DateTime at) => gate.Admit(M("""{"t":"key","k":"A"}"""), at, _ => { });
        Assert.Equal(20, Enumerable.Range(0, 30).Count(_ => Go(T0)));
        var back = T0.AddMinutes(-5);                                           // Windows time sync steps the clock back
        Assert.False(Go(back));                                                 // no refill from going back
        Assert.True(Go(back.AddMilliseconds(20)));                             // and keys work again right away
        Assert.True(Enumerable.Range(0, 30).Count(_ => Go(T0)) <= 20);         // coming forward: never more than a burst
    }

    [Fact]
    public void BrokenMessagesAreNotInput()
    {
        Assert.Null(Reach.Parse(M("""{"t":"at"}""")));                          // no spot is not the top-left corner
        Assert.Null(Reach.Parse(M("""{"t":"at","x":0.5}""")));
        Assert.Null(Reach.Parse(M("""{"t":"at","x":"0.5","y":0.5}""")));
        Assert.Null(Reach.Parse(M("""{"t":"key","k":5}""")));
        Assert.Null(Reach.Parse(M("""{"t":"type","text":5}""")));
        Assert.Null(Reach.Parse(M("""{"t":"mv","dx":"12","dy":"3"}""")));
        Assert.Null(Reach.Parse(M("""{"t":"btn","b":"left"}""")));
        Assert.Null(Reach.Parse(M("""{"t":5}""")));
        Assert.Equal(new ReachMove(13, 0), Reach.Parse(M("""{"t":"mv","dx":12.6}""")));   // fractions round
        Assert.Equal(new ReachMove(Reach.MaxMove, 0), Reach.Parse(M("""{"t":"mv","dx":1e300}""")));
    }

    [Fact]
    public void HalfAnEmojiOnItsOwnIsDropped()
    {
        Assert.Equal("ab", Reach.Clean("a\uD83Db"));                           // a high half with no partner
        Assert.Equal("ab", Reach.Clean("a\uDE00b"));                           // a low half on its own
        Assert.Equal("a", Reach.Clean("a\uD83D"));                              // a high half at the very end
        Assert.Equal("a😀b", Reach.Clean("a😀b"));
        Assert.Equal("😀", Reach.Clean("\uDE00😀\uD83D"));
        Assert.Equal("\uD83D", "\uD83D");
        // the cap falls between the halves: the whole emoji goes, never half
        Assert.Equal(Reach.MaxText - 1, Reach.Clean(new string('x', Reach.MaxText - 1) + "😀").Length);
        Assert.Equal(Reach.MaxText, Reach.Clean(new string('x', Reach.MaxText - 2) + "😀😀").Length);
        Assert.EndsWith("x", Reach.Clean(new string('x', Reach.MaxText - 1) + "😀b"));   // nothing after the cut slips in
    }
}
