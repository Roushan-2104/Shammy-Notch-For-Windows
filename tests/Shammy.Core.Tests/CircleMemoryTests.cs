using Shammy.Core.CircleMemory;

namespace Shammy.Core.Tests;

public class CircleMemoryTests
{
    /// <summary>Saturday 14 Nov 2026, 4:12 PM.</summary>
    static readonly DateTime Now = new(2026, 11, 14, 16, 12, 0);

    static RememberedCircle C(string words, string title, string? line, string kind, string? app, DateTime at) =>
        new() { Words = words, Title = title, Line = line, Kind = kind, App = app, At = at };

    static List<RememberedCircle> Sample() => new()
    {
        C("明日の会議は午後3時からです", "Japanese message, translated", "Tomorrow's meeting starts at 3 PM.", "Foreign", "WhatsApp", Now.AddHours(-2)),
        C("3 PM PST", "3 PM PST", "4:30 AM your time, Sun 15 Nov", "ZoneTime", "Slack", Now.AddDays(-1).AddHours(-3)),
        C("6E 2134", "6E 2134", "IndiGo flight", "Flight", "WhatsApp", new DateTime(2026, 11, 13, 18, 40, 0)),
        C("", "Wireless earbuds, black", "Cheapest at Store A", "Product", "Chrome", new DateTime(2026, 11, 9, 11, 0, 0)),
        C("what is a mortgage", "Mortgage", "A loan to buy a home", "Question", "Edge", new DateTime(2026, 11, 1, 9, 0, 0)),
    };

    // ---- The log: newest first, 30 days, 500 at most ------------------------------------------------

    [Fact]
    public void LogKeepsNewestFirst()
    {
        var log = new CircleLog(Sample());
        log.Add(C("x", "Newest", null, "Text", null, Now), Now);
        Assert.Equal("Newest", log.All[0].Title);
        Assert.True(log.All.Zip(log.All.Skip(1)).All(p => p.First.At >= p.Second.At));
    }

    [Fact]
    public void LogDropsCirclesOlderThan30Days()
    {
        var log = new CircleLog(new[]
        {
            C("old", "Old", null, "Text", null, Now.AddDays(-31)),
            C("fresh", "Fresh", null, "Text", null, Now.AddDays(-29)),
        });
        var dropped = log.Prune(Now);
        Assert.Equal("Old", Assert.Single(dropped).Title);
        Assert.Equal("Fresh", Assert.Single(log.All).Title);
    }

    [Fact]
    public void LogCapsAt500DroppingTheOldest()
    {
        var log = new CircleLog();
        var dropped = new List<RememberedCircle>();
        for (int i = 0; i < CircleLog.MaxCount + 3; i++)
            dropped.AddRange(log.Add(C($"w{i}", $"T{i}", null, "Text", null, Now.AddMinutes(-1000 + i)), Now));
        Assert.Equal(CircleLog.MaxCount, log.Count);
        Assert.Equal(new[] { "T0", "T1", "T2" }, dropped.Select(d => d.Title).OrderBy(t => t).ToArray());
        Assert.Equal($"T{CircleLog.MaxCount + 2}", log.All[0].Title);
    }

    [Fact]
    public void LogRemovesAndClears()
    {
        var log = new CircleLog(Sample());
        var id = log.All[1].Id;
        Assert.NotNull(log.Remove(id));
        Assert.Null(log.Find(id));
        Assert.Null(log.Remove(id));
        Assert.Equal(4, log.Clear().Count);
        Assert.Equal(0, log.Count);
    }

    [Fact]
    public void FiltersGroupTheKinds()
    {
        var log = new CircleLog(Sample());
        Assert.Equal(5, log.In(CircleGroup.All).Count());
        Assert.Equal("3 PM PST", Assert.Single(log.In(CircleGroup.Times)).Title);
        Assert.Single(log.In(CircleGroup.Translations));
        Assert.Single(log.In(CircleGroup.Products));
        Assert.Single(log.In(CircleGroup.Questions));
        Assert.Empty(log.In(CircleGroup.Pictures));
        Assert.Equal(CircleGroup.Times, CircleKinds.GroupOf("When"));
        Assert.Equal(CircleGroup.Translations, CircleKinds.GroupOf("Translation"));
        Assert.Equal(CircleGroup.Pictures, CircleKinds.GroupOf("Picture"));
        Assert.Equal(CircleGroup.All, CircleKinds.GroupOf("Phone"));
    }

    [Fact]
    public void RowTags()
    {
        Assert.Equal("明日", CircleKinds.Tag("Foreign", "明日の会議"));
        Assert.Equal("PST", CircleKinds.Tag("ZoneTime", "3 PM PST"));
        Assert.Equal("✈", CircleKinds.Tag("Flight", "6E 2134"));
        Assert.Equal("?", CircleKinds.Tag("Question", "what"));
    }

    [Fact]
    public void AnswerTextHasTitleAndLineOnce()
    {
        Assert.Equal("6E 2134\nIndiGo flight", C("", "6E 2134", "IndiGo flight", "Flight", null, Now).AnswerText);
        Assert.Equal("Mortgage", C("", "Mortgage", null, "Question", null, Now).AnswerText);
        Assert.Equal("3 PM PST is 4:30 AM", C("", "3 PM PST", "3 PM PST is 4:30 AM", "ZoneTime", null, Now).AnswerText);
    }

    [Fact]
    public void RowLinesAreWhatAndAnswer()
    {
        var flight = C("6E 2134", "6E 2134", "IndiGo flight", "Flight", null, Now);
        Assert.Equal(("6E 2134", "IndiGo flight"), (flight.What, flight.Answer));
        var time = C("3 PM PST", "4:30 AM", "your time, Sun 15 Nov", "ZoneTime", null, Now);
        Assert.Equal(("3 PM PST", "4:30 AM · your time, Sun 15 Nov"), (time.What, time.Answer));
        var picture = C("", "Wireless earbuds, black", "Cheapest at Store A", "Product", null, Now);
        Assert.Equal(("Wireless earbuds, black", "Cheapest at Store A"), (picture.What, picture.Answer));
        var translation = C("明日の会議は\n午後3時からです", "明日の会議は…", "Tomorrow's meeting starts at 3 PM.", "Foreign", null, Now);
        Assert.Equal("明日の会議は 午後3時からです", translation.What);
        Assert.Equal("Tomorrow's meeting starts at 3 PM.", translation.Answer);
        Assert.Equal("Tomorrow's meeting starts at 3 PM.", translation.AnswerText);
        var piped = C("6E 2134", "IndiGo flight", null, "Flight", null, Now);
        Assert.Equal("IndiGo flight", piped.Answer);
    }

    [Fact]
    public void StoredJsonLeavesOutTheWorkedOutLines()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(C("a", "b", "c", "Text", "App", Now));
        Assert.DoesNotContain("AnswerText", json);
        Assert.DoesNotContain("\"What\"", json);
        var back = System.Text.Json.JsonSerializer.Deserialize<RememberedCircle>(json)!;
        Assert.Equal(("a", "b", "c", "App", Now), (back.Words, back.Title, back.Line, back.App, back.At));
    }

    // ---- When it reads -------------------------------------------------------------------------------

    [Fact]
    public void WhenChips()
    {
        Assert.Equal("Today 2:14 PM", CircleWhen.Chip(new DateTime(2026, 11, 14, 14, 14, 0), Now));
        Assert.Equal("Today 14:14", CircleWhen.Chip(new DateTime(2026, 11, 14, 14, 14, 0), Now, h24: true));
        Assert.Equal("Yesterday", CircleWhen.Chip(new DateTime(2026, 11, 13, 23, 59, 0), Now));
        Assert.Equal("Mon", CircleWhen.Chip(new DateTime(2026, 11, 9, 8, 0, 0), Now));
        Assert.Equal("1 Nov", CircleWhen.Chip(new DateTime(2026, 11, 1, 8, 0, 0), Now));
        Assert.Equal("28 Dec 2025", CircleWhen.Chip(new DateTime(2025, 12, 28, 8, 0, 0), new DateTime(2026, 1, 10)));
    }

    [Fact]
    public void DraggedPictureFileName()
    {
        Assert.Equal("Circle 14 Nov 4.12 PM.png", CircleWhen.FileName(Now));
        Assert.Equal("Circle 14 Nov 16.12.png", CircleWhen.FileName(Now, h24: true));
    }

    [Fact]
    public void FoundSentenceSaysWhereAndWhen()
    {
        var c = C("6E 2134", "6E 2134", "IndiGo flight", "Flight", "WhatsApp", new DateTime(2026, 11, 13, 18, 40, 0));
        Assert.Equal("You circled it in WhatsApp yesterday at 6:40 PM.", CircleWhen.Sentence(c, Now));
        c.App = null;
        Assert.Equal("You circled it yesterday at 6:40 PM.", CircleWhen.Sentence(c, Now));
    }

    // ---- Date words ----------------------------------------------------------------------------------

    static CircleSpan? Span(string words)
    {
        var tokens = words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        return CircleDates.Take(tokens, Now);
    }

    static void Day(string words, int year, int month, int day)
    {
        var span = Span(words);
        Assert.NotNull(span);
        Assert.Equal(new DateTime(year, month, day), span!.From);
        Assert.Equal(new DateTime(year, month, day).AddDays(1), span.To);
    }

    [Theory]
    [InlineData("today", 14)]
    [InlineData("aaj", 14)]
    [InlineData("yesterday", 13)]
    [InlineData("kal", 13)]
    [InlineData("parso", 12)]
    [InlineData("day before yesterday", 12)]
    [InlineData("3 days ago", 11)]
    [InlineData("teen din pehle", 11)]
    [InlineData("saturday", 14)]
    [InlineData("last saturday", 7)]
    [InlineData("monday", 9)]
    [InlineData("mon", 9)]
    [InlineData("last fri", 13)]
    [InlineData("somvar", 9)]
    [InlineData("14 nov", 14)]
    [InlineData("14th november", 14)]
    [InlineData("nov 2", 2)]
    public void DayWords(string words, int day) => Day(words, 2026, 11, day);

    [Fact]
    public void ADateStillToComeIsLastYear() => Day("20 dec", 2025, 12, 20);

    [Fact]
    public void WeekAndMonthWords()
    {
        var lastWeek = Span("last week")!;
        Assert.Equal(new DateTime(2026, 11, 7), lastWeek.From);
        Assert.Equal(new DateTime(2026, 11, 15), lastWeek.To);
        Assert.Equal(lastWeek, Span("pichhle hafte"));
        Assert.Equal(new DateTime(2026, 11, 9), Span("this week")!.From);
        Assert.Equal(new DateTime(2026, 11, 1), Span("this month")!.From);
        var lastMonth = Span("last month")!;
        Assert.Equal(new DateTime(2026, 10, 1), lastMonth.From);
        Assert.Equal(new DateTime(2026, 11, 1), lastMonth.To);
        Assert.Equal(new DateTime(2026, 11, 12), Span("last 3 days")!.From);
    }

    [Fact]
    public void PartsOfADay()
    {
        var morning = Span("this morning")!;
        Assert.Equal(new DateTime(2026, 11, 14, 0, 0, 0), morning.From);
        Assert.Equal(new DateTime(2026, 11, 14, 12, 0, 0), morning.To);
        var night = Span("last night")!;
        Assert.Equal(new DateTime(2026, 11, 13, 18, 0, 0), night.From);
        Assert.Equal(night, Span("kal raat"));
    }

    [Fact]
    public void DateWordsAreTakenOutOfTheWords()
    {
        var tokens = new List<string> { "flights", "yesterday", "whatsapp" };
        Assert.NotNull(CircleDates.Take(tokens, Now));
        Assert.Equal(new[] { "flights", "whatsapp" }, tokens);
        Assert.Null(Span("flights from indigo"));
        Assert.Null(Span("what is a marker"));
    }

    // ---- Commands ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("my circles")]
    [InlineData("circles")]
    [InlineData("Show my circles")]
    [InlineData("what did I circle?")]
    [InlineData("circle memory")]
    public void OpenTheCard(string text) => Assert.Equal(CircleAsk.Open, CircleSearch.ParseCommand(text, Now)?.Ask);

    [Theory]
    [InlineData("how do I draw a circle")]
    [InlineData("circle area formula")]
    [InlineData("my circle of friends")]
    [InlineData("weather in pune")]
    [InlineData("circle to search")]
    public void NotAboutCircles(string text) => Assert.Null(CircleSearch.ParseCommand(text, Now));

    /// <summary>Modules that start before circle memory (by name) must not take these phrases first.</summary>
    [Theory]
    [InlineData("what did I circle yesterday about flights")]
    [InlineData("my circles")]
    [InlineData("circles from WhatsApp")]
    [InlineData("kal kya circle kiya tha")]
    public void EarlierCommandsLeaveCirclePhrasesAlone(string text)
    {
        Assert.NotNull(CircleSearch.ParseCommand(text, Now));
        Assert.Null(Shammy.Core.Search.LocalQuestion.Parse(text));
        Assert.False(Shammy.Core.Cards.CardPhrases.IsCalendar(text));
        Assert.Null(Shammy.Core.Ambient.CityWeather.Parse(text));
        Assert.Null(Shammy.Core.Ambient.AmbientCommands.ParseWeather(text));
        Assert.Null(Shammy.Core.Ambient.AmbientCommands.ParseTicker(text));
        Assert.Null(Shammy.Core.Ambient.AmbientCommands.ParsePet(text));
        Assert.Null(Shammy.Core.Ambient.CricketLive.ParseCommand(text));
        Assert.Null(Shammy.Core.Captions.CaptionPhrases.Parse(text));
    }

    [Fact]
    public void FlightsYesterday()
    {
        var q = CircleSearch.ParseCommand("what did I circle yesterday about flights", Now)!;
        Assert.Equal(CircleAsk.Find, q.Ask);
        Assert.Equal(new[] { "flights" }, q.Terms);
        Assert.Equal(new DateTime(2026, 11, 13), q.Span!.From);
        var found = CircleSearch.Find(Sample(), q);
        Assert.Equal("6E 2134", found[0].Title);
        Assert.Single(found);
    }

    [Fact]
    public void CirclesFromAnApp()
    {
        var q = CircleSearch.ParseCommand("circles from WhatsApp", Now)!;
        Assert.Equal(new[] { "whatsapp" }, q.Terms);
        var found = CircleSearch.Find(Sample(), q);
        Assert.Equal(2, found.Count);
        Assert.All(found, c => Assert.Equal("WhatsApp", c.App));
        Assert.Equal("Japanese message, translated", found[0].Title); // the newest of equals first
    }

    [Fact]
    public void HinglishYesterday()
    {
        var q = CircleSearch.ParseCommand("kal kya circle kiya tha", Now)!;
        Assert.Equal(CircleAsk.Find, q.Ask);
        Assert.Empty(q.Terms);
        var found = CircleSearch.Find(Sample(), q);
        Assert.Equal(new[] { "6E 2134", "3 PM PST" }, found.Select(c => c.Title).ToArray());
    }

    [Fact]
    public void KindWordsFindCircles()
    {
        var all = Sample();
        Assert.Equal("Wireless earbuds, black", CircleSearch.Find(all, CircleSearch.ParseFilter("products", Now))[0].Title);
        Assert.Equal("3 PM PST", CircleSearch.Find(all, CircleSearch.ParseFilter("time zone", Now))[0].Title);
        Assert.Equal("Japanese message, translated", CircleSearch.Find(all, CircleSearch.ParseFilter("translation", Now))[0].Title);
        Assert.Equal("Mortgage", CircleSearch.Find(all, CircleSearch.ParseFilter("mortgage", Now))[0].Title);
    }

    [Fact]
    public void AnswerAndWordsAreSearched()
    {
        var all = Sample();
        Assert.Equal("6E 2134", CircleSearch.Find(all, CircleSearch.ParseFilter("indigo", Now))[0].Title);
        Assert.Equal("Japanese message, translated", CircleSearch.Find(all, CircleSearch.ParseFilter("meeting", Now))[0].Title);
        Assert.Equal("Japanese message, translated", CircleSearch.Find(all, CircleSearch.ParseFilter("明日の会議は午後3時からです", Now))[0].Title);
        Assert.Empty(CircleSearch.Find(all, CircleSearch.ParseFilter("bitcoin", Now)));
    }

    [Fact]
    public void TypingInTheBoxNeedsEveryWord()
    {
        var all = Sample();
        var q = CircleSearch.ParseFilter("indigo whatsapp", Now);
        Assert.Equal("6E 2134", Assert.Single(CircleSearch.Find(all, q, requireAll: true)).Title);
        Assert.Empty(CircleSearch.Find(all, CircleSearch.ParseFilter("indigo chrome", Now), requireAll: true));
        // Without requireAll the best partial match still comes back (a spoken question with an extra word).
        Assert.NotEmpty(CircleSearch.Find(all, CircleSearch.ParseFilter("indigo delhi", Now)));
    }

    [Fact]
    public void ADateAloneListsThatDay()
    {
        var found = CircleSearch.Find(Sample(), CircleSearch.ParseFilter("mon", Now));
        Assert.Equal("Wireless earbuds, black", Assert.Single(found).Title);
        Assert.Single(CircleSearch.Find(Sample(), CircleSearch.ParseFilter("1 nov", Now)));
    }

    // ---- App names -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("WhatsApp.Root", null, "WhatsApp")]
    [InlineData("chrome", "Google Chrome", "Chrome")]
    [InlineData("msedge.exe", null, "Edge")]
    [InlineData("someapp", "Some App Application", "Some App")]
    [InlineData("someapp", "", "Someapp")]
    [InlineData("", null, null)]
    public void FriendlyAppNames(string process, string? description, string? expected) =>
        Assert.Equal(expected, AppNames.Friendly(process, description));
}
