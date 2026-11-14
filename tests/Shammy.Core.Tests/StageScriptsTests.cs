using Shammy.Core.Prompter;

namespace Shammy.Core.Tests;

public class StageScriptsTests
{
    const string Script = "Good morning everyone, and thank you for joining.\nToday I want to talk about three things.";

    [Theory]
    [InlineData("Good morning everyone, thanks for joining us today.")]
    [InlineData("One two three four five six seven")]
    [InlineData("Line one of the talk\nline two of the talk")]
    public void Copied_sentences_look_like_a_script(string text) => Assert.True(StageScripts.LooksLikeScript(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello there")]                                   // too short
    [InlineData("Supercalifragilisticexpialidocious")]            // one word
    [InlineData("https://example.com/a/very/long/path?with=query")]
    [InlineData("www.example.com/some/long/page/to/read")]
    [InlineData("someone.long.name@example.com")]
    [InlineData(@"C:\Users\me\Documents\scripts\talk.txt")]
    [InlineData(@"C:\Users\me\My Documents\talk.txt")]
    public void Words_links_and_paths_are_not_offered(string? text) => Assert.False(StageScripts.LooksLikeScript(text));

    [Theory]
    [InlineData("int ans = INT_MAX; for (int i = 0; i < n; i++) { ans = min(ans, a[i]); }")]
    [InlineData("const items = props.items ?? [];\nreturn items.map(x => x.id);")]
    [InlineData("public void Run()\n{\n    Console.WriteLine(\"hi there friend\");\n}")]
    public void Copied_code_is_not_offered_as_a_script(string text)
    {
        Assert.True(StageScripts.LooksLikeCode(text));
        Assert.False(StageScripts.LooksLikeScript(text));
    }

    [Theory]
    [InlineData("So here is the one thing nobody tells you about editing (and it matters).")]
    [InlineData("Good morning everyone! Today: three ideas, one story, and a question for you.")]
    public void Prose_with_a_little_punctuation_is_not_code(string text) => Assert.False(StageScripts.LooksLikeCode(text));

    [Fact]
    public void Two_long_words_are_not_a_script() => Assert.False(StageScripts.LooksLikeScript("Extraordinarily wonderful"));

    [Fact]
    public void The_copy_is_offered_once_and_never_when_it_is_already_on_stage()
    {
        const string copied = "This is the opening of my talk for the review today.";
        Assert.True(StageScripts.ShouldOffer(copied, "", null));
        Assert.True(StageScripts.ShouldOffer(copied, Script, null));
        // Already the script (even with different line endings or spaces at the ends).
        Assert.False(StageScripts.ShouldOffer("  " + copied + "\r\n", copied, null));
        // Said no to this copy.
        Assert.False(StageScripts.ShouldOffer(copied, Script, StageScripts.Key(copied)));
        // A new copy is offered again.
        Assert.True(StageScripts.ShouldOffer(copied + " And more.", Script, StageScripts.Key(copied)));
        Assert.False(StageScripts.ShouldOffer("short", "", null));
    }

    [Fact]
    public void The_key_is_stable_and_ignores_tidying()
    {
        Assert.Equal(StageScripts.Key("one two\r\nthree"), StageScripts.Key("one two\nthree  "));
        Assert.NotEqual(StageScripts.Key("one two three"), StageScripts.Key("one two four"));
        Assert.Equal(16, StageScripts.Key("anything").Length);
    }

    [Theory]
    [InlineData("Hello there, everyone.")]
    [InlineData("one")]
    [InlineData("A script\nwith lines")]
    public void Dragged_words_are_taken(string text) => Assert.True(StageScripts.IsDroppableText(text));

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("https://example.com/page")]
    public void Dragged_links_are_not(string text) => Assert.False(StageScripts.IsDroppableText(text));

    [Fact]
    public void Only_one_text_file_is_taken_from_a_drop()
    {
        Assert.Equal(@"C:\a\talk.txt", StageScripts.ScriptFile(new[] { @"C:\a\talk.txt" }));
        Assert.Equal(@"C:\a\notes.MD", StageScripts.ScriptFile(new[] { @"C:\a\notes.MD" }));
        Assert.Null(StageScripts.ScriptFile(new[] { @"C:\a\photo.png" }));
        Assert.Null(StageScripts.ScriptFile(new[] { @"C:\a\talk.txt", @"C:\a\b.txt" }));
        Assert.Null(StageScripts.ScriptFile(Array.Empty<string>()));
        Assert.Null(StageScripts.ScriptFile(null));
    }

    [Theory]
    [InlineData("Good morning everyone\nMore here", "Good morning everyone")]
    [InlineData("\n\n   # Quarterly review  \nbody", "Quarterly review")]
    [InlineData("   \n  ", "Untitled script")]
    [InlineData("Tabs\tand   spaces  squashed", "Tabs and spaces squashed")]
    public void The_title_is_the_first_line(string script, string title) => Assert.Equal(title, StageScripts.Title(script));

    [Fact]
    public void A_long_first_line_is_cut_at_a_word()
    {
        var title = StageScripts.Title("Good morning everyone and thank you so much for joining this rather long call today");
        Assert.True(title.Length <= StageScripts.MaxTitle, title);
        Assert.EndsWith("…", title);
        Assert.StartsWith("Good morning everyone and thank you", title);
        Assert.DoesNotContain(" …", title);
    }

    [Fact]
    public void Recent_scripts_keep_the_newest_five_once_each()
    {
        var now = new DateTime(2026, 11, 14, 10, 0, 0);
        List<RecentScript> list = new();
        for (int i = 1; i <= 7; i++) list = StageScripts.Remember(list, $"Script number {i}\nwith words", now.AddMinutes(i));
        Assert.Equal(StageScripts.MaxRecent, list.Count);
        Assert.Equal("Script number 7", list[0].Title);
        Assert.Equal("Script number 3", list[^1].Title);
        Assert.Equal(5, list[0].Words);

        // Reading one again moves it to the top, not a second copy.
        list = StageScripts.Remember(list, "Script number 4\r\nwith words  ", now.AddHours(1));
        Assert.Equal(StageScripts.MaxRecent, list.Count);
        Assert.Equal("Script number 4", list[0].Title);
        Assert.Equal(now.AddHours(1), list[0].SavedAt);
        Assert.Single(list, r => r.Title == "Script number 4");
    }

    [Fact]
    public void Remembering_nothing_changes_nothing()
    {
        var list = StageScripts.Remember(null, "  ", DateTime.Now);
        Assert.Empty(list);
    }

    [Fact]
    public void A_broken_store_is_tidied()
    {
        var list = StageScripts.Tidy(new[]
        {
            new RecentScript { Text = "First talk here" },
            new RecentScript { Text = "" },
            null!,
            new RecentScript { Text = "First talk here" },
            new RecentScript { Text = "Second", Title = "Kept title", Words = 9 },
        });
        Assert.Equal(2, list.Count);
        Assert.Equal("First talk here", list[0].Title);
        Assert.Equal(3, list[0].Words);
        Assert.Equal("Kept title", list[1].Title);
        Assert.Equal(9, list[1].Words);
    }

    [Fact]
    public void Dates_read_in_a_word()
    {
        var now = new DateTime(2026, 11, 14, 9, 0, 0); // a Saturday
        Assert.Equal("Today", StageScripts.When(now.AddHours(-2), now));
        Assert.Equal("Yesterday", StageScripts.When(now.AddDays(-1), now));
        Assert.Equal("Monday", StageScripts.When(new DateTime(2026, 11, 9, 20, 0, 0), now));
        Assert.Equal("2 Nov", StageScripts.When(new DateTime(2026, 11, 2), now));
        Assert.Equal("30 Dec 2025", StageScripts.When(new DateTime(2025, 12, 30), now));
        Assert.Equal("Today", StageScripts.When(now.AddHours(3), now));
    }

    [Fact]
    public void The_quiet_line_has_words_and_when()
    {
        var now = new DateTime(2026, 11, 14, 9, 0, 0);
        Assert.Equal("1,204 words · Yesterday", StageScripts.Meta(new RecentScript { Words = 1204, SavedAt = now.AddDays(-1) }, now));
        Assert.Equal("1 word", StageScripts.WordCount(1));
    }
}
