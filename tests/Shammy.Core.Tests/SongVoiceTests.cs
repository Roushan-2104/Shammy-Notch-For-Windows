using Shammy.Core.Music;
using Shammy.Core.Productivity;
using Shammy.Core.Search;

namespace Shammy.Core.Tests;

public class SongMatcherTests
{
    static readonly string[] Catalog = SongCatalog.Songs.Concat(SongCatalog.Artists).ToArray();

    [Theory]
    [InlineData("Kesaria", "Kesariya")]
    [InlineData("Raataan Lambiyaan", "ratan lambian")]
    [InlineData("Pasoori", "Pasuri")]
    [InlineData("Arijit Singh's", "arijit singh")]
    public void Spelling_variants_share_a_key(string a, string b) => Assert.Equal(SongMatcher.Key(a), SongMatcher.Key(b));

    [Theory]
    [InlineData("kesaria", "Kesariya")]
    [InlineData("Kaysria", "Kesariya")]
    [InlineData("Arigit Singh", "Arijit Singh")]
    [InlineData("Arijih Singh", "Arijit Singh")]
    [InlineData("Chunna Miria", "Channa Mereya")]
    [InlineData("Chenna Meriyya", "Channa Mereya")]
    [InlineData("Ratan Lumbian", "Raataan Lambiyan")]
    [InlineData("Tum Hee Ho", "Tum Hi Ho")]
    [InlineData("Shreya Goshal", "Shreya Ghoshal")]
    [InlineData("terrahone lagahu", "Tera Hone Laga Hoon")]
    [InlineData("Kal Ho Na Ho", "Kal Ho Naa Ho")]
    public void Heard_spellings_find_the_catalog_name(string heard, string expected)
    {
        var match = SongMatcher.FindBest(heard, Catalog);
        Assert.NotNull(match);
        Assert.Equal(expected, match!.Name);
    }

    [Theory]
    [InlineData("blinding lights")]
    [InlineData("shape of you by ed sheeran")]
    [InlineData("bohemian rhapsody")]
    [InlineData("case Rhea")]
    [InlineData("the weather tomorrow")]
    [InlineData("levitating")]
    [InlineData("some lofi beats")]
    public void Unrelated_text_matches_nothing(string heard) => Assert.Null(SongMatcher.FindBest(heard, Catalog));

    [Theory]
    [InlineData("Arijih Singh's songs", "Arijit Singh songs")]
    [InlineData("kesaria", "Kesariya")]
    [InlineData("tum hee ho by arigit singh", "Tum Hi Ho by Arijit Singh")]
    [InlineData("blinding lights", "blinding lights")]
    [InlineData("O Arijih Singh Songs", "Arijit Singh Songs")]
    [InlineData("a song by arigit singh", "a song by Arijit Singh")]
    [InlineData("O Maahi", "O Maahi")]
    public void Correct_respells_names_and_keeps_the_rest(string query, string expected) =>
        Assert.Equal(expected, SongMatcher.Correct(query, Catalog, out _));

    [Fact]
    public void Correct_counts_changed_names()
    {
        SongMatcher.Correct("Kesariya", Catalog, out int unchanged);
        SongMatcher.Correct("kesaria by arigit singh", Catalog, out int changed);
        Assert.Equal(0, unchanged);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Prompt_names_put_close_matches_and_history_last_within_budget()
    {
        var names = SongMatcher.PromptNames("Play Chunna Miria", new[] { "My Own Song", "Some Artist" }, SongCatalog.Songs, 300);
        Assert.True(names.Sum(n => n.Length + 2) <= 300);
        // Most useful last: the close match comes after the history, which comes after popular songs.
        Assert.Equal("Channa Mereya", names[^1]);
        Assert.True(names.ToList().IndexOf("My Own Song") > names.ToList().IndexOf("Kesariya") || !names.Contains("Kesariya"));
        Assert.Contains("My Own Song", names);
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Prompt_names_skip_duplicate_spellings()
    {
        var names = SongMatcher.PromptNames("", new[] { "Kesariya", "kesaria" }, new[] { "Kesariya" }, 500);
        Assert.Single(names);
    }
}

public class PlayIntentTests
{
    [Theory]
    [InlineData("Play. What would be?")]
    [InlineData("Play it to me on Spotify.")]
    [InlineData("Play Arigit Singh songs.")]
    [InlineData("play kesariya")]
    [InlineData("Que sria bajau.")]
    [InlineData("Kesariya Bajao.")]
    [InlineData("arijit ke gaane play karo")]
    [InlineData("Put on some Arijit Singh")]
    [InlineData("Playishk Bulava.")]
    public void Song_requests_look_like_play(string text) => Assert.True(MusicCommand.LooksLikePlay(text));

    [Theory]
    [InlineData("play")]
    [InlineData("Play the next song.")]
    [InlineData("pause")]
    [InlineData("play store")]
    [InlineData("Open Spotify.")]
    [InlineData("What is the weather in Delhi tomorrow?")]
    [InlineData("Turn up the volume.")]
    [InlineData("Play.")]
    [InlineData("5 minute ka timer laga do")]
    [InlineData("PlayStation 5 price")]
    [InlineData("players of the match")]
    [InlineData("playlist ideas for a road trip")]
    [InlineData("")]
    public void Other_text_does_not(string text) => Assert.False(MusicCommand.LooksLikePlay(text));

    [Theory]
    [InlineData("Kesariya bajao", MusicAction.PlayYouTubeMusic, "Kesariya")]
    [InlineData("tum hi ho spotify pe bajao", MusicAction.PlaySpotify, "tum hi ho")]
    [InlineData("arijit singh ke gaane sunao", MusicAction.PlayYouTubeMusic, "arijit singh")]
    [InlineData("Arijit Singh Ke Ganye Bajau.", MusicAction.PlayYouTubeMusic, "Arijit Singh")]
    [InlineData("channa mereya play karo", MusicAction.PlayYouTubeMusic, "channa mereya")]
    [InlineData("bajao pasoori", MusicAction.PlayYouTubeMusic, "pasoori")]
    [InlineData("Kesariya Bajau.", MusicAction.PlayYouTubeMusic, "Kesariya")]
    public void Hindi_play_commands_extract_the_query(string text, MusicAction action, string query)
    {
        var intent = MusicCommand.Parse(text);
        Assert.NotNull(intent);
        Assert.Equal(action, intent!.Action);
        Assert.Equal(query, intent.Query);
    }

    [Theory]
    [InlineData("Play Apna Bana Le, Play Apna Bana Le,", "Play Apna Bana Le.")]
    [InlineData("Play Kesariya. Play Kesariya.", "Play Kesariya.")]
    [InlineData("Play. Play Kesariya.", "Play. Play Kesariya.")]
    [InlineData("Play Tum Hi Ho on Spotify.", "Play Tum Hi Ho on Spotify.")]
    public void First_request_drops_a_repeat_in_the_same_sentence(string text, string expected) =>
        Assert.Equal(expected, MusicCommand.FirstRequest(text));

    [Theory]
    [InlineData("gaana bajao")]
    [InlineData("koi song sunao")]
    [InlineData("youtube chalao")]
    [InlineData("alarm laga do")]
    public void Hindi_without_a_song_is_not_a_play(string text) => Assert.Null(MusicCommand.Parse(text));

    [Fact]
    public void Play_songs_in_english_still_searches() =>
        Assert.Equal("songs", MusicCommand.Parse("play songs")!.Query);

    [Theory]
    [InlineData("Play Arijih Singh's songs.", "Play Arijit Singh songs.")]
    [InlineData("Play kesaria on Spotify.", "Play Kesariya on Spotify.")]
    [InlineData("Kessria bajao", "Kesariya bajao")]
    [InlineData("Turn up the volume.", "Turn up the volume.")]
    public void Correct_song_splices_the_query_back(string text, string expected)
    {
        var names = SongCatalog.Songs.Concat(SongCatalog.Artists).ToArray();
        Assert.Equal(expected, MusicCommand.CorrectSong(text, q => SongMatcher.Correct(q, names, out _)));
    }
}

public class ListeningHistoryTests
{
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    [Fact]
    public void Same_song_is_counted_not_duplicated()
    {
        var h = new ListeningHistory();
        Assert.True(h.Record("Kesariya", "Arijit Singh", T0));
        Assert.True(h.Record("kesaria", "Arijit Singh", T0.AddMinutes(5)));
        Assert.True(h.Record("Kesariya (From \"Brahmastra\")", "", T0.AddMinutes(9)));
        var song = Assert.Single(h.Songs);
        Assert.Equal(3, song.Plays);
        Assert.Equal(T0.AddMinutes(9), song.LastPlayed);
    }

    [Fact]
    public void Different_artists_keep_separate_entries()
    {
        var h = new ListeningHistory();
        h.Record("Hallelujah", "Leonard Cohen", T0);
        h.Record("Hallelujah", "Jeff Buckley", T0);
        Assert.Equal(2, h.Songs.Count);
    }

    [Theory]
    [InlineData("Kesariya - Brahmastra | Ranbir Kapoor | Alia Bhatt | Pritam | Arijit Singh", "Kesariya - Brahmastra")]
    [InlineData("Tum Hi Ho (Official Video) [HD]", "Tum Hi Ho")]
    [InlineData("Excuses ft. Gurlez Akhtar", "Excuses")]
    [InlineData("Channa Mereya Full Video Song", "Channa Mereya")]
    public void Titles_are_cleaned(string raw, string expected) => Assert.Equal(expected, ListeningHistory.Clean(raw));

    [Theory]
    [InlineData("")]
    [InlineData("How to fix a Windows update error in five easy steps for beginners")]
    [InlineData("#123 - 4567")]
    public void Things_that_are_not_song_titles_are_ignored(string title)
    {
        var h = new ListeningHistory();
        Assert.False(h.Record(title, "", T0));
        Assert.Empty(h.Songs);
    }

    [Fact]
    public void Artist_is_the_first_named_and_labels_are_dropped()
    {
        var h = new ListeningHistory();
        h.Record("Song A", "Arijit Singh, Shreya Ghoshal", T0);
        h.Record("Song B", "T-Series", T0);
        h.Record("Song C", "Pritam - Topic", T0);
        Assert.Equal(new[] { "Arijit Singh", "", "Pritam" }, h.Songs.Select(s => s.Artist));
    }

    [Fact]
    public void Cap_drops_the_least_played_oldest_songs()
    {
        var h = new ListeningHistory(cap: 3);
        h.Record("Favourite", "", T0);
        for (int i = 0; i < 5; i++) h.Record("Favourite", "", T0.AddMinutes(i));
        h.Record("Old once", "", T0.AddDays(1));
        h.Record("Newer once", "", T0.AddDays(2));
        h.Record("Newest once", "", T0.AddDays(3));
        Assert.Equal(3, h.Songs.Count);
        Assert.Contains(h.Songs, s => s.Title == "Favourite");
        Assert.DoesNotContain(h.Songs, s => s.Title == "Old once");
        Assert.Contains(h.Songs, s => s.Title == "Newest once");
    }

    [Fact]
    public void Names_are_best_first_and_include_artists()
    {
        var h = new ListeningHistory();
        h.Record("Old Song", "Old Artist", T0);
        h.Record("New Song", "Arijit Singh", T0.AddDays(1));
        h.Record("Other New", "Arijit Singh", T0.AddDays(1));
        Assert.Equal(new[] { "New Song", "Arijit Singh", "Other New", "Old Song", "Old Artist" }, h.Names().ToArray());
    }

    [Fact]
    public void Loading_merges_and_caps_stored_entries()
    {
        var stored = new[]
        {
            new HeardSong { Title = "Kesariya", Artist = "Arijit Singh", Plays = 2, LastPlayed = T0 },
            new HeardSong { Title = "kesaria", Artist = "", Plays = 1, LastPlayed = T0.AddDays(1) },
            new HeardSong { Title = "", Plays = 4, LastPlayed = T0 },
        };
        var h = new ListeningHistory(stored);
        var song = Assert.Single(h.Songs);
        Assert.Equal(3, song.Plays);
    }
}

public class TranscriptCheckTests
{
    [Theory]
    [InlineData("Turn up the volume.", 0.86f)]
    [InlineData("What is the weather in Delhi tomorrow?", 0.9f)]
    [InlineData("Remind me tomorrow at 9 to call mom.", 0.89f)]
    [InlineData("Open hum tum on Netflix", 0.8f)]
    [InlineData("Best budget phones under 20,000.", 0.83f)]
    [InlineData("Set a timer for 10 minutes.", -1f)]
    public void Ordinary_english_is_not_doubted(string text, float p) => Assert.Equal(TranscriptDoubt.None, TranscriptCheck.Assess(text, p));

    [Theory]
    [InlineData("کل صبح سات بجے کا الارم لگا دو", 0.9f, TranscriptDoubt.OtherScript)]
    [InlineData("कल सुबह सात बजे", 0.9f, TranscriptDoubt.OtherScript)]
    [InlineData("Thank you.", 0.9f, TranscriptDoubt.Hallucination)]
    [InlineData("Thanks for watching!", 0.9f, TranscriptDoubt.Hallucination)]
    [InlineData("Kalsubah Sathbaje Ka Alam Lagadu.", 0.67f, TranscriptDoubt.Hinglish)]
    [InlineData("Mutle Pachas had RK under a K-Achcha laptop batao.", 0.9f, TranscriptDoubt.Hinglish)]
    [InlineData("Today I'm going to play a game.", 0.55f, TranscriptDoubt.LowConfidence)]
    public void Wrong_looking_transcripts_are_doubted(string text, float p, TranscriptDoubt expected) =>
        Assert.Equal(expected, TranscriptCheck.Assess(text, p));

    [Fact]
    public void Retry_wins_when_likelier_or_english_was_unreadable()
    {
        Assert.True(TranscriptCheck.PreferRetry(TranscriptDoubt.LowConfidence, "Today I'm going to play a game.", 0.55f, "aaj mausam kaisa hai", 0.73f));
        Assert.False(TranscriptCheck.PreferRetry(TranscriptDoubt.LowConfidence, "Mutle Pachas laptop batao", 0.58f, "mujhe pachas hai", 0.55f));
        Assert.True(TranscriptCheck.PreferRetry(TranscriptDoubt.OtherScript, "کل صبح", 0.9f, "kal subah", 0.5f));
        Assert.False(TranscriptCheck.PreferRetry(TranscriptDoubt.Hinglish, "5 minute ka timer", 0.7f, "", 0.9f));
    }
}

public class DevanagariTests
{
    [Theory]
    [InlineData("कल सुबह 7 बजे का अलार्म लगा दो", "kal subah 7 baje kaa alaarm lagaa do")]
    [InlineData("केसरिया बजाओ", "kesariyaa bajaao")]
    [InlineData("मुझे ५०००० के अंदर", "mujhe 50000 ke andar")]
    [InlineData("ज़िंदगी", "zindagee")]
    [InlineData("play तुम ही हो", "play tum hee ho")]
    public void Spells_out_hindi(string devanagari, string latin) => Assert.Equal(latin, Devanagari.ToLatin(devanagari));

    [Fact]
    public void Leaves_latin_alone() => Assert.Equal("Turn up the volume.", Devanagari.ToLatin("Turn up the volume."));

    [Fact]
    public void Spelt_out_song_requests_still_match_the_catalog()
    {
        var latin = Devanagari.ToLatin("केसरिया बजाओ");
        var intent = MusicCommand.Parse(latin);
        Assert.NotNull(intent);
        Assert.Equal("Kesariya", SongMatcher.Correct(intent!.Query, SongCatalog.Songs, out _));
    }
}

public class HinglishCommandTests
{
    static readonly DateTime Now = new(2026, 3, 10, 15, 0, 0);

    [Theory]
    [InlineData("paanch minute ka timer laga do", "timer 5 minutes")]
    [InlineData("5 minute ka timer lagadoo.", "timer 5 minutes")]
    [InlineData("5 Minute Ka Timer Lagado", "timer 5 minutes")]
    [InlineData("das minat ka taimar set karo", "timer 10 minutes")]
    [InlineData("dedh ghante ka timer laga do", "timer 90 minutes")]
    [InlineData("ek ghante ka timer lagao", "timer 1 hour")]
    [InlineData("timer laga do 30 second ka", "timer 30 seconds")]
    [InlineData("पांच मिनट का टाइमर लगा दो", "timer 5 minutes")]
    public void Timers(string hinglish, string english) => Assert.Equal(english, HinglishCommands.ToEnglish(hinglish));

    [Theory]
    [InlineData("kal subah 7 baje yaad dilana ki doctor ko call karna", "remind me tomorrow morning at 7 to doctor ko call karna")]
    [InlineData("mujhe 10 minute baad yaad dila dena chai banana hai", "remind me in 10 minutes to chai banana hai")]
    [InlineData("aaj shaam 6 baje mummy ko phone karna yaad dilana", "remind me today evening at 6 to mummy ko phone karna")]
    [InlineData("aaj raat saade 9 baje remind karna", "remind me tonight at 9:30")]
    [InlineData("Do gante bad yad dilna ki khana khalo.", "remind me in 2 hours to khana khalo")]
    public void Reminders(string hinglish, string english) => Assert.Equal(english, HinglishCommands.ToEnglish(hinglish));

    [Theory]
    [InlineData("kal subah 7 baje ka alarm laga do", "alarm tomorrow morning at 7")]
    [InlineData("kal subah saat baajay ka alarm laga do.", "alarm tomorrow morning at 7")]
    [InlineData("kal subah sath bajeka alarm laga do.", "alarm tomorrow morning at 7")]
    [InlineData("Kal Subah Saat Bajay Ka Alam Lagado.", "alarm tomorrow morning at 7")]
    [InlineData("aaj raat 10 baje alarm set karo", "alarm tonight at 10")]
    [InlineData("कल सुबह 7 बजे का अलार्म लगा दो", "alarm tomorrow morning at 7")]
    public void Alarms(string hinglish, string english) => Assert.Equal(english, HinglishCommands.ToEnglish(hinglish));

    [Theory]
    [InlineData("set an alarm for 7 am", 2026, 3, 11, 7, 0)]
    [InlineData("alarm at 7 tomorrow", 2026, 3, 11, 7, 0)]
    [InlineData("Wake me up at 6:30 am.", 2026, 3, 11, 6, 30)]
    [InlineData("set alarm for 5 pm", 2026, 3, 10, 17, 0)]
    [InlineData("alarm in 20 minutes", 2026, 3, 10, 15, 20)]
    [InlineData("kal subah 7 baje ka alarm laga do", 2026, 3, 11, 7, 0)]
    [InlineData("Kal Subah Saat Bajay Ka Alam Lagado.", 2026, 3, 11, 7, 0)]
    public void Alarms_are_one_off_alarms(string text, int y, int mo, int d, int h, int mi)
    {
        var command = ProductivityParser.Parse(text, Now);
        Assert.NotNull(command);
        Assert.Equal(ProductivityAction.SetAlarm, command!.Action);
        Assert.Equal(AlarmDays.None, command.Alarm!.Days);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), command.Due);
        Assert.Contains("Alarm", Shammy.Core.Palette.PaletteQuery.DescribeProductivity(command, Now));
    }

    [Theory]
    [InlineData("alarm clock app")]
    [InlineData("alarm sounds")]
    [InlineData("best alarm clock")]
    [InlineData("alarm")]
    public void Alarm_searches_stay_searches(string text) => Assert.Null(ProductivityParser.Parse(text, Now));

    [Fact]
    public void Set_an_alarm_without_a_time_asks_for_one() =>
        Assert.Equal(ProductivityAction.ReminderNeedsTime, ProductivityParser.Parse("set an alarm", Now)!.Action);

    [Fact]
    public void Typed_hinglish_parses_directly_so_the_palette_can_preview_it()
    {
        Assert.Equal(ProductivityAction.StartTimer, ProductivityParser.Parse("paanch minute ka timer laga do", Now)!.Action);
        Assert.Equal(ProductivityAction.Remind, ProductivityParser.Parse("kal subah 7 baje yaad dilana ki doctor ko call karna", Now)!.Action);
    }

    [Theory]
    [InlineData("mujhe 50000 ke under ek achha laptop batao")]
    [InlineData("alarm laga do gaana")]
    [InlineData("Set a timer for 10 minutes.")]
    [InlineData("aaj mausam kaisa hai")]
    [InlineData("")]
    public void Everything_else_is_left_to_search(string text) => Assert.Null(HinglishCommands.ToEnglish(text));

    [Theory]
    [InlineData("paanch minute ka timer laga do", ProductivityAction.StartTimer)]
    [InlineData("kal subah 7 baje yaad dilana ki doctor ko call karna", ProductivityAction.Remind)]
    [InlineData("mujhe 10 minute baad yaad dila dena chai banana hai", ProductivityAction.Remind)]
    [InlineData("aaj shaam 6 baje mummy ko phone karna yaad dilana", ProductivityAction.Remind)]
    public void The_english_is_what_the_command_parser_understands(string hinglish, ProductivityAction action)
    {
        var english = HinglishCommands.ToEnglish(hinglish);
        var command = ProductivityParser.Parse(english!, Now);
        Assert.NotNull(command);
        Assert.Equal(action, command!.Action);
    }

    [Fact]
    public void Reminder_times_resolve_as_meant()
    {
        var command = ProductivityParser.Parse(HinglishCommands.ToEnglish("kal subah 7 baje yaad dilana ki doctor ko call karna")!, Now)!;
        Assert.Equal(new DateTime(2026, 3, 11, 7, 0, 0), command.Due);
        Assert.Equal("Doctor ko call karna", command.Text);
        var evening = ProductivityParser.Parse(HinglishCommands.ToEnglish("aaj shaam 6 baje mummy ko phone karna yaad dilana")!, Now)!;
        Assert.Equal(new DateTime(2026, 3, 10, 18, 0, 0), evening.Due);
    }
}

/// <summary>The Hinglish voice model (Apex) spells romanized Hindi its own way: long vowels, digits, "06:30".</summary>
public class ApexSpellingTests
{
    static readonly DateTime Now = new(2026, 3, 10, 15, 0, 0);

    [Theory]
    [InlineData("Kal subah 7 baaje ka alarm laga doon", 2026, 3, 11, 7, 0)]
    [InlineData("Kal subah saat baaje ka alarm lagaadoo.", 2026, 3, 11, 7, 0)]
    [InlineData("Kal subah saat baaje ka alarm lagaajoo.", 2026, 3, 11, 7, 0)]
    [InlineData("Kal subah 06:30 baje ka alarm laga do", 2026, 3, 11, 6, 30)]
    [InlineData("Wake me up at 06:30.", 2026, 3, 11, 6, 30)]
    public void Alarms(string text, int y, int mo, int d, int h, int mi)
    {
        var c = ProductivityParser.Parse(text, Now);
        Assert.NotNull(c);
        Assert.Equal(ProductivityAction.SetAlarm, c!.Action);
        Assert.Equal(new DateTime(y, mo, d, h, mi, 0), c.Due);
    }

    [Theory]
    [InlineData("Das minute ka timer laga do.", 10)]
    [InlineData("10 minute ka timer lagaa do", 10)]
    [InlineData("Paanch minute ka timer laga do.", 5)]
    [InlineData("Das minute ka timer lagaajoo.", 10)]
    public void Timers(string text, int minutes)
    {
        var c = ProductivityParser.Parse(text.TrimEnd('.'), Now);
        Assert.NotNull(c);
        Assert.Equal(ProductivityAction.StartTimer, c!.Action);
        Assert.Equal(TimeSpan.FromMinutes(minutes), c.Duration);
    }

    [Theory]
    [InlineData("Kal subah nau baaje yaad dilna ki doctor ko call karna.")]
    [InlineData("Kal subah nau baaje yaad dilaana ki doctor ko call karna")]
    public void Reminders(string text)
    {
        var c = ProductivityParser.Parse(text, Now);
        Assert.NotNull(c);
        Assert.Equal(ProductivityAction.Remind, c!.Action);
        Assert.Equal(new DateTime(2026, 3, 11, 9, 0, 0), c.Due);
        Assert.Equal("Doctor ko call karna", c.Text);
    }

    [Theory]
    [InlineData("Kesriya baajao.", "Kesriya")]
    [InlineData("Kesriya bhaajao.", "Kesriya")]
    [InlineData("Tum hi ho sunaao", "Tum hi ho")]
    [InlineData("baajao pasoori", "pasoori")]
    public void Play_verbs(string text, string query)
    {
        var intent = MusicCommand.Parse(text);
        Assert.NotNull(intent);
        Assert.Equal(MusicAction.PlayYouTubeMusic, intent!.Action);
        Assert.Equal(query, intent.Query);
    }

    [Theory]
    [InlineData("Post the music.")]
    [InlineData("Pose the music")]
    public void Misheard_pause(string text) => Assert.Equal(MusicAction.Pause, MusicCommand.Parse(text)!.Action);

    [Theory]
    [InlineData("Mujhe 50000 ke andar ek achchha laptop batao")]
    [InlineData("Mujhe pachaas hazaar ke under ek achchha laptop baantao.")]
    [InlineData("Aaj moesam kaaza hai?")]
    public void Questions_stay_searches(string text)
    {
        Assert.Null(ProductivityParser.Parse(text, Now));
        Assert.Null(MusicCommand.Parse(text));
    }
}
