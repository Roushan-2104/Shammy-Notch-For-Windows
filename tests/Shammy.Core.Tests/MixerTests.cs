using Shammy.Core.Levels;
using Shammy.Core.Mixer;

namespace Shammy.Core.Tests;

public class MixerTests
{
    static MixerSource App(string exe, float level = 1f, bool muted = false, float peak = 0.5f, bool active = true,
        string? description = null, string? title = null, uint pid = 100) =>
        new()
        {
            ProcessId = pid,
            ProcessName = exe,
            Description = description,
            WindowTitle = title,
            Path = "C:/Apps/" + exe + ".exe",
            Active = active,
            Level = level,
            Muted = muted,
            Peak = peak,
        };

    static MixerSource SystemSounds(bool active = true) => new() { IsSystemSounds = true, Active = active, Level = 1f };

    static string[] Names(IEnumerable<MixerRow> rows) => rows.Select(r => r.Name).ToArray();

    // ---- Names -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("chrome", "Google Chrome", "Inbox - Gmail - Google Chrome", "Google Chrome")]
    [InlineData("Spotify", "Spotify", "Daft Punk - Around the World", "Spotify")]
    [InlineData("vlc", "VLC media player", "holiday.mp4 - VLC media player", "VLC media player")]
    [InlineData("firefox", "Firefox", null, "Firefox")]
    public void An_app_is_called_by_its_file_description(string exe, string description, string? title, string expected) =>
        Assert.Equal(expected, MixerNames.App(exe, description, title));

    [Theory]
    [InlineData("javaw", "Java(TM) Platform SE binary", "Minecraft 1.21", "Minecraft 1.21")]
    [InlineData("notes", "Electron", "Shopping list - Notes", "Notes")]
    [InlineData("player", null, "holiday.mp4 | Clip Player", "Clip Player")]
    [InlineData("hades", "", "Hades", "Hades")]
    public void A_runtime_or_missing_description_gives_way_to_the_window_titles_app(string exe, string? description, string title, string expected) =>
        Assert.Equal(expected, MixerNames.App(exe, description, title));

    [Theory]
    [InlineData("javaw", "Java(TM) Platform SE binary", null, "Java(TM) Platform SE binary")]
    [InlineData("vlc", null, null, "Vlc")]
    [InlineData("WhatsApp", "", "", "WhatsApp")]
    [InlineData("game.exe", null, "A very long window title that is plainly a document and not an app", "Game")]
    [InlineData(null, null, null, "App")]
    public void With_nothing_better_an_app_is_called_by_its_process(string? exe, string? description, string? title, string expected) =>
        Assert.Equal(expected, MixerNames.App(exe, description, title));

    [Theory]
    [InlineData("Inbox - Gmail - Google Chrome", "Google Chrome")]
    [InlineData("Chat | Microsoft Teams", "Microsoft Teams")]
    [InlineData("Minecraft 1.21", "Minecraft 1.21")]
    [InlineData("  ", null)]
    [InlineData("x", null)]
    public void The_app_is_the_last_part_of_a_window_title(string title, string? expected) =>
        Assert.Equal(expected, MixerNames.TitleApp(title));

    [Fact]
    public void A_window_title_may_use_a_long_dash()
    {
        string dash = ((char)0x2014).ToString();
        Assert.Equal("Mozilla Firefox", MixerNames.TitleApp("Video " + dash + " Mozilla Firefox"));
    }

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers")]
    [InlineData("Headphones (Airdopes 311 Pro)", "Airdopes 311 Pro")]
    [InlineData("Headset (Airdopes 311 Pro Hands-Free AG Audio)", "Airdopes 311 Pro")]
    [InlineData("Headphones (2- WH-1000XM4 Stereo)", "WH-1000XM4")]
    [InlineData("LG ULTRAGEAR (NVIDIA High Definition Audio)", "LG ULTRAGEAR")]
    [InlineData("Speakers (USB Audio Device)", "Speakers")]
    [InlineData("Line 1 (Virtual Cable)", "Line 1")]
    [InlineData("CABLE Input", "CABLE Input")]
    [InlineData("", "Output")]
    public void An_output_loses_its_driver_wrapping(string friendly, string expected) =>
        Assert.Equal(expected, MixerNames.Output(friendly));

    [Fact]
    public void Two_outputs_with_the_same_short_name_keep_what_tells_them_apart()
    {
        var names = MixerNames.Outputs(new[]
        {
            "Speakers (Realtek(R) Audio)", "Headphones (Airdopes 311 Pro)", "Headset (Airdopes 311 Pro Hands-Free AG Audio)",
        });
        string dot = ((char)0xB7).ToString();
        Assert.Equal(new[] { "Speakers", "Headphones " + dot + " Airdopes 311 Pro", "Headset " + dot + " Airdopes 311 Pro" }, names);
    }

    [Fact]
    public void A_long_label_is_cut_with_an_ellipsis()
    {
        Assert.Equal("Speakers", MixerNames.Fit("Speakers", 18));
        Assert.Equal("Logitech G560 Gam" + (char)0x2026, MixerNames.Fit("Logitech G560 Gaming Speaker", 18));
    }

    [Fact]
    public void The_switcher_lists_outputs_by_name_and_marks_the_current_one()
    {
        var outputs = MixerOutputs.Choose(new[]
        {
            ("b", "Speakers (Realtek(R) Audio)", true), ("a", "Headphones (Airdopes 311 Pro)", false),
        });
        Assert.Equal(new[] { new MixerOutput("a", "Airdopes 311 Pro", false), new MixerOutput("b", "Speakers", true) }, outputs);
    }

    [Fact]
    public void Choosing_another_output_does_not_reorder_the_switcher()
    {
        var before = MixerOutputs.Choose(new[] { ("b", "Speakers (Realtek(R) Audio)", true), ("a", "Headphones (Airdopes 311 Pro)", false) });
        var after = MixerOutputs.Choose(new[] { ("b", "Speakers (Realtek(R) Audio)", false), ("a", "Headphones (Airdopes 311 Pro)", true) });
        Assert.Equal(before.Select(o => o.Id), after.Select(o => o.Id));
    }

    [Fact]
    public void With_many_outputs_the_switcher_keeps_the_current_one()
    {
        var devices = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Zulu" }.Select(n => (n, n, n == "Zulu"));
        var outputs = MixerOutputs.Choose(devices);
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Zulu" }, outputs.Select(o => o.Label).ToArray());
        Assert.True(outputs[3].Current);
    }

    [Fact]
    public void No_outputs_means_an_empty_switcher() =>
        Assert.Empty(MixerOutputs.Choose(Array.Empty<(string, string, bool)>()));

    // ---- Rows ------------------------------------------------------------------------------------

    [Fact]
    public void Everything_comes_first_with_the_outputs_own_level()
    {
        var rows = new MixerBoard().Update(0.64f, false, new[] { App("spotify", 0.8f) });
        Assert.Equal(new MixerRow(MixerBoard.EverythingKey, "Everything", 64, false, MixerRowKind.Everything), rows[0]);
        Assert.Equal(new MixerRow("app:spotify", "Spotify", 80, false, MixerRowKind.App, "C:/Apps/spotify.exe"), rows[1]);
    }

    [Fact]
    public void Without_an_output_there_is_no_everything_row()
    {
        var rows = new MixerBoard().Update(null, false, new[] { App("spotify") });
        Assert.Equal(new[] { "Spotify" }, Names(rows));
    }

    [Fact]
    public void Only_apps_that_are_playing_are_listed()
    {
        var rows = new MixerBoard().Update(1f, false, new[] { App("spotify"), App("discord", active: false), SystemSounds(active: false) });
        Assert.Equal(new[] { "Everything", "Spotify" }, Names(rows));
    }

    [Fact]
    public void System_sounds_show_only_while_they_play_and_after_the_apps()
    {
        var board = new MixerBoard();
        var rows = board.Update(1f, false, new[] { SystemSounds(), App("spotify", peak: 0.1f) });
        Assert.Equal(new[] { "Everything", "Spotify", "System sounds" }, Names(rows));
        Assert.Equal(MixerRowKind.SystemSounds, rows[2].Kind);
        Assert.Equal(new[] { "Everything", "Spotify" }, Names(board.Update(1f, false, new[] { SystemSounds(active: false), App("spotify") })));
    }

    [Fact]
    public void An_apps_processes_share_one_row()
    {
        var rows = new MixerBoard().Update(1f, false, new[]
        {
            App("Discord", 0.4f, pid: 1, description: "Discord"),
            App("discord", 0.7f, pid: 2),
            App("discord", 0.2f, pid: 3, active: false),
        });
        var row = Assert.Single(rows, r => r.Kind == MixerRowKind.App);
        Assert.Equal("Discord", row.Name);
        // The highest setting among what is playing; the silent leftover is not counted.
        Assert.Equal(70, row.Percent);
    }

    [Fact]
    public void A_row_is_muted_only_when_everything_it_plays_is_muted()
    {
        var board = new MixerBoard();
        Assert.False(board.Update(1f, false, new[] { App("discord", muted: true, pid: 1), App("discord", pid: 2) })[1].Muted);
        Assert.True(board.Update(1f, false, new[] { App("discord", muted: true, pid: 1), App("discord", muted: true, pid: 2) })[1].Muted);
    }

    [Fact]
    public void A_process_without_a_name_gets_a_row_of_its_own()
    {
        var rows = new MixerBoard().Update(1f, false, new[]
        {
            new MixerSource { ProcessId = 7, Active = true, Level = 1f },
            new MixerSource { ProcessId = 8, Active = true, Level = 1f },
        });
        Assert.Equal(new[] { "pid:7", "pid:8" }, rows.Skip(1).Select(r => r.Key).ToArray());
    }

    [Fact]
    public void The_loudest_app_is_first_when_the_view_opens()
    {
        var rows = new MixerBoard().Update(1f, false, new[] { App("discord", peak: 0.1f), App("spotify", peak: 0.9f), App("chrome", peak: 0.4f) });
        Assert.Equal(new[] { "Everything", "Spotify", "Chrome", "Discord" }, Names(rows));
    }

    [Fact]
    public void Apps_about_as_loud_as_each_other_are_listed_by_name()
    {
        var rows = new MixerBoard().Update(1f, false, new[] { App("spotify", peak: 0.52f), App("chrome", peak: 0.49f) });
        Assert.Equal(new[] { "Everything", "Chrome", "Spotify" }, Names(rows));
    }

    [Fact]
    public void Rows_do_not_move_when_loudness_changes()
    {
        var board = new MixerBoard();
        board.Update(1f, false, new[] { App("spotify", peak: 0.9f), App("chrome", peak: 0.1f) });
        var rows = board.Update(1f, false, new[] { App("spotify", peak: 0f), App("chrome", peak: 1f) });
        Assert.Equal(new[] { "Everything", "Spotify", "Chrome" }, Names(rows));
    }

    [Fact]
    public void An_app_that_starts_playing_later_joins_at_the_end()
    {
        var board = new MixerBoard();
        board.Update(1f, false, new[] { App("spotify", peak: 0.2f), App("chrome", peak: 0.1f) });
        var rows = board.Update(1f, false, new[] { App("chrome"), App("vlc", peak: 1f), App("spotify") });
        Assert.Equal(new[] { "Everything", "Spotify", "Chrome", "Vlc" }, Names(rows));
    }

    [Fact]
    public void An_app_that_stops_leaves_the_others_in_place()
    {
        var board = new MixerBoard();
        board.Update(1f, false, new[] { App("spotify", peak: 0.9f), App("chrome", peak: 0.5f), App("vlc", peak: 0.1f) });
        var rows = board.Update(1f, false, new[] { App("spotify"), App("chrome", active: false), App("vlc") });
        Assert.Equal(new[] { "Everything", "Spotify", "Vlc" }, Names(rows));
    }

    [Fact]
    public void A_row_the_user_changed_stays_until_the_view_closes()
    {
        var board = new MixerBoard();
        board.Update(1f, false, new[] { App("spotify", peak: 0.9f), App("chrome", peak: 0.5f) });
        board.Keep("app:spotify");
        var silent = new[] { App("spotify", 0.3f, active: false), App("chrome") };
        var rows = board.Update(1f, false, silent);
        Assert.Equal(new[] { "Everything", "Spotify", "Chrome" }, Names(rows));
        Assert.Equal(30, rows[1].Percent);

        board.Clear();
        Assert.Equal(new[] { "Everything", "Chrome" }, Names(board.Update(1f, false, silent)));
    }

    [Fact]
    public void A_kept_row_goes_when_its_app_quits()
    {
        var board = new MixerBoard();
        board.Update(1f, false, new[] { App("spotify"), App("chrome") });
        board.Keep("app:spotify");
        Assert.Equal(new[] { "Everything", "Chrome" }, Names(board.Update(1f, false, new[] { App("chrome") })));
        Assert.False(board.IsKept("app:spotify"));
    }

    [Fact]
    public void Window_titles_are_left_out_while_the_screen_is_shared()
    {
        var sources = new[] { App("javaw", description: "Java(TM) Platform SE binary", title: "Minecraft 1.21") };
        Assert.Equal("Minecraft 1.21", new MixerBoard().Update(1f, false, sources)[1].Name);
        Assert.Equal("Java(TM) Platform SE binary", new MixerBoard().Update(1f, false, sources, useTitles: false)[1].Name);
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.004f, 0)]
    [InlineData(0.635f, 64)]
    [InlineData(1f, 100)]
    [InlineData(1.4f, 100)]
    [InlineData(-1f, 0)]
    [InlineData(float.NaN, 0)]
    public void A_level_is_shown_as_a_whole_number_up_to_100(float level, int expected) =>
        Assert.Equal(expected, MixerBoard.Percent(level));

    [Fact]
    public void A_row_is_found_by_its_name_or_part_of_it()
    {
        var rows = new MixerBoard().Update(0.5f, false, new[]
        {
            App("music", description: "YouTube Music"), App("youtube", description: "YouTube"), App("meet", description: "Google Meet"),
        });
        Assert.Equal("YouTube", MixerBoard.Find(rows, "youtube")?.Name);
        Assert.Equal("YouTube Music", MixerBoard.Find(rows, "youtube m")?.Name);
        Assert.Equal("Google Meet", MixerBoard.Find(rows, "meet")?.Name);
        Assert.Equal(MixerRowKind.Everything, MixerBoard.Find(rows, " Everything ")?.Kind);
        Assert.Equal(MixerRowKind.Everything, MixerBoard.Find(rows, "master")?.Kind);
        Assert.Null(MixerBoard.Find(rows, "spotify"));
        Assert.Null(MixerBoard.Find(rows, ""));
    }

    // ---- Phrases and the pipe --------------------------------------------------------------------

    [Theory]
    [InlineData("mixer")]
    [InlineData("Volume mixer")]
    [InlineData("app volume")]
    [InlineData("App volumes.")]
    [InlineData("open the volume mixer")]
    [InlineData("show  app volumes please")]
    [InlineData("Shammy, volume mixer")]
    public void Opens_the_volume_view(string text) => Assert.True(MixerPhrase.IsOpen(text));

    [Theory]
    [InlineData("volume 50")]
    [InlineData("volume up")]
    [InlineData("mute")]
    [InlineData("volume")]
    [InlineData("mixer grinder price")]
    [InlineData("best dj mixer")]
    [InlineData("app volume control windows 11")]
    [InlineData("")]
    public void Leaves_level_commands_and_searches_alone(string text) => Assert.False(MixerPhrase.IsOpen(text));

    [Theory]
    [InlineData("mixer")]
    [InlineData("volume mixer")]
    [InlineData("app volume")]
    [InlineData("app volumes")]
    public void The_level_commands_do_not_take_the_mixers_phrases(string text) => Assert.Null(LevelPhrase.Parse(text));

    [Fact]
    public void A_set_line_is_a_name_then_a_level()
    {
        Assert.Equal(("YouTube Music", 40), MixerPhrase.ParseSet("YouTube Music 40"));
        Assert.Equal(("Everything", 100), MixerPhrase.ParseSet("  Everything   100% "));
        Assert.Equal(("Left 4 Dead 2", 0), MixerPhrase.ParseSet("Left 4 Dead 2 0"));
        Assert.Null(MixerPhrase.ParseSet("YouTube Music"));
        Assert.Null(MixerPhrase.ParseSet("Chrome 101"));
        Assert.Null(MixerPhrase.ParseSet("Chrome -5"));
        Assert.Null(MixerPhrase.ParseSet("40"));
        Assert.Null(MixerPhrase.ParseSet(""));
    }

    [Fact]
    public void A_mute_line_is_a_name_then_on_or_off()
    {
        Assert.Equal(("Google Meet", true), MixerPhrase.ParseMute("Google Meet on"));
        Assert.Equal(("Game", false), MixerPhrase.ParseMute("Game OFF"));
        Assert.Null(MixerPhrase.ParseMute("Game"));
        Assert.Null(MixerPhrase.ParseMute("Game maybe"));
        Assert.Null(MixerPhrase.ParseMute("on"));
    }

    [Fact]
    public void The_state_is_one_line_of_output_master_and_rows()
    {
        var rows = new MixerBoard().Update(0.64f, false, new[]
        {
            App("music", 0.8f, description: "YouTube Music", peak: 0.9f),
            App("meet", 1f, description: "Google Meet", peak: 0.5f),
            App("game", 0.35f, muted: true, peak: 0f),
        });
        Assert.Equal("output=Speakers; master=64; YouTube Music=80; Google Meet=100; Game=35,muted", MixerPhrase.State("Speakers", rows));
    }

    [Fact]
    public void The_state_says_when_there_is_no_output_or_the_output_is_muted()
    {
        Assert.Equal("output=-; master=-", MixerPhrase.State(null, new MixerBoard().Update(null, false, Array.Empty<MixerSource>())));
        Assert.Equal("output=Speakers; master=20,muted", MixerPhrase.State("Speakers", new MixerBoard().Update(0.2f, true, Array.Empty<MixerSource>())));
    }

    [Fact]
    public void Rows_can_be_written_the_way_the_state_writes_them()
    {
        var rows = MixerPhrase.ParseRows("Everything=64; YouTube Music=80; Game=35,muted;");
        Assert.Equal(new[] { new MixerEntry("Everything", 64, false), new MixerEntry("YouTube Music", 80, false), new MixerEntry("Game", 35, true) }, rows);
        Assert.Null(MixerPhrase.ParseRows("Game"));
        Assert.Null(MixerPhrase.ParseRows("Game=loud"));
        Assert.Null(MixerPhrase.ParseRows("Game=140"));
        Assert.Null(MixerPhrase.ParseRows("Game=40,quiet"));
        Assert.Null(MixerPhrase.ParseRows("  "));
    }
}
