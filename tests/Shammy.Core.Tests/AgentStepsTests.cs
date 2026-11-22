using System.Text;
using System.Text.Json;
using Shammy.Core.Agents;

namespace Shammy.Core.Tests;

public class AgentStepsTests
{
    static Func<string, string> F(params (string Name, string Value)[] fields) =>
        name => fields.FirstOrDefault(f => f.Name == name).Value ?? "";

    [Theory]
    [InlineData("Read", "file_path", @"C:\dev\shammy\src\MusicModule.cs", "Reading MusicModule.cs")]
    [InlineData("Edit", "file_path", "/home/me/app/hero.astro", "Editing hero.astro")]
    [InlineData("Write", "file_path", @"C:\x\notes.md", "Writing notes.md")]
    [InlineData("Bash", "command", "dotnet test tests/Shammy.Core.Tests -v q", "Running tests")]
    [InlineData("Bash", "command", "cd site && npm run build", "Building")]
    [InlineData("Bash", "command", "npm install three", "Installing packages")]
    [InlineData("Bash", "command", "git commit -m \"x\"", "Committing")]
    [InlineData("Bash", "command", "git status --short", "Using git")]
    [InlineData("PowerShell", "command", "Get-ChildItem C:\\x", "Running get-childitem")]
    [InlineData("Grep", "pattern", "lyrics", "Searching for “lyrics”")]
    [InlineData("WebFetch", "url", "https://www.example.com/page", "Opening example.com")]
    [InlineData("WebSearch", "query", "velopack apply", "Searching the web for “velopack apply”")]
    public void Steps_are_worded_as_plain_actions(string tool, string field, string value, string expected)
    {
        var words = StepWords.Describe(tool, F((field, value)));
        Assert.Equal(expected, words!.Value.Text);
    }

    [Fact]
    public void A_command_with_claudes_own_description_uses_it_and_odd_tools_still_read_well()
    {
        Assert.Equal("List the files", StepWords.Describe("Bash", F(("command", "ls -la ./src"), ("description", "List the files")))!.Value.Text);
        Assert.Equal("Using create issue (github)", StepWords.Describe("mcp__github__create_issue", F())!.Value.Text);
        Assert.Equal("Helper: Find the cause", StepWords.Describe("Task", F(("description", "Find the cause")))!.Value.Text);
        Assert.Equal(StepKind.Ask, StepWords.Describe("AskUserQuestion", F())!.Value.Kind);
        // Task-list tools only update the list; they aren't steps.
        Assert.Null(StepWords.Describe("TodoWrite", F()));
        Assert.Null(StepWords.Describe("TaskUpdate", F()));
        Assert.Null(StepWords.Describe("", F()));
    }

    static AgentEvent Ev(string hook, string tool, string id, string session = "s1", params (string, string)[] fields) =>
        new(HookPayload.Kind(hook), session, @"C:\dev\shammy", hook, tool, fields.ToDictionary(f => f.Item1, f => f.Item2)) { ToolUseId = id };

    [Fact]
    public void A_step_starts_on_PreToolUse_and_is_marked_when_it_ends_or_fails()
    {
        var feed = new AgentFeed();
        var now = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        feed.NewTurn("s1");
        var started = feed.Note(Ev("PreToolUse", "Bash", "t1", "s1", ("command", "dotnet test")), now)!;
        Assert.False(started.Finished);
        feed.Note(Ev("PostToolUseFailure", "Bash", "t1", "s1", ("command", "dotnet test")), now);
        Assert.True(started.Finished);
        Assert.True(started.Failed);
        // An end with no start heard still shows, already finished.
        var late = feed.Note(Ev("PostToolUse", "Read", "t2", "s1", ("file_path", "a.cs")), now)!;
        Assert.True(late.Finished);
        Assert.False(late.Failed);
        Assert.Equal(new[] { "Reading a.cs", "Running tests" }, feed.Recent(5).Select(s => s.Text));
        Assert.Equal(2, feed.TurnSteps("s1"));
        feed.NewTurn("s1");
        Assert.Equal(0, feed.TurnSteps("s1"));
    }

    [Fact]
    public void The_feed_mixes_sessions_newest_first_and_keeps_a_bounded_history()
    {
        var feed = new AgentFeed();
        var now = DateTime.UtcNow;
        for (int i = 0; i < AgentFeed.Keep + 10; i++)
            feed.Note(Ev("PostToolUse", "Read", "id" + i, i % 2 == 0 ? "a" : "b", ("file_path", $"f{i}.cs")), now);
        Assert.Equal(AgentFeed.Keep, feed.Recent(1000).Count);
        Assert.Equal($"Reading f{AgentFeed.Keep + 9}.cs", feed.Recent(1)[0].Text);
        feed.Forget("a");
        Assert.All(feed.Recent(1000), s => Assert.Equal("b", s.SessionId));
    }

    [Fact]
    public void TodoWrite_replaces_the_list_and_shows_the_active_form_for_the_task_underway()
    {
        var lists = new AgentTaskLists();
        string json = """{"todos":[{"content":"Find the cause","status":"completed","activeForm":"Finding the cause"},{"content":"Fix the card","status":"in_progress","activeForm":"Fixing the card"},{"content":"Run the tests","status":"pending","activeForm":"Running the tests"}]}""";
        Assert.True(lists.Apply("s1", "TodoWrite", json));
        var l = lists.For("s1");
        Assert.Equal(new[] { "Find the cause", "Fixing the card", "Run the tests" }, l.Select(t => t.Text));
        Assert.Equal(new[] { TaskMark.Done, TaskMark.Now, TaskMark.Todo }, l.Select(t => t.Mark));
        Assert.Empty(lists.For("other"));
    }

    [Fact]
    public void TaskCreate_and_TaskUpdate_build_the_list_one_task_at_a_time()
    {
        var lists = new AgentTaskLists();
        Assert.True(lists.Apply("s1", "TaskCreate", """{"subject":"Find the cause","description":"…"}"""));
        Assert.True(lists.Apply("s1", "TaskCreate", """{"subject":"Fix it"}"""));
        Assert.True(lists.Apply("s1", "TaskUpdate", """{"taskId":"1","status":"completed"}"""));
        Assert.True(lists.Apply("s1", "TaskUpdate", """{"taskId":2,"status":"in_progress"}"""));
        Assert.Equal(new[] { TaskMark.Done, TaskMark.Now }, lists.For("s1").Select(t => t.Mark));
        Assert.True(lists.Apply("s1", "TaskUpdate", """{"taskId":"1","status":"deleted"}"""));
        Assert.Equal(new[] { "Fix it" }, lists.For("s1").Select(t => t.Text));
        // Nonsense never throws.
        Assert.False(lists.Apply("s1", "TaskUpdate", """{"taskId":"9","status":"completed"}"""));
        Assert.False(lists.Apply("s1", "TodoWrite", "not json"));
    }

    [Fact]
    public void Questions_are_read_from_the_tool_input_and_answered_with_the_input_plus_answers()
    {
        string input = AgentJson.Questions(("Which side should the hero picture sit on?", "Hero", new[] { "Right side", "Left side" }, false),
                                           ("Which pages?", "Pages", new[] { "Home", "Download", "Phone" }, true));
        var qs = AgentQuestions.Parse(input);
        Assert.Equal(2, qs.Count);
        Assert.Equal("Hero", qs[0].Header);
        Assert.Equal(new[] { "Right side", "Left side" }, qs[0].Options.Select(o => o.Label));
        Assert.True(qs[1].MultiSelect);

        var answers = new Dictionary<string, string> { [qs[0].Question] = "Right side", [qs[1].Question] = "Home, Phone" };
        using var doc = JsonDocument.Parse(AgentQuestions.AnswerJson(input, answers));
        var decision = doc.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PermissionRequest", decision.GetProperty("hookEventName").GetString());
        var d = decision.GetProperty("decision");
        Assert.Equal("allow", d.GetProperty("behavior").GetString());
        var updated = d.GetProperty("updatedInput");
        // The original questions go back unchanged, with the answers beside them.
        Assert.Equal(2, updated.GetProperty("questions").GetArrayLength());
        Assert.Equal("Home, Phone", updated.GetProperty("answers").GetProperty("Which pages?").GetString());
    }

    [Fact]
    public void Hooks_written_by_an_older_Shammy_read_as_connected_with_an_update_not_as_broken()
    {
        var ep = new HookEndpoint(47600, new string('a', 32), "C:/Windows/System32/curl.exe");
        string full = AgentHooks.Connect("", ep).Json;
        Assert.Equal(HookState.Connected, AgentHooks.Inspect(full, ep));
        // The same file without the new PreToolUse entry, as 1.7.x wrote it.
        using var doc = JsonDocument.Parse(full);
        var hooks = doc.RootElement.GetProperty("hooks").EnumerateObject().Where(p => p.Name != "PreToolUse")
            .Select(p => $"\"{p.Name}\":{p.Value.GetRawText()}");
        string older = "{\"hooks\":{" + string.Join(",", hooks) + "}}";
        Assert.Equal(HookState.Outdated, AgentHooks.Inspect(older, ep));
        var view = AgentText.ConnectView(found: true, HookState.Outdated, listening: true, sessionsToday: 1);
        Assert.Equal("Update", view.Button);
        // Another port's hooks are still "needs reconnecting".
        Assert.Equal(HookState.Stale, AgentHooks.Inspect(older, new HookEndpoint(47601, new string('a', 32), "C:/Windows/System32/curl.exe")));
    }

    [Fact]
    public void Questions_that_are_not_there_or_broken_give_nothing_to_show()
    {
        Assert.Empty(AgentQuestions.Parse("{}"));
        Assert.Empty(AgentQuestions.Parse("nope"));
        Assert.Empty(AgentQuestions.Parse("""{"questions":[{"header":"no question text"}]}"""));
    }

    [Theory]
    [InlineData("Fixed it.\n\nShall I commit it?", "Shall I commit it?")]
    [InlineData("All tests pass. Want me to publish 1.7.3?", "Want me to publish 1.7.3?")]
    [InlineData("Is this right?\n\nI went ahead and fixed it.", "")]
    [InlineData("Done. The tests pass.", "")]
    [InlineData("", "")]
    public void An_end_of_reply_question_is_found_only_in_the_last_paragraph(string message, string expected) =>
        Assert.Equal(expected, EndQuestion.Find(message));

    [Fact]
    public void The_payload_keeps_the_tool_use_id_the_whole_input_of_question_tools_and_the_last_message()
    {
        string questions = AgentJson.Questions(("Pick one?", "Pick", new[] { "A", "B" }, false));
        string ask = "{\"session_id\":\"s1\",\"hook_event_name\":\"PermissionRequest\",\"tool_name\":\"AskUserQuestion\",\"tool_use_id\":\"toolu_1\",\"tool_input\":" + questions + "}";
        var e = HookPayload.Parse(Encoding.UTF8.GetBytes(ask))!;
        Assert.Equal("toolu_1", e.ToolUseId);
        Assert.Equal("Pick one?", AgentQuestions.Parse(e.Input)[0].Question);

        // Other tools' inputs aren't kept whole (a file's text can be huge); their plain fields still are.
        string edit = "{\"session_id\":\"s1\",\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Edit\",\"tool_input\":{\"file_path\":\"a.cs\",\"new_string\":\"x\"}}";
        var e2 = HookPayload.Parse(Encoding.UTF8.GetBytes(edit))!;
        Assert.Equal("", e2.Input);
        Assert.Equal("a.cs", e2.Field("file_path"));

        string stop = "{\"session_id\":\"s1\",\"hook_event_name\":\"Stop\",\"last_assistant_message\":\"Done.\\n\\nShall I push?\"}";
        Assert.Equal("Shall I push?", EndQuestion.Find(HookPayload.Parse(Encoding.UTF8.GetBytes(stop))!.LastMessage));

        // A question list cut off mid-way is left out rather than half-read.
        var cut = Encoding.UTF8.GetBytes(ask)[..(ask.Length - 20)];
        Assert.Equal("", HookPayload.Parse(cut)!.Input);
    }
}
