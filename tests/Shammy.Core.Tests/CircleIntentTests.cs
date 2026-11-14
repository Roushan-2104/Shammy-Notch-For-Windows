using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class CircleIntentTests
{
    static CircleIntent Parse(string text)
    {
        var i = CircleIntent.Parse(text);
        Assert.True(i is not null, $"\"{text}\" gave nothing");
        return i!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void Nothing_said_is_null(string? text) => Assert.Null(CircleIntent.Parse(text));

    // ---- Translate -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("in english", "en")]
    [InlineData("In English please", "en")]
    [InlineData("in hindi", "hi")]
    [InlineData("hindi mein", "hi")]
    [InlineData("Hindi me batao", "hi")]
    [InlineData("english mein", "en")]
    [InlineData("translate to french", "fr")]
    [InlineData("translate this into Japanese", "ja")]
    [InlineData("tamil mein translate karo", "ta")]
    [InlineData("hindi", "hi")]
    [InlineData("English", "en")]
    [InlineData("what is this in english", "en")]
    [InlineData("isko hindi mein bolo", "hi")]
    [InlineData("हिंदी में", "hi")]
    public void Translates_into_the_language_asked(string text, string language)
    {
        var i = Parse(text);
        Assert.Equal(CircleAction.Translate, i.Action);
        Assert.Equal(language, i.TargetLanguage);
        Assert.False(i.AutoLanguage);
    }

    [Theory]
    [InlineData("translate")]
    [InlineData("Translate karo")]
    [InlineData("translate this")]
    [InlineData("ise translate kar do")]
    public void Translate_without_a_language_leaves_the_choice_to_the_caller(string text)
    {
        var i = Parse(text);
        Assert.Equal(CircleAction.Translate, i.Action);
        Assert.Null(i.TargetLanguage);
        Assert.True(i.AutoLanguage);
        Assert.False(i.WantsMeaning);
    }

    [Theory]
    [InlineData("meaning")]
    [InlineData("matlab")]
    [InlineData("iska matlab kya hai")]
    [InlineData("matlab kya hai")]
    [InlineData("what does this mean")]
    [InlineData("meaning of this word")]
    public void Meaning_is_a_translation_with_the_language_picked_for_them(string text)
    {
        var i = Parse(text);
        Assert.Equal(CircleAction.Translate, i.Action);
        Assert.True(i.AutoLanguage);
        Assert.True(i.WantsMeaning);
        Assert.Equal("en", i.TargetFor("ja"));
        Assert.Equal("en", i.TargetFor(null));
        Assert.Equal("hi", i.TargetFor("en"));
    }

    [Fact]
    public void Meaning_in_a_named_language_keeps_it()
    {
        var i = Parse("meaning in hindi");
        Assert.Equal(CircleAction.Translate, i.Action);
        Assert.Equal("hi", i.TargetLanguage);
        Assert.True(i.WantsMeaning);
        Assert.False(i.AutoLanguage);
        Assert.Equal("hi", i.TargetFor("en"));
    }

    // ---- Search ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("search")]
    [InlineData("Search this")]
    [InlineData("google")]
    [InlineData("google it")]
    [InlineData("Google it please")]
    [InlineData("search karo")]
    [InlineData("isko search kar do")]
    [InlineData("dhundo")]
    [InlineData("ye dhundho")]
    [InlineData("look it up")]
    [InlineData("please search for reviews")]
    public void Searches(string text) => Assert.Equal(CircleAction.Search, Parse(text).Action);

    // ---- Convert ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("what time")]
    [InlineData("what time is this for me")]
    [InlineData("my time")]
    [InlineData("in my time")]
    [InlineData("convert")]
    [InlineData("convert it")]
    [InlineData("kitne baje")]
    [InlineData("mere time pe kitne baje")]
    [InlineData("in rupees")]
    [InlineData("how much is this in rupees")]
    [InlineData("in dollars")]
    [InlineData("kitne rupaye")]
    public void Converts(string text) => Assert.Equal(CircleAction.Convert, Parse(text).Action);

    // ---- Ask the AI ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("explain")]
    [InlineData("Explain this")]
    [InlineData("what is this")]
    [InlineData("what's this")]
    [InlineData("kya hai")]
    [InlineData("ye kya hai")]
    [InlineData("samjhao")]
    [InlineData("samjha do")]
    [InlineData("fix")]
    [InlineData("fix this error")]
    [InlineData("why")]
    [InlineData("why is this failing")]
    [InlineData("how do i open this file")]
    [InlineData("summarise")]
    [InlineData("summarize this")]
    [InlineData("solve")]
    [InlineData("solve this for x")]
    public void Asks_the_ai(string text)
    {
        var i = Parse(text);
        Assert.Equal(CircleAction.AskAi, i.Action);
        Assert.Equal(text, i.Question);
    }

    [Theory]
    [InlineData("is this vegan?")]
    [InlineData("how much does it cost")]
    [InlineData("is this open on Sunday?")]
    [InlineData("Who made this painting?")]
    [InlineData("can I eat this during a fast")]
    [InlineData("ye sasta hai ya mehenga")]
    [InlineData("best time to visit")]
    public void Anything_else_is_a_question_for_the_ai(string text)
    {
        var i = Parse(text);
        Assert.Equal(CircleAction.AskAi, i.Action);
        Assert.Equal(text, i.Question);
        Assert.Null(i.TargetLanguage);
    }

    [Fact]
    public void Explaining_in_a_language_is_the_ai_answering_in_it()
    {
        var i = Parse("explain in hindi");
        Assert.Equal(CircleAction.AskAi, i.Action);
        Assert.Equal("hi", i.TargetLanguage);
        var j = Parse("hindi mein samjhao");
        Assert.Equal(CircleAction.AskAi, j.Action);
        Assert.Equal("hi", j.TargetLanguage);
    }

    [Fact]
    public void The_question_is_tidied_but_kept_in_their_words()
    {
        var i = Parse("  is this   vegan?  ");
        Assert.Equal("is this vegan?", i.Question);
    }

    // ---- Copy, Track, Call, WhatsApp, Remind, Open --------------------------------------------------------------------

    [Theory]
    [InlineData("copy", CircleAction.Copy)]
    [InlineData("copy text", CircleAction.Copy)]
    [InlineData("Copy karo", CircleAction.Copy)]
    [InlineData("isko copy kar lo", CircleAction.Copy)]
    [InlineData("can you copy this?", CircleAction.Copy)]
    [InlineData("track", CircleAction.Track)]
    [InlineData("track this parcel", CircleAction.Track)]
    [InlineData("track karo", CircleAction.Track)]
    [InlineData("call", CircleAction.Call)]
    [InlineData("call them", CircleAction.Call)]
    [InlineData("call karo", CircleAction.Call)]
    [InlineData("isko phone lagao", CircleAction.Call)]
    [InlineData("whatsapp", CircleAction.WhatsApp)]
    [InlineData("WhatsApp this number", CircleAction.WhatsApp)]
    [InlineData("whatsapp karo", CircleAction.WhatsApp)]
    [InlineData("remind me", CircleAction.Remind)]
    [InlineData("remind me about this tomorrow", CircleAction.Remind)]
    [InlineData("yaad dilana", CircleAction.Remind)]
    [InlineData("kal yaad dila dena", CircleAction.Remind)]
    [InlineData("open", CircleAction.Open)]
    [InlineData("open it", CircleAction.Open)]
    [InlineData("open karo", CircleAction.Open)]
    [InlineData("ise kholo", CircleAction.Open)]
    [InlineData("please open this link", CircleAction.Open)]
    [InlineData("hey Shammy, track this", CircleAction.Track)]
    public void Reads_commands(string text, CircleAction action) => Assert.Equal(action, Parse(text).Action);

    [Theory]
    [InlineData("is this open on Sunday?")]
    [InlineData("how do i call customer care")]
    [InlineData("does it copy files too")]
    public void A_question_that_mentions_a_command_is_still_a_question(string text) => Assert.Equal(CircleAction.AskAi, Parse(text).Action);

    [Fact]
    public void Odd_input_never_throws()
    {
        foreach (var s in new[] { "?", "!!!", "((((", "🙂", new string('a', 10000), "\u0000", "in in in", "matlab?" })
            Assert.NotNull(CircleIntent.Parse(s));
    }
}
