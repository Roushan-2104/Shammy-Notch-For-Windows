using Shammy.Core.Palette;
using Shammy.Core.Productivity;

namespace Shammy.Core.Tests;

public class PaletteRankerTests
{
    static PaletteItem Item(string key, PaletteKind kind, int match, bool confident = false) =>
        new(key, kind, key, "", match) { Confident = confident };

    static readonly PaletteItem Web = Item("web", PaletteKind.Web, 0);

    [Fact]
    public void Bands_order_sources_and_web_is_always_last()
    {
        var r = PaletteRanker.Rank(new[]
        {
            Web,
            Item("music", PaletteKind.Music, 100),
            Item("file", PaletteKind.File, 100),
            Item("app", PaletteKind.App, 70),
            Item("setting", PaletteKind.Setting, 90),
            Item("cmd", PaletteKind.Command, 50),
        });
        Assert.Equal(new[] { "cmd", "setting", "app", "file", "music", "web" }, r.Items.Select(i => i.Key));
    }

    [Fact]
    public void Commands_beat_even_an_exact_app_match()
    {
        // "weather" runs Shammy's weather even when a Weather app is installed.
        var r = PaletteRanker.Rank(new[] { Item("app", PaletteKind.App, 100, true), Item("cmd", PaletteKind.Command, 100, true), Web });
        Assert.Equal(new[] { "cmd", "app", "web" }, r.Items.Select(i => i.Key));
        Assert.Equal(0, r.Selected);
    }

    [Fact]
    public void No_learned_boost_lifts_anything_over_a_command()
    {
        var r = PaletteRanker.Rank(new[] { Item("cmd", PaletteKind.Command, 0, true), Item("app", PaletteKind.App, 100, true) },
            i => i.Key == "app" ? 10_000 : 0);
        Assert.Equal("cmd", r.Items[0].Key);
    }

    [Fact]
    public void Strong_apps_beat_settings_files_music_and_web()
    {
        // "spot": no command handles it, so Spotify is on top and Enter opens it.
        var r = PaletteRanker.Rank(new[]
        {
            Item("setting", PaletteKind.Setting, 90), Item("file", PaletteKind.File, 100), Item("music", PaletteKind.Music, 100), Web,
            Item("app", PaletteKind.App, 90, confident: true),
        });
        Assert.Equal(new[] { "app", "setting", "file", "music", "web" }, r.Items.Select(i => i.Key));
        Assert.Equal(0, r.Selected);
    }

    [Fact]
    public void An_exact_settings_word_beats_an_app_that_merely_starts_with_it()
    {
        // "sound" is the volume row, not Sound Recorder; an exact app name still wins.
        var r = PaletteRanker.Rank(new[] { Item("recorder", PaletteKind.App, 90, true), Item("volume", PaletteKind.Setting, 100, true) });
        Assert.Equal("volume", r.Items[0].Key);
        var exact = PaletteRanker.Rank(new[] { Item("volume", PaletteKind.Setting, 100, true), Item("camera", PaletteKind.App, 100, true) });
        Assert.Equal("camera", exact.Items[0].Key);
    }

    [Fact]
    public void Weak_top_row_leaves_enter_to_the_old_behaviour()
    {
        var r = PaletteRanker.Rank(new[] { Item("app", PaletteKind.App, 50), Web });
        Assert.Equal(-1, r.Selected);
        Assert.Null(r.SelectedItem);
    }

    [Fact]
    public void Only_web_means_no_selection()
    {
        var r = PaletteRanker.Rank(new[] { Web });
        Assert.Single(r.Items);
        Assert.Equal(-1, r.Selected);
    }

    [Fact]
    public void Caps_rows_but_keeps_web()
    {
        var items = Enumerable.Range(0, 20).Select(i => Item($"f{i}", PaletteKind.File, 50)).Append(Web);
        var r = PaletteRanker.Rank(items);
        Assert.Equal(PaletteRanker.MaxRows, r.Items.Count);
        Assert.Equal("web", r.Items[^1].Key);
    }

    [Fact]
    public void Equal_scores_keep_source_order_and_duplicates_collapse()
    {
        var r = PaletteRanker.Rank(new[]
        {
            Item("a", PaletteKind.File, 60), Item("b", PaletteKind.File, 60), Item("a", PaletteKind.File, 40), Item("c", PaletteKind.File, 60),
        });
        Assert.Equal(new[] { "a", "b", "c" }, r.Items.Select(i => i.Key));
    }

    [Fact]
    public void Learned_boost_lifts_a_row_and_makes_it_the_default()
    {
        var items = new[] { Item("app1", PaletteKind.App, 70), Item("app2", PaletteKind.App, 70), Web };
        var r = PaletteRanker.Rank(items, i => i.Key == "app2" ? 120 : 0);
        Assert.Equal("app2", r.Items[0].Key);
        Assert.Equal(0, r.Selected);
    }

    [Fact]
    public void Boost_is_capped_so_habit_cannot_jump_far()
    {
        var items = new[] { Item("cmd", PaletteKind.Command, 100), Item("music", PaletteKind.Music, 100) };
        var r = PaletteRanker.Rank(items, i => i.Key == "music" ? 10_000 : 0);
        Assert.Equal("cmd", r.Items[0].Key);
    }

    [Fact]
    public void Late_results_never_move_rows_at_or_above_the_selection()
    {
        var shown = new RankedPalette(new[] { Item("app", PaletteKind.App, 70), Item("music", PaletteKind.Music, 90), Web }, 1);
        var fresh = PaletteRanker.Rank(new[] { Item("app", PaletteKind.App, 70), Item("file", PaletteKind.File, 90), Item("music", PaletteKind.Music, 90), Web });
        Assert.Equal(new[] { "app", "file", "music", "web" }, fresh.Items.Select(i => i.Key));

        var merged = PaletteRanker.Stabilize(shown, fresh);
        Assert.Equal(new[] { "app", "music", "file", "web" }, merged.Items.Select(i => i.Key));
        Assert.Equal(1, merged.Selected);
    }

    [Fact]
    public void Late_results_go_above_web_when_web_is_selected_and_selection_follows_it()
    {
        var shown = new RankedPalette(new[] { Item("app", PaletteKind.App, 70), Web }, 1);
        var fresh = PaletteRanker.Rank(new[] { Item("app", PaletteKind.App, 70), Item("file", PaletteKind.File, 90), Web });
        var merged = PaletteRanker.Stabilize(shown, fresh);
        Assert.Equal(new[] { "app", "file", "web" }, merged.Items.Select(i => i.Key));
        Assert.Equal("web", merged.SelectedItem!.Key);
    }

    [Fact]
    public void Without_a_selection_late_results_simply_rank()
    {
        var shown = new RankedPalette(new[] { Item("music", PaletteKind.Music, 90), Web }, -1);
        var fresh = PaletteRanker.Rank(new[] { Item("file", PaletteKind.File, 90), Item("music", PaletteKind.Music, 90), Web });
        Assert.Same(fresh, PaletteRanker.Stabilize(shown, fresh));
    }
}

public class FrecencyStoreTests
{
    static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0);

    [Fact]
    public void Unknown_keys_get_nothing()
    {
        Assert.Equal(0, new FrecencyStore().Boost("app:x", "x", Now));
    }

    [Fact]
    public void Same_text_picked_before_reaches_the_learned_threshold()
    {
        var s = new FrecencyStore();
        s.Record("app:spotify", "s", Now);
        Assert.True(s.Boost("app:spotify", "s", Now) >= PaletteRanker.LearnedBoost);
        Assert.True(s.Boost("app:spotify", "x", Now) < PaletteRanker.LearnedBoost);
    }

    [Fact]
    public void Related_text_gets_a_smaller_lift()
    {
        var s = new FrecencyStore();
        s.Record("app:spotify", "spot", Now);
        int exact = s.Boost("app:spotify", "spot", Now);
        int longer = s.Boost("app:spotify", "spotify", Now);
        int unrelated = s.Boost("app:spotify", "music", Now);
        Assert.True(exact > longer && longer > unrelated && unrelated > 0);
    }

    [Fact]
    public void Repeated_picks_rise_and_old_ones_fade()
    {
        var s = new FrecencyStore();
        s.Record("a", "", Now);
        int once = s.Boost("a", "", Now);
        for (int i = 0; i < 5; i++) s.Record("a", "", Now);
        int often = s.Boost("a", "", Now);
        Assert.True(often > once);
        Assert.True(s.Boost("a", "", Now.AddDays(60)) < often);
        Assert.True(often <= 70);
    }

    [Fact]
    public void Remembers_only_the_last_few_texts_per_row()
    {
        var s = new FrecencyStore();
        for (int i = 0; i < 9; i++) s.Record("a", $"q{i}", Now);
        var e = s.Entries.Single();
        Assert.Equal(FrecencyStore.QueriesPerEntry, e.Queries.Count);
        Assert.Equal("q8", e.Queries[0]);
    }

    [Fact]
    public void Evicts_the_weakest_when_full()
    {
        var s = new FrecencyStore();
        s.Record("old", "", Now.AddDays(-100));
        for (int i = 0; i < FrecencyStore.MaxEntries; i++) s.Record($"k{i}", "", Now);
        Assert.Equal(FrecencyStore.MaxEntries, s.Entries.Count);
        Assert.DoesNotContain(s.Entries, e => e.Key == "old");
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var s = new FrecencyStore();
        s.Record("file:C:\\a.txt", "a txt", Now);
        var json = System.Text.Json.JsonSerializer.Serialize(s.Entries.ToList());
        var back = new FrecencyStore(System.Text.Json.JsonSerializer.Deserialize<List<FrecencyStore.Entry>>(json));
        Assert.Equal(s.Boost("file:C:\\a.txt", "a txt", Now), back.Boost("file:C:\\a.txt", "a txt", Now));
    }
}

public class WindowsSearchSqlTests
{
    const string Scope = @"C:\Users\Roushan Sharma";

    [Fact]
    public void Builds_a_scoped_prefix_query_per_word()
    {
        var sql = WindowsSearchSql.Build("budget 2024", Scope, 20);
        Assert.Equal(
            "SELECT TOP 20 System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:C:/Users/Roushan Sharma'" +
            " AND CONTAINS(System.FileName, '\"budget*\"') AND CONTAINS(System.FileName, '\"2024*\"')" +
            string.Concat(WindowsSearchSql.ExcludedFolderWords.Select(w => $" AND NOT CONTAINS(System.ItemFolderPathDisplay, '\"{w}\"')")) +
            " ORDER BY System.DateModified DESC",
            sql);
    }

    [Theory]
    [InlineData("x' OR 1=1 --")]
    [InlineData("\"; DROP TABLE x; --")]
    [InlineData("a%b_c*d[e]")]
    [InlineData("it's (draft)")]
    public void Typed_text_cannot_break_out_of_the_query(string text)
    {
        var sql = WindowsSearchSql.Build(text, Scope)!;
        var where = sql[sql.IndexOf(" AND CONTAINS", StringComparison.Ordinal)..sql.IndexOf(" AND NOT CONTAINS", StringComparison.Ordinal)];
        foreach (var part in where.Split(" AND CONTAINS(System.FileName, '\"", StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part[..part.IndexOf("*\"')", StringComparison.Ordinal)];
            Assert.DoesNotContain('\'', token);
            Assert.DoesNotContain('"', token);
            Assert.DoesNotContain('%', token);
            Assert.DoesNotContain('*', token);
            Assert.DoesNotContain('(', token);
            Assert.DoesNotContain(';', token);
        }
    }

    [Fact]
    public void Strips_special_characters_and_keeps_name_characters()
    {
        Assert.Equal(new[] { "its", "draft" }, WindowsSearchSql.Tokens("it's (draft)"));
        Assert.Equal(new[] { "q3-report", "notes_v2.txt" }, WindowsSearchSql.Tokens("q3-report notes_v2.txt"));
        Assert.Equal(new[] { "abc" }, WindowsSearchSql.Tokens("abc ABC *** ''"));
    }

    [Fact]
    public void Caps_words_and_length()
    {
        Assert.Equal(WindowsSearchSql.MaxTokens, WindowsSearchSql.Tokens("a1 b2 c3 d4 e5 f6 g7").Count);
        Assert.Equal(WindowsSearchSql.MaxTokenLength, WindowsSearchSql.Tokens(new string('z', 300))[0].Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a")]
    [InlineData("*** %%% '")]
    public void Nothing_searchable_means_no_query(string text)
    {
        Assert.Null(WindowsSearchSql.Build(text, Scope));
    }

    [Theory]
    [InlineData(@"C:\Users\me\a\obj\x.cs")]
    [InlineData(@"C:\Users\me\a\venv\x.py")]
    [InlineData(@"C:\Users\me\a\env\x.py")]
    [InlineData(@"C:\Users\me\a\__pycache__\x.pyc")]
    [InlineData(@"C:\Users\me\a\dist\x.js")]
    [InlineData(@"C:\Users\me\a\build\x.o")]
    [InlineData(@"C:\Users\me\a\.vs\x")]
    [InlineData(@"C:\Users\me\a\.idea\x.xml")]
    [InlineData(@"C:\Users\me\a\.gradle\x")]
    [InlineData(@"C:\Users\me\a\packages\x.nupkg")]
    [InlineData(@"C:\Users\me\a\.nuxt\x.js")]
    [InlineData(@"C:\Users\me\a\coverage\x.html")]
    [InlineData(@"C:\Users\me\a\build")]
    public void Developer_folders_are_noise(string path)
    {
        Assert.True(FileResults.IsNoise(path, @"C:\Users\me"));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\devel\.agents\skills\readme.md")]
    [InlineData(@"C:\Users\me\.config\app\settings.json")]
    [InlineData(@"C:\Users\me\code\.cache")]
    [InlineData(@"C:\Users\me\code\.env")]
    public void Hidden_dot_items_anywhere_on_the_path_are_noise(string path)
    {
        Assert.True(FileResults.IsNoise(path, @"C:\Users\me"));
    }

    [Theory]
    [InlineData("resume", true, false, false, true)]
    [InlineData("weather", true, false, false, true)]
    [InlineData("pa", true, false, false, false)]
    [InlineData("pa", false, false, false, true)]
    [InlineData("b", false, false, false, false)]
    [InlineData("2+2", false, true, false, false)]
    [InlineData("sound", false, false, true, false)]
    [InlineData("budget", false, false, false, true)]
    public void When_files_are_searched(string text, bool command, bool calc, bool exactSetting, bool expected)
    {
        Assert.Equal(expected, FileResults.ShouldSearch(text, command, calc, exactSetting));
    }

    [Fact]
    public void File_rows_that_arrive_late_stay_under_the_selected_command()
    {
        var command = new PaletteItem("cmd:music", PaletteKind.Command, "Resume", "Media", 100) { Confident = true };
        var web = new PaletteItem("web", PaletteKind.Web, "Search", "", 0);
        var shown = PaletteRanker.Rank(new[] { command, web });
        Assert.Equal(0, shown.Selected);
        var file = new PaletteItem("file:C:\\cv\\Resume.pdf", PaletteKind.File, "Resume.pdf", "cv", 100);
        var merged = PaletteRanker.Stabilize(shown, PaletteRanker.Rank(new[] { command, web, file }));
        Assert.Equal(new[] { "cmd:music", "file:C:\\cv\\Resume.pdf", "web" }, merged.Items.Select(i => i.Key));
        Assert.Equal(0, merged.Selected);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Documents\Build Week\plan.docx")]
    [InlineData(@"C:\Users\me\Documents\binary notes.txt")]
    [InlineData(@"C:\Users\me\Documents\distribution.xlsx")]
    public void Similar_names_are_not_noise(string path)
    {
        Assert.False(FileResults.IsNoise(path, @"C:\Users\me"));
    }

    [Fact]
    public void The_query_excludes_developer_folders()
    {
        var sql = WindowsSearchSql.Build("main", Scope)!;
        foreach (var word in new[] { "node_modules", "AppData", "bin", "obj", "venv", "env", "__pycache__", "dist", "build", "target", "packages", "coverage" })
            Assert.Contains($"AND NOT CONTAINS(System.ItemFolderPathDisplay, '\"{word}\"')", sql);
    }

    [Fact]
    public void Scope_quotes_are_doubled()
    {
        Assert.Equal("file:C:/Users/O''Brien", WindowsSearchSql.ScopeUrl(@"C:\Users\O'Brien\"));
        Assert.Contains("SCOPE='file:C:/Users/O''Brien'", WindowsSearchSql.Build("doc", @"C:\Users\O'Brien"));
    }

    [Fact]
    public void Drops_noise_and_ranks_names()
    {
        const string profile = @"C:\Users\me";
        var hits = new[]
        {
            new FileHit(@"C:\Users\me\AppData\Local\x\budget.json", false),
            new FileHit(@"C:\Users\me\code\app\node_modules\budget\index.js", false),
            new FileHit(@"C:\Users\me\Documents\~$budget.xlsx", false),
            new FileHit(@"C:\Users\me\Documents\old budget notes.txt", false),
            new FileHit(@"C:\Users\me\Documents\Budget.xlsx", false),
            new FileHit(@"C:\Users\me\Documents\Budgets", true),
            new FileHit(@"C:\Users\me\code\api\bin\Debug\budget.dll", false),
            new FileHit(@"C:\Users\me\code\web\.next\budget.js", false),
            new FileHit(@"C:\Users\me\code\ml\.venv\budget.py", false),
            new FileHit(@"C:\Users\me\code\rs\target\budget", false),
        };
        var ranked = FileResults.Rank("budget", hits, profile);
        Assert.Equal(new[] { "Budget.xlsx", "Budgets", "old budget notes.txt" }, ranked.Select(r => Path.GetFileName(r.Hit.Path)));
    }
}

public class SettingsCatalogTests
{
    [Theory]
    [InlineData("brightness", SettingControl.Brightness)]
    [InlineData("bright", SettingControl.Brightness)]
    [InlineData("sound", SettingControl.Volume)]
    [InlineData("volume", SettingControl.Volume)]
    [InlineData("bluetooth", SettingControl.Bluetooth)]
    [InlineData("bluetooth settings", SettingControl.Bluetooth)]
    [InlineData("open bluetooth", SettingControl.Bluetooth)]
    public void Control_words_find_their_control(string text, SettingControl control)
    {
        Assert.Equal(control, SettingsCatalog.Find(text)[0].Entry.Control);
    }

    [Theory]
    [InlineData("wifi", "ms-settings:network-wifi")]
    [InlineData("wi-fi", "ms-settings:network-wifi")]
    [InlineData("display", "ms-settings:display")]
    [InlineData("night light", "ms-settings:nightlight")]
    [InlineData("battery", "ms-settings:batterysaver")]
    [InlineData("notifications", "ms-settings:notifications")]
    [InlineData("dark mode", "ms-settings:colors")]
    [InlineData("settings", "ms-settings:")]
    public void Plain_words_open_their_page(string text, string uri)
    {
        var top = SettingsCatalog.Find(text)[0];
        Assert.Equal(uri, top.Entry.Uri);
        Assert.Equal(100, top.Match);
    }

    [Fact]
    public void Prefix_and_later_word_matches_score_lower()
    {
        Assert.Equal(90, SettingsCatalog.Find("notif")[0].Match);
        Assert.Contains(SettingsCatalog.Find("light"), r => r.Entry.Uri == "ms-settings:nightlight" && r.Match == 70);
    }

    [Theory]
    [InlineData("")]
    [InlineData("b")]
    [InlineData("spotify")]
    [InlineData("how tall is everest")]
    public void Unrelated_text_finds_nothing(string text)
    {
        Assert.Empty(SettingsCatalog.Find(text));
    }

    [Fact]
    public void Every_page_is_a_settings_uri_and_has_keywords()
    {
        Assert.All(SettingsCatalog.All, e =>
        {
            Assert.StartsWith("ms-settings:", e.Uri);
            Assert.NotEmpty(e.Keywords);
        });
    }
}

public class PaletteQueryTests
{
    [Theory]
    [InlineData("pa", MediaControl.PlayPause, 80)]
    [InlineData("pause", MediaControl.PlayPause, 100)]
    [InlineData("next", MediaControl.Next, 100)]
    [InlineData("sk", MediaControl.Next, 80)]
    [InlineData("prev", MediaControl.Previous, 100)]
    public void Media_prefixes(string text, MediaControl control, int match)
    {
        Assert.Contains((control, match), PaletteQuery.MediaControls(text));
    }

    [Theory]
    [InlineData("p")]
    [InlineData("next week")]
    [InlineData("spotify")]
    public void Not_media(string text)
    {
        Assert.Empty(PaletteQuery.MediaControls(text));
    }

    [Theory]
    [InlineData("play blinding lights", "blinding lights")]
    [InlineData("put on bohemian rhapsody", "bohemian rhapsody")]
    [InlineData("tum hi ho bajao", "tum hi ho")]
    [InlineData("arijit ke gaane bajao", "arijit")]
    [InlineData("Arijit Singh Ke Ganye Bajau.", "Arijit Singh")]
    [InlineData("kesariya chalao", "kesariya")]
    [InlineData("tum hi ho song chala do", "tum hi ho")]
    [InlineData("kesariya sunao na", "kesariya")]
    public void Music_needs_a_play_intent(string text, string expected)
    {
        Assert.Equal(expected, PaletteQuery.MusicQuery(text));
    }

    [Theory]
    [InlineData("arijit singh")]
    [InlineData("Bohemian Rhapsody")]
    [InlineData("how to cook rice")]
    [InlineData("play store")]
    [InlineData("bajao")]
    [InlineData("2+2")]
    [InlineData("kal subah 7 baje ka alarm laga do")]
    [InlineData("lagao kesariya")]
    public void Not_music(string text)
    {
        Assert.Null(PaletteQuery.MusicQuery(text));
    }

    [Fact]
    public void Describes_productivity_commands()
    {
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        Assert.Equal("Start a 25 min timer", PaletteQuery.DescribeProductivity(ProductivityParser.Parse("timer 25m", now)!, now));
        var remind = ProductivityParser.Parse("remind me at 6pm to call mom", now)!;
        Assert.StartsWith("Reminder \u00B7 Today 6:00 PM", PaletteQuery.DescribeProductivity(remind, now));
        Assert.StartsWith("Save note", PaletteQuery.DescribeProductivity(ProductivityParser.Parse("note buy milk", now)!, now));
    }

    [Fact]
    public void A_reminder_without_text_has_no_dangling_separator()
    {
        var now = new DateTime(2026, 11, 3, 10, 0, 0);
        Assert.Equal("Reminder · Today 6:00 PM", PaletteQuery.DescribeProductivity(ProductivityParser.Parse("remind me at 6pm", now)!, now));
    }
}
