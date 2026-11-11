using Shammy.Core.Ambient;
using Shammy.Core.Guide;
using Shammy.Core.Music;
using Shammy.Core.Quick;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class SpokenRewriteTests
{
    [Theory]
    // Screenshot
    [InlineData("screenshot le lo", "screenshot")]
    [InlineData("ek screenshot le lo", "screenshot")]
    [InlineData("screen ka photo le lo", "screenshot")]
    [InlineData("grab my screen", "screenshot")]
    [InlineData("capture the whole screen", "screenshot")]
    [InlineData("take a picture of my screen", "screenshot")]
    // Recording and captions
    [InlineData("recording shuru karo", "start recording")]
    [InlineData("recording band karo", "stop recording")]
    [InlineData("screen record karo", "record my screen")]
    [InlineData("captions chalu karo", "turn on captions")]
    [InlineData("captions band kar do", "turn off captions")]
    // Music
    [InlineData("music chalao", "play music")]
    [InlineData("gaana bajao", "play music")]
    [InlineData("koi gaana laga do", "play music")]
    [InlineData("gaana band karo", "pause music")]
    [InlineData("agla gaana", "next song")]
    [InlineData("pichla gaana", "previous song")]
    [InlineData("kesariya chalao", "play kesariya")]
    [InlineData("arijit singh ke gaane bajao", "play arijit singh")]
    [InlineData("ye kaunsa gaana hai", "what song is this")]
    // Weather
    [InlineData("mausam kaisa hai", "weather")]
    [InlineData("kal ka mausam", "weather tomorrow")]
    [InlineData("aaj baarish hogi kya", "will it rain today")]
    [InlineData("bahar kitni garmi hai", "weather")]
    // Keep awake, the pet, cricket
    [InlineData("don't let my laptop sleep", "keep my pc awake")]
    [InlineData("laptop ko sone mat do", "keep my pc awake")]
    [InlineData("pet band karo", "pet off")]
    [InlineData("hide the cat", "pet off")]
    [InlineData("show the cat", "pet on")]
    [InlineData("india ka score kya hai", "cricket score")]
    [InlineData("what's the score", "cricket score")]
    // Shammy itself
    [InlineData("shammy ki settings kholo", "shammy settings")]
    [InlineData("open your settings", "shammy settings")]
    [InlineData("show me all features", "what can you do")]
    [InlineData("tum kya kya kar sakte ho", "what can you do")]
    [InlineData("whats new", "what's new")]
    [InlineData("kya naya hai", "what's new")]
    // Questions answered on the PC
    [InlineData("battery kitni hai", "battery")]
    [InlineData("how much charge is left", "battery")]
    [InlineData("kitne baje hain", "what time is it")]
    [InlineData("aaj ki date kya hai", "what's the date")]
    [InlineData("koi naya message aaya kya", "any new notifications")]
    [InlineData("last message padho", "read my last message")]
    [InlineData("aaj meetings hai kya", "what's on my calendar")]
    [InlineData("do I have any meetings today", "what's on my calendar")]
    // Notes and opening apps
    [InlineData("note likh lo doodh lana hai", "note doodh lana hai")]
    [InlineData("doodh lana hai note kar lo", "note doodh lana hai")]
    [InlineData("connect my earbuds", "bluetooth")]
    [InlineData("pair my headphones", "bluetooth")]
    [InlineData("whatsapp kholo", "open whatsapp")]
    [InlineData("settings kholo", "open settings")]
    // Wake and polite words go first
    [InlineData("hey shammy, please take a screenshot of my screen", "screenshot")]
    [InlineData("zara screenshot le lo yaar", "screenshot")]
    public void Says_it_the_way_the_commands_know(string said, string english) =>
        Assert.Equal(english, SpokenRewrite.Rewrite(said));

    [Theory]
    [InlineData("how tall is the eiffel tower")]
    [InlineData("best budget mechanical keyboard")]
    [InlineData("capital of australia")]
    [InlineData("5 minute ka timer laga do")] // left to the Hinglish timer parser
    [InlineData("kal subah 7 baje ka alarm laga do")]
    [InlineData("")]
    public void Leaves_real_questions_and_other_commands_alone(string said) => Assert.Null(SpokenRewrite.Rewrite(said));

    [Fact]
    public void A_rewrite_is_tried_before_the_words_as_said()
    {
        var c = SpokenRewrite.Candidates("note likh lo doodh lana hai");
        Assert.Equal("note doodh lana hai", c[0]);
        Assert.Contains("note likh lo doodh lana hai", c);
    }

    [Theory]
    [InlineData("Take a screenshot. Take a screenshot.", "take a screenshot")]
    [InlineData("play sleepwalk play sleepwalk", "play sleepwalk")]
    [InlineData("What is the weather? What is the weather?", "what is the weather")]
    public void A_doubled_sentence_counts_once(string heard, string once)
    {
        var c = SpokenRewrite.Candidates(heard);
        Assert.Equal(once, c[0]);
        Assert.DoesNotContain(c, x => x.Length > once.Length + 2 && x.StartsWith(once, StringComparison.OrdinalIgnoreCase) && x.EndsWith(once.Split(' ')[^1], StringComparison.OrdinalIgnoreCase) && x.Split(' ').Length == once.Split(' ').Length * 2);
    }

    [Fact]
    public void Two_different_sentences_try_the_last_one_too()
    {
        var c = SpokenRewrite.Candidates("Screen shot. Take a screenshot.");
        Assert.Contains("take a screenshot", c);
    }

    [Fact]
    public void Devanagari_is_spelt_out() => Assert.Equal("screenshot", SpokenRewrite.Rewrite("स्क्रीनशॉट ले लो"));

    // ---- The rewrites land on what the real parsers accept.

    [Theory]
    [InlineData("music chalao")]
    [InlineData("gaana band karo")]
    [InlineData("agla gaana")]
    [InlineData("pichla gaana")]
    [InlineData("kesariya chalao")]
    public void Music_understands_the_rewrite(string said) => Assert.NotNull(MusicCommand.Parse(SpokenRewrite.Rewrite(said)));

    [Theory]
    [InlineData("don't let my laptop sleep")]
    [InlineData("laptop ko sone mat do")]
    [InlineData("keep the screen on")]
    public void Quick_actions_understand_the_rewrite(string said) => Assert.NotNull(QuickPhrases.Parse(SpokenRewrite.Rewrite(said)!));

    [Theory]
    [InlineData("mausam kaisa hai")]
    [InlineData("kal ka mausam")]
    [InlineData("aaj baarish hogi kya")]
    public void Weather_understands_the_rewrite(string said) => Assert.NotNull(AmbientCommands.ParseWeather(SpokenRewrite.Rewrite(said)!));

    [Theory]
    [InlineData("pet band karo", false)]
    [InlineData("show the cat", true)]
    public void The_pet_understands_the_rewrite(string said, bool on) => Assert.Equal(on, AmbientCommands.ParsePet(SpokenRewrite.Rewrite(said)!));

    [Theory]
    [InlineData("show me all features")]
    [InlineData("tum kya kya kar sakte ho")]
    public void The_guide_understands_the_rewrite(string said) => Assert.NotNull(GuidePhrases.Parse(SpokenRewrite.Rewrite(said)));
}

public class LocalQuestionTests
{
    [Theory]
    [InlineData("battery", LocalQuestionKind.Battery)]
    [InlineData("battery kitni hai", LocalQuestionKind.Battery)]
    [InlineData("what time is it", LocalQuestionKind.Time)]
    [InlineData("time kya hua", LocalQuestionKind.Time)]
    [InlineData("what day is it", LocalQuestionKind.Date)]
    [InlineData("any new notifications", LocalQuestionKind.Notifications)]
    [InlineData("read my last message", LocalQuestionKind.LastMessage)]
    [InlineData("shammy settings", LocalQuestionKind.ShammySettings)]
    [InlineData("shammy ki settings kholo", LocalQuestionKind.ShammySettings)]
    [InlineData("what's new", LocalQuestionKind.WhatsNew)]
    [InlineData("what's playing", LocalQuestionKind.NowPlaying)]
    public void Knows_the_question(string said, LocalQuestionKind kind) => Assert.Equal(kind, LocalQuestion.Parse(said));

    [Theory]
    [InlineData("settings")] // that is Windows Settings
    [InlineData("what time does the match start")]
    [InlineData("battery life of iphone 16")]
    public void Leaves_other_things_alone(string said) => Assert.Null(LocalQuestion.Parse(said));

    [Theory]
    [InlineData(78, true, null, "78% · charging")]
    [InlineData(100, true, null, "100% · full, plugged in")]
    [InlineData(45, false, 130, "45% · 2 h 10 min left")]
    [InlineData(12, false, 25, "12% · 25 min left")]
    [InlineData(60, false, null, "60% · on battery")]
    [InlineData(255, false, null, "No battery on this PC")]
    public void Battery_in_words(int percent, bool plugged, int? minutes, string said) =>
        Assert.Equal(said, LocalQuestion.Battery(percent, plugged, minutes is { } m ? TimeSpan.FromMinutes(m) : null));
}
