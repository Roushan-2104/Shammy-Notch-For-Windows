using System.Text.Json;
using Shammy.Core.Music;

namespace Shammy.Core.Tests;

public class LrcTests
{
    static TimeSpan T(int min, double sec) => TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);

    [Fact]
    public void Parses_timestamps_in_all_common_precisions()
    {
        var lines = Lrc.Parse("[00:01]one\n[00:02.5]two\n[00:03.25]three\n[00:04.125]four\n[01:05:50]five");
        Assert.Equal(new[] { "one", "two", "three", "four", "five" }, lines.Select(l => l.Text));
        Assert.Equal(T(0, 1), lines[0].Time);
        Assert.Equal(T(0, 2.5), lines[1].Time);
        Assert.Equal(T(0, 3.25), lines[2].Time);
        Assert.Equal(T(0, 4.125), lines[3].Time);
        Assert.Equal(T(1, 5.5), lines[4].Time);
    }

    [Fact]
    public void A_line_with_several_timestamps_repeats_in_time_order()
    {
        var lines = Lrc.Parse("[00:10.00]verse\n[00:05.00][00:20.00]chorus\n[00:15.00]bridge");
        Assert.Equal(new[] { "chorus", "verse", "bridge", "chorus" }, lines.Select(l => l.Text));
        Assert.Equal(new[] { T(0, 5), T(0, 10), T(0, 15), T(0, 20) }, lines.Select(l => l.Time));
    }

    [Fact]
    public void Skips_metadata_blank_lines_and_untimed_text_but_keeps_timed_breaks()
    {
        var lrc = "[ar:Someone]\r\n[ti:Song]\r\n\r\n   \r\n[00:01.00]first\r\nnot timed\r\n[00:02.00]\r\n[00:03.00]  third  \r\n";
        var lines = Lrc.Parse(lrc);
        Assert.Equal(new[] { "first", "", "third" }, lines.Select(l => l.Text));
    }

    [Fact]
    public void Offset_shifts_lines_earlier_and_never_below_zero()
    {
        var lines = Lrc.Parse("[offset:+500]\n[00:00.20]a\n[00:10.00]b");
        Assert.Equal(TimeSpan.Zero, lines[0].Time);
        Assert.Equal(T(0, 9.5), lines[1].Time);

        var later = Lrc.Parse("[offset:-250]\n[00:10.00]b");
        Assert.Equal(T(0, 10.25), later[0].Time);
    }

    [Fact]
    public void Strips_word_level_timing()
    {
        var lines = Lrc.Parse("[00:01.00]<00:01.00>hello <00:01.50>world");
        Assert.Equal("hello world", lines.Single().Text);
    }

    [Fact]
    public void Empty_or_null_input_gives_no_lines()
    {
        Assert.Empty(Lrc.Parse(null));
        Assert.Empty(Lrc.Parse(""));
        Assert.Empty(Lrc.Parse("plain lyrics\nwithout times"));
    }

    [Fact]
    public void IndexAt_finds_the_line_being_sung()
    {
        var lines = Lrc.Parse("[00:05.00]a\n[00:10.00]b\n[00:15.00]c");
        Assert.Equal(-1, Lrc.IndexAt(lines, T(0, 1)));
        Assert.Equal(0, Lrc.IndexAt(lines, T(0, 5)));
        Assert.Equal(0, Lrc.IndexAt(lines, T(0, 9.99)));
        Assert.Equal(1, Lrc.IndexAt(lines, T(0, 10)));
        Assert.Equal(2, Lrc.IndexAt(lines, T(3, 0)));
        Assert.Equal(-1, Lrc.IndexAt(Array.Empty<LyricLine>(), T(0, 3)));
    }
}

public class MusicCommandTests
{
    [Theory]
    [InlineData("Play Kaahe Mose", MusicAction.PlayYouTubeMusic, "Kaahe Mose")]
    [InlineData("play bohemian rhapsody by queen.", MusicAction.PlayYouTubeMusic, "bohemian rhapsody by queen")]
    [InlineData("Play Tum Hi Ho on YouTube Music", MusicAction.PlayYouTubeMusic, "Tum Hi Ho")]
    [InlineData("play blinding lights on youtube", MusicAction.PlayYouTubeMusic, "blinding lights")]
    [InlineData("Put on some Arijit Singh", MusicAction.PlayYouTubeMusic, "some Arijit Singh")]
    [InlineData("Play Levitating on Spotify", MusicAction.PlaySpotify, "Levitating")]
    [InlineData("play levitating in spotify please", MusicAction.PlaySpotify, "levitating")]
    [InlineData("Play \"Yellow\" by Coldplay", MusicAction.PlayYouTubeMusic, "\"Yellow\" by Coldplay")]
    [InlineData("play  Kesariya ", MusicAction.PlayYouTubeMusic, "Kesariya")]
    [InlineData("play \u201CKesariya\u201D", MusicAction.PlayYouTubeMusic, "Kesariya")]
    public void Play_commands_extract_the_query(string text, MusicAction action, string query)
    {
        var intent = MusicCommand.Parse(text);
        Assert.NotNull(intent);
        Assert.Equal(action, intent!.Action);
        Assert.Equal(query, intent.Query);
    }

    [Theory]
    [InlineData("pause", MusicAction.Pause)]
    [InlineData("Pause music", MusicAction.Pause)]
    [InlineData("Resume.", MusicAction.Resume)]
    [InlineData("play", MusicAction.Resume)]
    [InlineData("Play!", MusicAction.Resume)]
    [InlineData("next", MusicAction.Next)]
    [InlineData("Next song", MusicAction.Next)]
    [InlineData("skip", MusicAction.Next)]
    [InlineData("previous", MusicAction.Previous)]
    [InlineData("Previous song", MusicAction.Previous)]
    [InlineData("Go back a song", MusicAction.Previous)]
    [InlineData("lyrics", MusicAction.ShowLyrics)]
    [InlineData("Show lyrics", MusicAction.ShowLyrics)]
    public void Transport_words_match_only_as_the_whole_text(string text, MusicAction action)
    {
        Assert.Equal(action, MusicCommand.Parse(text)?.Action);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("next week weather")]
    [InlineData("pause meaning in hindi")]
    [InlineData("skip the line tickets")]
    [InlineData("lyrics of tum hi ho")]
    [InlineData("previous president of india")]
    [InlineData("how to play chess")]
    [InlineData("playing cards")]
    [InlineData("play store")]
    [InlineData("Play Station 5 price")]
    [InlineData("play on spotify")]
    [InlineData("play spotify")]
    [InlineData("display settings")]
    [InlineData("resume template")]
    public void Ordinary_searches_are_not_music_commands(string text)
    {
        Assert.Null(MusicCommand.Parse(text));
    }
}

public class YouTubeMusicTests
{
    // Trimmed from a real WEB_REMIX search response ("Kaahe Mose", songs filter), keeping the shape.
    const string Response = """
    {"contents":{"tabbedSearchResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"sectionListRenderer":{"contents":[
      {"itemSectionRenderer":{"contents":[{"messageRenderer":{"text":{"runs":[{"text":"Showing results for"}]}}}]}},
      {"musicShelfRenderer":{"contents":[
        {"musicResponsiveListItemRenderer":{
          "overlay":{"musicItemThumbnailOverlayRenderer":{"content":{"musicPlayButtonRenderer":{"playNavigationEndpoint":{"watchEndpoint":{"videoId":"M4bYA-xuZ6I"}}}}}},
          "flexColumns":[
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Kaahe Mose","navigationEndpoint":{"watchEndpoint":{"videoId":"M4bYA-xuZ6I"}}}]}}},
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Garvit Soni"},{"text":", "},{"text":"Priyansh Srivastava"},{"text":" & "},{"text":"Garvit-Priyansh"},{"text":" \u2022 "},{"text":"Kaahe Mose"},{"text":" \u2022 "},{"text":"3:40"}]}}},
            {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"17M plays"}]}}}],
          "playlistItemData":{"videoId":"M4bYA-xuZ6I"}}},
        {"musicResponsiveListItemRenderer":{
          "flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Tose Naina"}]}}}],
          "playlistItemData":{"videoId":"qq1LxHVnti4"}}}
      ]}}
    ]}}}}]}}}
    """;

    [Fact]
    public void Finds_the_first_song_with_title_and_artist()
    {
        var song = YouTubeMusic.ParseSongs(Response).FirstOrDefault();
        Assert.NotNull(song);
        Assert.Equal("M4bYA-xuZ6I", song!.VideoId);
        Assert.Equal("Kaahe Mose", song.Title);
        Assert.Equal("Garvit Soni, Priyansh Srivastava & Garvit-Priyansh", song.Artist);
    }

    [Fact]
    public void Drops_the_type_prefix_of_unfiltered_results()
    {
        const string json = """
        {"a":[{"musicResponsiveListItemRenderer":{"flexColumns":[
          {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Yellow"}]}}},
          {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Song"},{"text":" \u2022 "},{"text":"Coldplay"},{"text":" \u2022 "},{"text":"4:29"}]}}}],
          "overlay":{"x":{"watchEndpoint":{"videoId":"yKNxeF4KMsY"}}}}}]}
        """;
        var song = YouTubeMusic.ParseSongs(json).FirstOrDefault();
        Assert.Equal(new YouTubeMusicSong("yKNxeF4KMsY", "Yellow", "Coldplay"), song);
    }

    [Fact]
    public void Falls_back_to_any_video_id_and_survives_junk()
    {
        Assert.Equal("abc", YouTubeMusic.ParseSongs("""{"x":{"watchEndpoint":{"videoId":"abc"}}}""").FirstOrDefault()?.VideoId);
        Assert.Null(YouTubeMusic.ParseSongs("""{"contents":{}}""").FirstOrDefault());
        Assert.Null(YouTubeMusic.ParseSongs("<html>not json").FirstOrDefault());
    }

    [Fact]
    public void Request_body_escapes_the_query_and_asks_for_songs()
    {
        using var doc = JsonDocument.Parse(YouTubeMusic.BuildSearchBody("say \"hi\" \u0915\u093E"));
        var root = doc.RootElement;
        Assert.Equal("say \"hi\" \u0915\u093E", root.GetProperty("query").GetString());
        Assert.Equal("WEB_REMIX", root.GetProperty("context").GetProperty("client").GetProperty("clientName").GetString());
        Assert.Equal(YouTubeMusic.SongsFilter, root.GetProperty("params").GetString());
    }

    [Fact]
    public void Urls_are_escaped()
    {
        Assert.Equal("https://music.youtube.com/watch?v=M4bYA-xuZ6I", YouTubeMusic.WatchUrl("M4bYA-xuZ6I"));
        Assert.Equal("https://music.youtube.com/search?q=tum%20hi%20ho%20%26%20more", YouTubeMusic.SearchPageUrl("tum hi ho & more"));
    }
}

public class LrclibTests
{
    [Fact]
    public void Get_url_includes_rounded_duration_only_when_known()
    {
        Assert.Equal("https://lrclib.net/api/get?track_name=Kaahe%20Mose&artist_name=Garvit%20Soni&duration=220",
            Lrclib.GetUrl("Kaahe Mose", "Garvit Soni", TimeSpan.FromSeconds(219.6)));
        Assert.DoesNotContain("duration", Lrclib.GetUrl("a", "b", TimeSpan.Zero));
    }

    [Theory]
    [InlineData("Kesariya", "Arijit Singh", true)]
    [InlineData("Kesariya", "", false)]   // a browser tab with no artist: LRCLIB answers 400
    [InlineData("Kesariya", "  ", false)]
    [InlineData("", "Arijit Singh", false)]
    public void Exact_lookup_needs_a_title_and_an_artist(string track, string artist, bool can) =>
        Assert.Equal(can, Lrclib.CanGet(track, artist));

    [Theory]
    [InlineData("Kaahe Mose", "Garvit Soni, Priyansh Srivastava & Garvit-Priyansh", "Kaahe Mose", "Garvit Soni")]
    [InlineData("Coldplay - Yellow (Official Video)", "Coldplay", "Yellow", "Coldplay")]
    [InlineData("Yellow [Remastered 2011]", "Coldplay - Topic", "Yellow", "Coldplay")]
    [InlineData("Rockabye (feat. Sean Paul & Anne-Marie)", "Clean Bandit", "Rockabye", "Clean Bandit")]
    [InlineData("Arijit Singh - Kesariya (Lyrics)", "", "Kesariya", "Arijit Singh")]
    [InlineData("Hello", "AdeleVEVO", "Hello", "Adele")]
    public void Clean_reduces_video_titles_to_track_and_lead_artist(string title, string artist, string track, string lead)
    {
        Assert.Equal((track, lead), Lrclib.Clean(title, artist));
    }

    [Fact]
    public void Hostile_long_titles_are_cleaned_quickly()
    {
        var title = "(" + string.Concat(Enumerable.Repeat("a official ", 3000));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Lrclib.Clean(title, new string('x', 30000));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Parses_get_objects_and_search_arrays()
    {
        const string get = """{"id":1,"trackName":"A","artistName":"B","duration":219.0,"instrumental":false,"plainLyrics":"x","syncedLyrics":"[00:01.00]x"}""";
        var one = Lrclib.ParseRecords(get)!.Single();
        Assert.Equal(new LrclibRecord("A", "B", 219, false, "x", "[00:01.00]x"), one);

        const string search = """[{"trackName":"A","duration":200,"plainLyrics":null,"syncedLyrics":null,"instrumental":true},{"trackName":"C","duration":201,"plainLyrics":"y","syncedLyrics":null}]""";
        Assert.Equal(2, Lrclib.ParseRecords(search)!.Count);

        Assert.Empty(Lrclib.ParseRecords("""{"code":404,"name":"TrackNotFound","message":"Failed to find specified track"}""")!);
        // Not JSON (a Wi-Fi login page): unknown, so it's retried rather than cached as "no lyrics".
        Assert.Null(Lrclib.ParseRecords("oops"));
        Assert.Null(Lrclib.ParseRecords("<html><body>Sign in to Airport Wi-Fi</body></html>"));
        Assert.Null(Lrclib.ParseRecords("42"));
    }

    [Fact]
    public void A_chat_app_is_not_music()
    {
        // WhatsApp's web app: "playing" a track titled "WhatsApp", no artist (its id is Chrome's, so the title decides).
        Assert.True(NotMusic.Is("Chrome._crx_hnpfjngllnfapefoaidbinmjnm", "WhatsApp", ""));
        Assert.True(NotMusic.Is("5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App", "Voice message", "Riya"));
        Assert.True(NotMusic.Is(null, " telegram ", null));
        Assert.True(NotMusic.Is("Chrome", "(3) WhatsApp", ""));                    // unread messages in the page title
        Assert.False(NotMusic.Is("Chrome", "(3) Some Song", ""));
        Assert.False(NotMusic.Is("Chrome", "WhatsApp", "Seyi Vibez"));             // a real song called that
        Assert.False(NotMusic.Is("Spotify.exe", "Kho Sa Gaya Hoon", "OutStation"));
        Assert.False(NotMusic.Is("ZoomPlayer", "Movie", ""));                      // Zoom Player is a media player
    }

    [Fact]
    public void A_bare_title_is_not_worth_a_lyrics_search()
    {
        Assert.False(Lrclib.WorthSearching("WhatsApp", "", TimeSpan.Zero));
        Assert.False(Lrclib.WorthSearching("WhatsApp", "", TimeSpan.FromSeconds(135)));
        Assert.False(Lrclib.WorthSearching("Some Tab", "", TimeSpan.Zero));
        Assert.False(Lrclib.WorthSearching("", "Arijit Singh", TimeSpan.Zero));
        Assert.True(Lrclib.WorthSearching("Tum Hi Ho", "Arijit Singh", TimeSpan.Zero));
        Assert.True(Lrclib.WorthSearching("Tum Hi Ho", "", TimeSpan.FromSeconds(262)));
    }

    [Fact]
    public void Search_results_must_be_able_to_be_this_track()
    {
        var right = new LrclibRecord("Tum Hi Ho", "Arijit Singh", 262, false, null, "[00:01.00] a");
        var other = new LrclibRecord("Tum Se Hi", "Mohit Chauhan", 262, false, null, "[00:01.00] b");
        var longer = new LrclibRecord("Tum Hi Ho (Unplugged)", "Arijit Singh", 320, false, null, "[00:01.00] c");
        var all = new[] { other, right, longer };

        Assert.Equal(new[] { right, longer }, Lrclib.SearchMatches(all, "Tum Hi Ho", "Arijit Singh", TimeSpan.FromSeconds(262)));
        // No artist to check: the length has to be close as well.
        Assert.Equal(new[] { right }, Lrclib.SearchMatches(all, "tum hi ho!", "", TimeSpan.FromSeconds(270)));
        Assert.Empty(Lrclib.SearchMatches(all, "Tum Hi Ho", "", TimeSpan.Zero));
        Assert.Empty(Lrclib.SearchMatches(all, "", "Arijit Singh", TimeSpan.Zero));
    }

    [Fact]
    public void PickBest_prefers_synced_lyrics_with_a_matching_duration()
    {
        var plain = new LrclibRecord("S", "A", 220, false, "words", null);
        var syncedRight = new LrclibRecord("S", "A", 221, false, "words", "[00:01.00]words");
        var syncedLive = new LrclibRecord("S (Live)", "A", 300, false, "words", "[00:01.00]words");
        var empty = new LrclibRecord("S", "A", 220, false, null, null);

        Assert.Same(syncedRight, Lrclib.PickBest(new[] { empty, plain, syncedLive, syncedRight }, TimeSpan.FromSeconds(220)));
        Assert.Same(plain, Lrclib.PickBest(new[] { syncedLive, plain }, TimeSpan.FromSeconds(220)));
        Assert.Same(syncedLive, Lrclib.PickBest(new[] { plain, syncedLive }, TimeSpan.Zero));
        Assert.Null(Lrclib.PickBest(new[] { empty }, TimeSpan.Zero));
    }

    [Fact]
    public void ToLyrics_falls_back_from_synced_to_plain_to_instrumental()
    {
        var synced = Lrclib.ToLyrics(new LrclibRecord("S", "A", 1, false, "a", "[00:01.00]a\n[00:02.00]b"));
        Assert.Equal(LyricsKind.Synced, synced.Kind);
        Assert.Equal(2, synced.Lines.Count);

        Assert.Equal(LyricsKind.Plain, Lrclib.ToLyrics(new LrclibRecord("S", "A", 1, false, " a\nb ", "no timestamps here")).Kind);
        Assert.Equal(LyricsKind.Instrumental, Lrclib.ToLyrics(new LrclibRecord("S", "A", 1, true, null, null)).Kind);
        Assert.Equal(LyricsKind.None, Lrclib.ToLyrics(null).Kind);
    }
}

public class MusicGestureTests
{
    static readonly DateTime T0 = new(2026, 1, 1);

    [Fact]
    public void Swipe_fires_once_per_gesture_past_the_threshold()
    {
        var s = new SwipeDetector();
        Assert.Equal(0, s.Add(40, T0));
        Assert.Equal(0, s.Add(40, T0.AddMilliseconds(16)));
        Assert.Equal(1, s.Add(60, T0.AddMilliseconds(32)));
        // The rest of the same swipe (momentum) does nothing.
        Assert.Equal(0, s.Add(200, T0.AddMilliseconds(48)));
        Assert.Equal(0, s.Add(200, T0.AddMilliseconds(200)));
    }

    [Fact]
    public void Swipe_left_is_previous_and_cooldown_blocks_quick_repeats()
    {
        var s = new SwipeDetector();
        Assert.Equal(-1, s.Add(-150, T0));
        // A new gesture (after a quiet gap) but still inside the 600 ms cooldown.
        Assert.Equal(0, s.Add(-150, T0.AddMilliseconds(400)));
        // Another fresh gesture once the cooldown is over.
        Assert.Equal(-1, s.Add(-150, T0.AddMilliseconds(700)));
    }

    [Fact]
    public void Swipe_direction_change_restarts_the_count()
    {
        var s = new SwipeDetector();
        Assert.Equal(0, s.Add(100, T0));
        Assert.Equal(0, s.Add(-100, T0.AddMilliseconds(16)));
        Assert.Equal(-100, s.Accumulated);
        Assert.Equal(-1, s.Add(-30, T0.AddMilliseconds(32)));
    }

    [Fact]
    public void Wheel_stepper_turns_partial_deltas_into_whole_notches()
    {
        var w = new WheelStepper();
        Assert.Equal(1, w.Add(120));
        Assert.Equal(-2, w.Add(-240));
        Assert.Equal(0, w.Add(60));
        Assert.Equal(1, w.Add(60));
        Assert.Equal(0, w.Add(-60));
        Assert.Equal(0, w.Add(30)); // reversing discards the partial notch
        Assert.Equal(0, w.Add(0));
    }
}
