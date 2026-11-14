using Shammy.Core.Dictation;
using Shammy.Core.OnlineVoice;

namespace Shammy.Core.Tests;

public class DictationCleanTests
{
    [Theory]
    [InlineData("um I I think we should go", "I think we should go.")]
    [InlineData("kal milte hain", "Kal milte hain.")]
    [InlineData("hmm, okay uh see you at five", "Okay see you at five.")]
    [InlineData("Umm the the meeting is moved", "The meeting is moved.")]
    [InlineData("is it ready ?", "Is it ready?")]
    [InlineData("done. now the next one", "Done. Now the next one.")]
    [InlineData("i think i'm late", "I think I'm late.")]
    [InlineData("send it,", "Send it.")]
    [InlineData("  lots   of    space  ", "Lots of space.")]
    public void Light_clean_up(string raw, string typed) => Assert.Equal(typed, DictationText.Clean(raw));

    [Fact]
    public void Gnani_punctuation_stays() => Assert.Equal("Wait! Really? Okay.", DictationText.Clean("Wait! Really? Okay."));

    [Theory]
    [InlineData("to err is human")] // "err" is a word, not a filler
    [InlineData("hum kal chalenge")] // "hum" is Hindi for "we"
    [InlineData("the summer is here")]
    public void Words_that_look_like_fillers_stay(string raw) =>
        Assert.Equal(char.ToUpperInvariant(raw[0]) + raw[1..] + ".", DictationText.Clean(raw));

    [Theory]
    [InlineData("kabhi kabhi aisa hota hai", "Kabhi kabhi aisa hota hai.")]
    [InlineData("no, no, no", "No, no, no.")]
    public void Words_meant_twice_stay(string raw, string typed) => Assert.Equal(typed, DictationText.Clean(raw));

    [Fact]
    public void Nothing_but_fillers_types_nothing() => Assert.Equal("", DictationText.Clean("um uh hmm"));

    [Fact]
    public void A_devanagari_end_mark_counts() => Assert.Equal("कल मिलते हैं।", DictationText.Clean("कल मिलते हैं।"));

    [Fact]
    public void Off_only_tidies_the_spacing() => Assert.Equal("um i i think", DictationText.Finish("  um i  i think ", hindi: false, light: false));

    [Theory]
    [InlineData("", 0)]
    [InlineData("Kal milte hain.", 3)]
    public void Counts_words(string text, int words) => Assert.Equal(words, DictationText.Words(text));
}

public class DictationHindiTests
{
    [Theory]
    [InlineData("in Hindi kal milte hain")]
    [InlineData("kal milte hain, in hindi")]
    [InlineData("Hindi mein likho kal milte hain")]
    [InlineData("hindi me kal milte hain")]
    public void In_hindi_asks_for_devanagari(string heard) => Assert.True(DictationText.AsksForHindi(heard));

    [Theory]
    [InlineData("kal milte hain")]
    [InlineData("send the hindustan times link")]
    [InlineData("")]
    public void Other_words_stay_roman(string heard) => Assert.False(DictationText.AsksForHindi(heard));

    [Theory]
    [InlineData("in Hindi kal milte hain", "kal milte hain")]
    [InlineData("kal milte hain, in hindi", "kal milte hain")]
    [InlineData("Hindi mein kal milte hain", "kal milte hain")]
    [InlineData("इन हिंदी कल मिलते हैं", "कल मिलते हैं")]
    [InlineData("कल मिलते हैं हिंदी में", "कल मिलते हैं")]
    [InlineData("in हिंदी कल मिलते हैं", "कल मिलते हैं")]
    public void The_trigger_words_are_taken_out(string heard, string left) => Assert.Equal(left, DictationText.RemoveHindiTrigger(heard));

    [Fact]
    public void Devanagari_turns_roman_unless_hindi_was_asked()
    {
        Assert.Equal("Kal chalo.", DictationText.Finish("कल चलो", hindi: false, light: true));
        Assert.Equal("कल मिलते हैं.", DictationText.Finish("इन हिंदी कल मिलते हैं", hindi: true, light: true));
    }

    [Fact]
    public void Live_words_are_roman_without_the_trigger() => Assert.Equal("kal chalo", DictationText.Live("in hindi कल चलो"));

    [Fact]
    public void Devanagari_is_asked_for_with_a_language()
    {
        var r = GnaniApi.Stt(new SttOptions(Format: GnaniApi.SttDevanagariFormat, LanguageCode: GnaniApi.SttHindiLanguages), "k", 100);
        Assert.Contains(r.FormFields, f => f.Key == "format" && f.Value == "codemix");
        Assert.Contains(r.FormFields, f => f.Key == "language_code" && f.Value == "hi-IN,en-IN");
    }
}

public class DictationKeyTests
{
    [Fact]
    public void Ctrl_then_win_starts_and_letting_go_of_either_stops()
    {
        var g = new CtrlWinGesture();
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftCtrl, true));
        Assert.Equal(GestureStep.Start, g.Key(GestureKey.LeftWin, true));
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftWin, true)); // auto-repeat
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftCtrl, true));
        Assert.Equal(GestureStep.Stop, g.Key(GestureKey.LeftWin, false));
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftCtrl, false));
    }

    [Fact]
    public void Win_then_either_ctrl_starts_too()
    {
        var g = new CtrlWinGesture();
        g.Key(GestureKey.RightWin, true);
        Assert.Equal(GestureStep.Start, g.Key(GestureKey.RightCtrl, true));
        Assert.Equal(GestureStep.Stop, g.Key(GestureKey.RightCtrl, false));
    }

    [Fact]
    public void Another_key_cancels_and_the_shortcut_passes()
    {
        var g = new CtrlWinGesture();
        g.Key(GestureKey.LeftCtrl, true);
        g.Key(GestureKey.LeftWin, true);
        Assert.Equal(GestureStep.Cancel, g.Key(GestureKey.Other, true)); // Ctrl+Win+Right: next desktop
        Assert.Equal(GestureStep.None, g.Key(GestureKey.Other, false));
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftWin, false));
        Assert.False(g.Active);
    }

    [Fact]
    public void A_key_pressed_before_both_are_down_means_a_shortcut()
    {
        var g = new CtrlWinGesture();
        g.Key(GestureKey.LeftCtrl, true);
        g.Key(GestureKey.Other, true); // Ctrl+C
        g.Key(GestureKey.Other, false);
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftWin, true));
        g.Key(GestureKey.LeftWin, false);
        g.Key(GestureKey.LeftCtrl, false);
        // Both let go: a fresh press works again.
        g.Key(GestureKey.LeftCtrl, true);
        Assert.Equal(GestureStep.Start, g.Key(GestureKey.LeftWin, true));
    }

    [Fact]
    public void Pressing_the_let_go_key_again_is_not_a_new_dictation()
    {
        var g = new CtrlWinGesture();
        g.Key(GestureKey.LeftCtrl, true);
        g.Key(GestureKey.LeftWin, true);
        Assert.Equal(GestureStep.Stop, g.Key(GestureKey.LeftCtrl, false));
        Assert.Equal(GestureStep.None, g.Key(GestureKey.LeftCtrl, true));
    }

    [Theory]
    [InlineData("ctrlwin", DictationKey.CtrlWin)]
    [InlineData("RightAlt", DictationKey.RightAlt)]
    [InlineData("double", DictationKey.DoubleTap)]
    [InlineData("off", DictationKey.Off)]
    public void Key_names_parse(string name, DictationKey key)
    {
        Assert.Equal(key, DictationKeys.Parse(name));
        Assert.Equal(name.ToLowerInvariant(), DictationKeys.Name(key));
    }

    [Fact]
    public void Unknown_key_names_are_refused() => Assert.Null(DictationKeys.Parse("capslock"));
}

public class LiveWordsPacerTests
{
    [Fact]
    public void Asks_at_most_every_one_and_a_half_seconds_one_at_a_time_and_only_after_speech()
    {
        var p = new LiveWordsPacer(0);
        Assert.False(p.Due(1000, spokeSince: true));
        Assert.False(p.Due(1600, spokeSince: false));
        Assert.True(p.Due(1600, spokeSince: true));
        p.Sent(1600);
        Assert.False(p.Due(4000, spokeSince: true)); // still waiting for the answer
        p.Answered();
        Assert.True(p.Due(4000, spokeSince: true));
        p.Sent(4000);
        p.Answered();
        Assert.False(p.Due(5000, spokeSince: true));
    }
}
