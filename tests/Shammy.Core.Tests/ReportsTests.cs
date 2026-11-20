using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Shammy.Core.Reports;

namespace Shammy.Core.Tests;

public class ReportTextTests
{
    [Theory]
    [InlineData("mail a.b@c.com now", "mail ‹email› now")]
    [InlineData("see https://x.com/a?b=1 now", "see ‹link› now")]
    [InlineData(@"Settings loaded from C:\Users\Someone\AppData\Local\Shammy\settings.json", "Settings loaded from ‹path›")]
    [InlineData(@"share \\server\folder\x.txt gone", "share ‹path› gone")]
    [InlineData("call 98765 43210 or OTP 482913", "call ‹number› or OTP ‹number›")]
    [InlineData("card 4111-1111-1111-1111", "card ‹number›")]
    public void Removes_what_could_identify_someone(string text, string cleaned) =>
        Assert.Equal(cleaned, ReportText.Clean(text));

    [Theory]
    [InlineData("20:31:52.120 [INFO] Typed 6 words, 41 KB sent")]
    [InlineData("Shammy 1.5.3 starting on 2026-11-20 at 20:31")]
    [InlineData("2026-11-20 20:31:52 ok, 120 ms")]
    public void Keeps_times_dates_versions_and_small_counts(string text) =>
        Assert.Equal(text, ReportText.Clean(text));

    [Fact]
    public void Removes_the_user_and_pc_names_anywhere_longest_first()
    {
        var cleaned = ReportText.Clean("Signed in as Roushan Sharma on ROUSHAN-PC; roushan said hi", new[] { "Roushan", "Roushan Sharma", "ROUSHAN-PC" });
        Assert.Equal("Signed in as ‹name› on ‹name›; ‹name› said hi", cleaned);
    }

    [Theory]
    [InlineData(@"Settings loaded from C:\Users\Jane Doe\AppData\Local\Shammy\settings.json now", "Settings loaded from ‹path› now")]
    [InlineData(@"at Shammy.X() in C:\Users\Jane Doe\src\X.cs:line 12", "at Shammy.X() in ‹path›:line 12")]
    public void A_path_with_spaces_goes_whole(string text, string cleaned) =>
        Assert.Equal(cleaned, ReportText.Clean(text));

    [Fact]
    public void A_name_inside_a_path_leaves_nothing_behind()
    {
        var cleaned = ReportText.Clean(@"Loaded C:\Users\Jane Doe\a.json for Doe", new[] { "Jane Doe", "Jane", "Doe" });
        Assert.DoesNotContain("Doe", cleaned);
        Assert.DoesNotContain("Jane", cleaned);
    }

    [Fact]
    public void Text_that_looks_like_the_cleaners_own_markers_is_harmless()
    {
        Assert.Equal("a 7 b", ReportText.Clean("a \u00017\u0002 b"));
        Assert.Equal("at 10:30 ‹number› ok", ReportText.Clean("at 10:30 \u000199999999999\u0002 ok"));
    }

    [Fact]
    public void Counts_what_was_removed_in_words()
    {
        var lines = new[] { "a ‹path› b ‹path›", "‹name› and ‹number›" };
        Assert.Equal("1 name, 2 file paths, 1 long number", ReportText.RemovedInWords(ReportText.Removed(lines)));
        Assert.Equal("", ReportText.RemovedInWords(ReportText.Removed(new[] { "nothing here" })));
    }
}

public class ProblemTests
{
    const string RuntimeEvent = """
        Application: Shammy.exe
        CoreCLR Version: 8.0.1125.51716
        .NET Version: 8.0.11
        Description: The process was terminated due to an unhandled exception.
        Exception Info: System.AccessViolationException: Attempted to read or write protected memory. This is often an indication that other memory is corrupt.
           at NAudio.Wave.WaveInEvent.DoRecording()
           at NAudio.Wave.WaveInEvent.RecordThread()
           at System.Threading.Thread.StartCallback()
        """;

    [Fact]
    public void Reads_a_dotnet_runtime_crash_event()
    {
        var stack = CrashStack.FromRuntimeEvent(RuntimeEvent)!;
        Assert.Equal("System.AccessViolationException", stack.Type);
        Assert.Equal("AccessViolationException", stack.ShortType);
        Assert.StartsWith("Attempted to read", stack.Message);
        Assert.Equal("NAudio.Wave.WaveInEvent.DoRecording", stack.Frames[0]);

        var place = ProblemPlace.Find(stack.Frames);
        Assert.Equal("Voice › Microphone", place.AreaName);
        Assert.Equal(ProblemPlace.Dictation, place.FeatureKey);
        Assert.Equal("WaveInEvent.DoRecording", place.Where);
    }

    [Fact]
    public void Reads_a_native_crash_event()
    {
        var stack = CrashStack.FromNativeCrash("wdmaud.drv", "C0000005");
        Assert.Equal("NativeCrash.0xc0000005", stack.Type);
        Assert.Equal("NativeCrash.0xc0000005", CrashStack.FromNativeCrash("x.dll", "0xC0000005").Type);
        Assert.Equal(ProblemPlace.Dictation, ProblemPlace.Find(stack.Frames).FeatureKey);
    }

    [Fact]
    public void The_innermost_exception_is_the_cause()
    {
        var stack = CrashStack.FromText("""
            System.Reflection.TargetInvocationException: Exception has been thrown. ---> System.InvalidOperationException: Bad state
               at Shammy.Modules.Music.LyricsSource.Pick()
               --- End of inner exception stack trace ---
               at System.RuntimeMethodHandle.InvokeMethod()
            """)!;
        Assert.Equal("System.InvalidOperationException", stack.Type);
        Assert.Equal(new[] { "Shammy.Modules.Music.LyricsSource.Pick" }, stack.Frames);
    }

    [Fact]
    public void A_module_frame_names_the_module_and_can_be_paused()
    {
        var place = ProblemPlace.Find(new[] { "System.Linq.Enumerable.First", "Shammy.Modules.ClipboardHistory.ClipImages.Process" });
        Assert.Equal("Clipboard history", place.AreaName);
        Assert.Equal("Shammy.Modules.ClipboardHistory", ProblemPlace.ModuleNamespace(place.FeatureKey!));
        Assert.Equal("ClipImages.Process", place.Where);
    }

    [Fact]
    public void A_shared_module_helper_doesnt_decide_the_next_frame_does()
    {
        var place = ProblemPlace.Find(new[] { "Shammy.Modules.ModuleContext.Save", "Shammy.Modules.Music.LyricsSource.Pick" });
        Assert.Equal("module:Shammy.Modules.Music", place.FeatureKey);
        Assert.Null(ProblemPlace.Find(new[] { "Shammy.Modules.ModuleContext.Save" }).FeatureKey);
    }

    [Fact]
    public void A_nested_folder_or_a_lambda_belongs_to_its_module_folder()
    {
        Assert.Equal("module:Shammy.Modules.Productivity",
            ProblemPlace.Find(new[] { "Shammy.Modules.Productivity.Alarms.AlarmsDetail.Build" }).FeatureKey);
        Assert.Equal("module:Shammy.Modules.Music",
            ProblemPlace.Find(new[] { "Shammy.Modules.Music.LyricsSource.<GetAsync>d__12.MoveNext" }).FeatureKey);
    }

    [Fact]
    public void Settings_and_the_island_are_never_paused()
    {
        Assert.Null(ProblemPlace.Find(new[] { "Shammy.Modules.Settings.SettingsView.Build" }).FeatureKey);
        Assert.Null(ProblemPlace.Find(new[] { "Shammy.Shell.NotchWindow.OnRender" }).FeatureKey);
        Assert.Equal("Shammy", ProblemPlace.Find(new[] { "System.Windows.Threading.Dispatcher.Invoke" }).AreaName);
    }

    [Fact]
    public void The_same_problem_in_the_next_build_has_the_same_signature()
    {
        var place = ProblemPlace.Find(new[] { "Shammy.Modules.Music.LyricsSource.<GetAsync>d__12.MoveNext" });
        var a = ProblemRules.Signature("error", place, "HttpRequestException", "Looking up lyrics failed");
        var b = ProblemRules.Signature("error", place with { Where = "<GetAsync>d__14.MoveNext" }, "HttpRequestException", "Looking up lyrics failed");
        Assert.Equal(a, b);
        Assert.Matches("^error/music/[0-9a-f]{6}$", a);
        Assert.NotEqual(a, ProblemRules.Signature("error", place, "JsonException", "Looking up lyrics failed"));
    }

    [Fact]
    public void Network_blips_are_not_problems_but_server_errors_are()
    {
        Assert.True(ProblemRules.IsNetworkBlip(new HttpRequestException(HttpRequestError.NameResolutionError)));
        Assert.True(ProblemRules.IsNetworkBlip(new TaskCanceledException()));
        Assert.True(ProblemRules.IsNetworkBlip(new IOException("x", new SocketException())));
        Assert.False(ProblemRules.IsNetworkBlip(new HttpRequestException("400 (Bad Request)", null, System.Net.HttpStatusCode.BadRequest)));
        Assert.False(ProblemRules.IsNetworkBlip(new InvalidOperationException()));
    }
}

public class ReportsStateTests
{
    static readonly DateOnly Today = new(2026, 11, 20);

    [Fact]
    public void An_error_is_offered_once_at_five_in_a_day()
    {
        var state = new ReportsState();
        var offered = Enumerable.Range(0, 8).Select(_ => state.CountError("error/music/abc123", Today, "1.5.3")).ToList();
        Assert.Equal(new[] { false, false, false, false, true, false, false, false }, offered);
        // The next day counts from zero again.
        Assert.False(state.CountError("error/music/abc123", Today.AddDays(1), "1.5.3"));
        Assert.Equal(1, state.TodayCount("error/music/abc123", Today.AddDays(1)));
    }

    [Fact]
    public void An_answered_error_is_asked_again_only_after_an_update()
    {
        var state = new ReportsState();
        state.Answer("error/music/abc123", "1.5.3");
        Assert.DoesNotContain(true, Enumerable.Range(0, 6).Select(_ => state.CountError("error/music/abc123", Today, "1.5.3")));
        Assert.Contains(true, Enumerable.Range(0, 6).Select(_ => state.CountError("error/music/abc123", Today.AddDays(1), "1.5.4")));
    }

    [Fact]
    public void Two_crashes_in_a_feature_in_a_day_pause_it_until_the_next_version()
    {
        var state = new ReportsState();
        var now = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now));
        Assert.False(state.RecordCrash("module:Shammy.Modules.Music", "Music", Today, "1.5.3", now));
        Assert.True(state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now));
        Assert.True(state.IsPaused("dictation"));
        Assert.Equal(new[] { "dictation" }, state.PauseNotices);

        Assert.Empty(state.ResumeAfterUpdate("1.5.3"));
        Assert.Equal(new[] { "dictation" }, state.ResumeAfterUpdate("1.5.4"));
        Assert.False(state.IsPaused("dictation"));
        Assert.Empty(state.PauseNotices);
    }

    [Fact]
    public void Crashes_on_different_days_dont_pause()
    {
        var state = new ReportsState();
        var now = DateTime.UtcNow;
        Assert.False(state.RecordCrash("dictation", "Dictation", Today.AddDays(-1), "1.5.3", now));
        Assert.False(state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now));
    }

    [Fact]
    public void Turning_a_feature_back_on_starts_its_count_over()
    {
        var state = new ReportsState();
        var now = DateTime.UtcNow;
        state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now);
        state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now);
        state.Resume("dictation");
        Assert.False(state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now));
        Assert.True(state.RecordCrash("dictation", "Dictation", Today, "1.5.3", now));
    }

    [Fact]
    public void Old_counts_and_week_old_crashes_are_forgotten()
    {
        var state = new ReportsState { PendingCrash = Sample(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc)) };
        state.CountError("error/a/111111", Today.AddDays(-1), "1.5.3");
        state.Prune(Today, new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.Empty(state.Errors);
        Assert.Null(state.PendingCrash);
    }

    [Fact]
    public void Survives_a_round_trip_through_json()
    {
        var state = new ReportsState { PendingCrash = Sample(DateTime.UtcNow) };
        state.RecordCrash("dictation", "Dictation", Today, "1.5.3", DateTime.UtcNow);
        state.CountError("error/a/111111", Today, "1.5.3");
        var back = JsonSerializer.Deserialize<ReportsState>(JsonSerializer.Serialize(state))!;
        Assert.Equal("crash/voice/3f9ac2", back.PendingCrash!.Signature);
        Assert.Single(back.Crashes);
        Assert.Equal(1, back.TodayCount("error/a/111111", Today));
    }

    [Fact]
    public void The_body_matches_what_the_relay_reads_and_cleans_the_note()
    {
        var json = JsonDocument.Parse(Sample(DateTime.UtcNow).ToJson("Call me on 98765 43210", new[] { "Roushan" })).RootElement;
        foreach (var name in new[] { "kind", "signature", "version", "error", "area", "where", "windows", "when", "times", "healing", "note", "stack", "log" })
            Assert.True(json.TryGetProperty(name, out _), name);
        Assert.Equal("crash", json.GetProperty("kind").GetString());
        Assert.Equal("Call me on ‹number›", json.GetProperty("note").GetString());
    }

    internal static Report Sample(DateTime when) => new(Report.CrashKind, "crash/voice/3f9ac2", "1.5.3", "AccessViolationException",
        "Voice › Microphone", "WaveInEvent.DoRecording", "Windows 11 (26300)", when, 1, null,
        new[] { "NAudio.Wave.WaveInEvent.DoRecording" }, new[] { "20:31:52.000 [INFO] Talk key held" });
}

public class LogTailTests
{
    [Fact]
    public void Takes_the_end_of_the_run_before_this_one()
    {
        var lines = new List<string> { "09:00:00.000 [INFO] Shammy 1.5.2 starting on Windows" };
        lines.AddRange(Enumerable.Range(1, 40).Select(i => $"09:00:{i:00}.000 [INFO] line {i}"));
        lines.Add("20:00:00.000 [INFO] Shammy 1.5.3 starting on Windows");
        lines.Add("20:00:01.000 [INFO] this run");
        var tail = LogTail.PreviousRun(lines, 30);
        Assert.Equal(30, tail.Count);
        Assert.Equal("09:00:11.000 [INFO] line 11", tail[0]);
        Assert.Equal("09:00:40.000 [INFO] line 40", tail[^1]);
    }

    [Fact]
    public void A_short_run_gives_only_its_own_lines()
    {
        var lines = new[]
        {
            "08:00:00.000 [INFO] Shammy 1.5.2 starting on Windows", "08:00:01.000 [INFO] old",
            "09:00:00.000 [INFO] Shammy 1.5.2 starting on Windows", "09:00:01.000 [INFO] crashed run",
            "20:00:00.000 [INFO] Shammy 1.5.3 starting on Windows",
        };
        Assert.Equal(new[] { "09:00:00.000 [INFO] Shammy 1.5.2 starting on Windows", "09:00:01.000 [INFO] crashed run" }, LogTail.PreviousRun(lines, 30));
        Assert.Empty(LogTail.PreviousRun(new[] { "20:00:00.000 [INFO] Shammy 1.5.3 starting on Windows" }, 30));
    }
}
