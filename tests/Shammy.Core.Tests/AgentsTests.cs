using System.Text;
using System.Text.Json;
using Shammy.Core.Agents;

namespace Shammy.Core.Tests;

public class AgentHookSettingsTests
{
    static readonly HookEndpoint Ep = new(47611, "0123456789abcdef0123456789abcdef", "C:/Windows/System32/curl.exe");

    const string Rich = """
        {
          "$schema": "https://json.schemastore.org/claude-code-settings.json",
          "permissions": {
            "allow": ["Bash(npm run lint)", "Bash(git status && git diff)"],
            "deny": ["Read(./.env)"]
          },
          "env": { "NOTE": "naïve café ✓ <tag> & 'quotes'" },
          "hooks": {
            "PostToolUse": [
              {
                "matcher": "Edit|Write",
                "hooks": [
                  { "type": "command", "command": "jq -r '.tool_input.file_path' | xargs npx prettier --write" }
                ]
              }
            ],
            "Stop": [
              { "hooks": [ { "type": "command", "command": "powershell -c \"[console]::beep(880,120)\"", "timeout": 5 } ] }
            ]
          },
          "model": "opus"
        }

        """;

    static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    static List<string> Commands(string json, string ev) =>
        Parse(json).GetProperty("hooks").GetProperty(ev).EnumerateArray()
            .SelectMany(g => g.GetProperty("hooks").EnumerateArray())
            .Select(h => h.GetProperty("command").GetString()!).ToList();

    [Fact]
    public void The_status_command_is_silent_quick_and_always_succeeds()
    {
        Assert.Equal(
            "C:/Windows/System32/curl.exe -s -o NUL -m 2 --connect-timeout 0.3 -H Content-Type:application/json -H Expect: --data-binary '@-' " +
            "http://127.0.0.1:47611/shammy-hook/0123456789abcdef0123456789abcdef/status; exit 0",
            AgentHooks.StatusCommand(Ep));
    }

    [Fact]
    public void The_approval_command_prints_the_answer_and_gives_up_before_Claude_Code_would_cancel_it()
    {
        Assert.Equal(
            "C:/Windows/System32/curl.exe -s -m 295 --connect-timeout 0.3 -H Content-Type:application/json -H Expect: --data-binary '@-' " +
            "http://127.0.0.1:47611/shammy-hook/0123456789abcdef0123456789abcdef/ask; exit 0",
            AgentHooks.AskCommand(Ep));
        Assert.True(AgentHooks.AskAnswerSeconds < AgentHooks.AskCurlSeconds);
        Assert.True(AgentHooks.AskCurlSeconds < AgentHooks.AskHookSeconds);
    }

    [Fact]
    public void Only_the_approval_hook_may_print_and_only_the_tool_ping_runs_in_the_background()
    {
        var entries = AgentHooks.Entries(Ep);
        Assert.Equal(new[] { "UserPromptSubmit", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Notification", "Stop", "StopFailure", "SessionEnd" },
            entries.Select(e => e.Event));
        foreach (var e in entries)
        {
            Assert.Contains("/shammy-hook/", e.Command);
            Assert.EndsWith("; exit 0", e.Command);
            Assert.Equal(e.Event != "PermissionRequest", e.Command.Contains(" -o NUL "));
            Assert.Equal(e.Event is "PostToolUse" or "PostToolUseFailure", e.Async);
        }
        Assert.Equal(AgentHooks.AskHookSeconds, entries.Single(e => e.Event == "PermissionRequest").TimeoutSeconds);
        // Claude Code gives closing hooks 1.5 s; a timeout of ours would stretch that for everyone's hooks.
        Assert.Null(entries.Single(e => e.Event == "SessionEnd").TimeoutSeconds);
        Assert.Contains(" -m 1 ", entries.Single(e => e.Event == "SessionEnd").Command);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\curl.exe", "C:/Windows/System32/curl.exe")]
    [InlineData("C:/Windows/System32/curl.exe", "C:/Windows/System32/curl.exe")]
    [InlineData(@"C:\Program Files\Git\mingw64\bin\curl.exe", "curl.exe")]
    [InlineData(@"C:\Tools\curl.exe; del x", "curl.exe")]
    [InlineData(@"C:\Tools\$(evil)\curl.exe", "curl.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "curl.exe")]
    [InlineData("", "curl.exe")]
    [InlineData(null, "curl.exe")]
    public void A_curl_path_that_would_need_quoting_falls_back_to_the_bare_name(string? path, string expected) =>
        Assert.Equal(expected, AgentHooks.SafeCurl(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n")]
    public void Connecting_an_empty_file_writes_just_the_hooks(string? text)
    {
        var edit = AgentHooks.Connect(text, Ep);
        Assert.True(edit.Ok, edit.Error);
        Assert.True(edit.Changed);
        var root = Parse(edit.Json);
        Assert.Equal(new[] { "hooks" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(8, root.GetProperty("hooks").EnumerateObject().Count());
        Assert.Equal(HookState.Connected, AgentHooks.Inspect(edit.Json, Ep));
    }

    [Fact]
    public void Connecting_a_file_with_no_hooks_key_keeps_every_other_key_as_written()
    {
        const string before = "{\n  \"model\": \"opus\",\n  \"permissions\": { \"allow\": [ \"Bash(ls)\" ] }\n}\n";
        var edit = AgentHooks.Connect(before, Ep);
        Assert.True(edit.Ok, edit.Error);
        Assert.StartsWith("{\n  \"model\": \"opus\",\n  \"permissions\": { \"allow\": [ \"Bash(ls)\" ] },\n  \"hooks\": {\n    \"UserPromptSubmit\": [\n", edit.Json);
        Assert.EndsWith("\n  }\n}\n", edit.Json);
        Assert.Equal(new[] { "model", "permissions", "hooks" }, Parse(edit.Json).EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void What_connect_writes_is_exactly_the_documented_shape()
    {
        var edit = AgentHooks.Connect("{}", Ep);
        var hooks = Parse(edit.Json).GetProperty("hooks");

        var ask = hooks.GetProperty("PermissionRequest")[0];
        Assert.False(ask.TryGetProperty("matcher", out _));
        var handler = ask.GetProperty("hooks")[0];
        Assert.Equal(new[] { "type", "command", "timeout" }, handler.EnumerateObject().Select(p => p.Name));
        Assert.Equal("command", handler.GetProperty("type").GetString());
        Assert.Equal(AgentHooks.AskCommand(Ep), handler.GetProperty("command").GetString());
        Assert.Equal(300, handler.GetProperty("timeout").GetInt32());

        var tool = hooks.GetProperty("PostToolUse")[0].GetProperty("hooks")[0];
        Assert.True(tool.GetProperty("async").GetBoolean());
        Assert.Equal(AgentHooks.StatusCommand(Ep), tool.GetProperty("command").GetString());

        Assert.Equal("permission_prompt|idle_prompt", hooks.GetProperty("Notification")[0].GetProperty("matcher").GetString());
        Assert.False(hooks.GetProperty("SessionEnd")[0].GetProperty("hooks")[0].TryGetProperty("timeout", out _));
    }

    [Fact]
    public void Connecting_adds_to_an_event_that_already_has_hooks_and_leaves_them_first()
    {
        var edit = AgentHooks.Connect(Rich, Ep);
        Assert.True(edit.Ok, edit.Error);

        var post = Commands(edit.Json, "PostToolUse");
        Assert.Equal(2, post.Count);
        Assert.Equal("jq -r '.tool_input.file_path' | xargs npx prettier --write", post[0]);
        Assert.Equal(AgentHooks.StatusCommand(Ep), post[1]);

        var stop = Commands(edit.Json, "Stop");
        Assert.Equal("powershell -c \"[console]::beep(880,120)\"", stop[0]);
        Assert.Equal(AgentHooks.StatusCommand(Ep), stop[1]);

        // The user's own lines are still there, character for character.
        Assert.Contains("\"env\": { \"NOTE\": \"naïve café ✓ <tag> & 'quotes'\" },", edit.Json);
        Assert.Contains("\"allow\": [\"Bash(npm run lint)\", \"Bash(git status && git diff)\"],", edit.Json);
        Assert.Contains("{ \"type\": \"command\", \"command\": \"jq -r '.tool_input.file_path' | xargs npx prettier --write\" }", edit.Json);
        Assert.Equal(AgentHooks.Others(Rich), AgentHooks.Others(edit.Json));
        Assert.Equal("opus", Parse(edit.Json).GetProperty("model").GetString());
    }

    [Fact]
    public void Connecting_twice_changes_nothing_the_second_time()
    {
        var first = AgentHooks.Connect(Rich, Ep);
        var second = AgentHooks.Connect(first.Json, Ep);
        Assert.True(second.Ok, second.Error);
        Assert.False(second.Changed);
        Assert.Equal(first.Json, second.Json);
        Assert.Equal(8, AgentHooks.Found(second.Json).Count);
    }

    [Fact]
    public void Connecting_again_with_a_new_port_replaces_the_old_hooks_instead_of_adding_more()
    {
        var first = AgentHooks.Connect(Rich, Ep);
        var moved = Ep with { Port = 50123, Token = "ffffffffffffffffffffffffffffffff" };
        Assert.Equal(HookState.Stale, AgentHooks.Inspect(first.Json, moved));

        var second = AgentHooks.Connect(first.Json, moved);
        Assert.True(second.Ok, second.Error);
        Assert.True(second.Changed);
        Assert.Equal(8, AgentHooks.Found(second.Json).Count);
        Assert.DoesNotContain(":47611/", second.Json);
        Assert.Equal(HookState.Connected, AgentHooks.Inspect(second.Json, moved));
        Assert.Equal(AgentHooks.Others(Rich), AgentHooks.Others(second.Json));
    }

    [Theory]
    [InlineData("{ \"model\": \"opus\", }")]
    [InlineData("{ // my settings\n  \"model\": \"opus\" }")]
    [InlineData("{ /* note */ \"model\": \"opus\" }")]
    [InlineData("{ \"model\": \"opus\"")]
    [InlineData("{ \"hooks\": { \"Stop\": [ } }")]
    [InlineData("not json at all")]
    [InlineData("{ 'model': 'opus' }")]
    public void A_file_that_is_not_strict_JSON_is_refused_and_says_so(string text)
    {
        var connect = AgentHooks.Connect(text, Ep);
        Assert.False(connect.Ok);
        Assert.False(connect.Changed);
        Assert.Equal(text, connect.Json);
        Assert.Contains("isn’t valid JSON", connect.Error);
        Assert.Contains("left it alone", connect.Error);

        var disconnect = AgentHooks.Disconnect(text);
        Assert.False(disconnect.Ok);
        Assert.Equal(text, disconnect.Json);
        Assert.Equal(HookState.Unreadable, AgentHooks.Inspect(text, Ep));
    }

    [Fact]
    public void The_refusal_names_the_line_that_is_wrong()
    {
        var edit = AgentHooks.Connect("{\n  \"model\": \"opus\",\n  \"hooks\": {,}\n}", Ep);
        Assert.False(edit.Ok);
        Assert.Contains("(line 3)", edit.Error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{ \"hooks\": [] }")]
    [InlineData("{ \"hooks\": \"none\" }")]
    [InlineData("{ \"hooks\": null }")]
    [InlineData("{ \"hooks\": { \"Stop\": {} } }")]
    [InlineData("{ \"hooks\": { \"Stop\": \"beep\" } }")]
    public void A_file_laid_out_in_a_way_Shammy_does_not_know_is_refused(string text)
    {
        var edit = AgentHooks.Connect(text, Ep);
        Assert.False(edit.Ok);
        Assert.Equal(text, edit.Json);
        Assert.Contains("left them alone", edit.Error);
    }

    [Fact]
    public void Connecting_without_a_port_is_refused()
    {
        Assert.False(AgentHooks.Connect("{}", new HookEndpoint(0, "")).Ok);
        Assert.False(AgentHooks.Connect("{}", new HookEndpoint(47611, "short")).Ok);
        Assert.False(AgentHooks.Connect("{}", new HookEndpoint(47611, "0123456789abcdef'; rm -rf x")).Ok);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{}\n")]
    [InlineData("{\n  \"model\": \"opus\"\n}\n")]
    [InlineData("{\r\n  \"model\": \"opus\",\r\n  \"hooks\": {\r\n    \"Stop\": [\r\n      { \"hooks\": [ { \"type\": \"command\", \"command\": \"beep\" } ] }\r\n    ]\r\n  }\r\n}\r\n")]
    [InlineData("{\n\t\"model\": \"opus\",\n\t\"hooks\": {\n\t\t\"PreToolUse\": [\n\t\t\t{ \"matcher\": \"Bash\", \"hooks\": [ { \"type\": \"command\", \"command\": \"check.sh\" } ] }\n\t\t]\n\t}\n}")]
    [InlineData("{\"model\":\"opus\",\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"beep\"}]}]}}")]
    [InlineData("\uFEFF{\n    \"model\": \"opus\"\n}")]
    public void Disconnecting_gives_back_the_file_exactly_as_it_was_before_connecting(string before)
    {
        var connected = AgentHooks.Connect(before, Ep);
        Assert.True(connected.Ok, connected.Error);
        Assert.NotEqual(before, connected.Json);
        Assert.Equal(HookState.Connected, AgentHooks.Inspect(connected.Json, Ep));
        JsonDocument.Parse(connected.Json.TrimStart('\uFEFF')).Dispose();

        var disconnected = AgentHooks.Disconnect(connected.Json);
        Assert.True(disconnected.Ok, disconnected.Error);
        Assert.True(disconnected.Changed);
        Assert.Equal(before, disconnected.Json);
    }

    [Fact]
    public void An_empty_hooks_key_of_the_users_goes_with_Shammys_hooks_and_the_file_means_the_same()
    {
        var connected = AgentHooks.Connect("{\"model\":\"opus\",\"hooks\":{}}", Ep);
        var disconnected = AgentHooks.Disconnect(connected.Json);
        Assert.True(disconnected.Ok, disconnected.Error);
        Assert.Equal("{\"model\":\"opus\"}", disconnected.Json);
    }

    [Fact]
    public void Disconnecting_leaves_the_other_hooks_byte_for_byte()
    {
        var connected = AgentHooks.Connect(Rich, Ep);
        var disconnected = AgentHooks.Disconnect(connected.Json);
        Assert.True(disconnected.Ok, disconnected.Error);
        Assert.Equal(Rich, disconnected.Json);
        Assert.Equal(HookState.NotConnected, AgentHooks.Inspect(disconnected.Json, Ep));
        Assert.Empty(AgentHooks.Found(disconnected.Json));
    }

    [Fact]
    public void Connect_keeps_the_files_own_indent_and_line_endings()
    {
        var tabs = AgentHooks.Connect("{\n\t\"model\": \"opus\"\n}", Ep);
        Assert.Contains("\n\t\"hooks\": {\n\t\t\"UserPromptSubmit\": [\n\t\t\t{\n\t\t\t\t\"hooks\": [\n\t\t\t\t\t{\n\t\t\t\t\t\t\"type\": \"command\",", tabs.Json);

        var windows = AgentHooks.Connect("{\r\n    \"model\": \"opus\"\r\n}\r\n", Ep);
        Assert.DoesNotContain("\n", windows.Json.Replace("\r\n", ""));
        Assert.Contains("\r\n    \"hooks\": {\r\n        \"UserPromptSubmit\": [", windows.Json);
    }

    [Fact]
    public void Disconnecting_a_file_that_was_never_connected_changes_nothing()
    {
        var edit = AgentHooks.Disconnect(Rich);
        Assert.True(edit.Ok);
        Assert.False(edit.Changed);
        Assert.Equal(Rich, edit.Json);
        Assert.False(AgentHooks.Disconnect("").Changed);
        Assert.False(AgentHooks.Disconnect(null).Changed);
    }

    [Fact]
    public void A_hook_of_Shammys_that_was_moved_into_someone_elses_group_is_still_found_and_removed_alone()
    {
        string mine = AgentHooks.StatusCommand(Ep);
        string text = "{ \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"beep\" }, { \"type\": \"command\", \"command\": \"" + mine + "\" } ] } ] } }";
        Assert.Equal(HookState.Stale, AgentHooks.Inspect(text, Ep));
        var edit = AgentHooks.Disconnect(text);
        Assert.True(edit.Ok, edit.Error);
        Assert.Equal("{ \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"beep\" } ] } ] } }", edit.Json);
    }

    [Fact]
    public void Hooks_that_merely_mention_Shammy_are_not_taken_for_its_own()
    {
        const string text = "{ \"hooks\": { \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"echo shammy-hook done >> log.txt\" } ] } ] } }";
        Assert.Equal(HookState.NotConnected, AgentHooks.Inspect(text, Ep));
        Assert.False(AgentHooks.Disconnect(text).Changed);
    }

    [Fact]
    public void Missing_hooks_make_the_connection_stale_not_connected()
    {
        var connected = AgentHooks.Connect("{}", Ep).Json;
        using var doc = JsonDocument.Parse(connected);
        var hooks = doc.RootElement.GetProperty("hooks").EnumerateObject().Where(p => p.Name != "PermissionRequest")
            .ToDictionary(p => p.Name, p => p.Value);
        string without = JsonSerializer.Serialize(new Dictionary<string, object> { ["hooks"] = hooks });
        Assert.Equal(HookState.Stale, AgentHooks.Inspect(without, Ep));
        Assert.Equal(HookState.Connected, AgentHooks.Inspect(AgentHooks.Connect(without, Ep).Json, Ep));
    }

    [Fact]
    public void An_empty_event_list_of_the_users_is_filled_and_nothing_else_moves()
    {
        const string before = "{\n  \"hooks\": {\n    \"Stop\": [],\n    \"PreToolUse\": [ { \"matcher\": \"Bash\", \"hooks\": [ { \"type\": \"command\", \"command\": \"check.sh\" } ] } ]\n  }\n}";
        var edit = AgentHooks.Connect(before, Ep);
        Assert.True(edit.Ok, edit.Error);
        Assert.Single(Commands(edit.Json, "Stop"));
        Assert.Equal(AgentHooks.Others(before), AgentHooks.Others(edit.Json));
        Assert.Equal(AgentHooks.Others(before), AgentHooks.Others(AgentHooks.Disconnect(edit.Json).Json));
    }
}

public class AgentHookPayloadTests
{
    static AgentEvent? Parse(string json) => HookPayload.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void A_permission_request_gives_the_session_the_folder_the_tool_and_its_command()
    {
        var e = Parse("""
            {"session_id":"abc123","transcript_path":"C:\\Users\\me\\.claude\\projects\\x\\t.jsonl","cwd":"C:\\dev\\shammy",
             "permission_mode":"default","hook_event_name":"PermissionRequest","tool_name":"Bash",
             "tool_input":{"command":"dotnet test tests/Shammy.Core.Tests","description":"Run the tests"},
             "permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Bash","ruleContent":"dotnet test"}],"behavior":"allow","destination":"localSettings"}]}
            """)!;
        Assert.Equal(AgentEventKind.Permission, e.Kind);
        Assert.Equal("abc123", e.SessionId);
        Assert.Equal(@"C:\dev\shammy", e.Cwd);
        Assert.Equal("Bash", e.ToolName);
        Assert.Equal("dotnet test tests/Shammy.Core.Tests", e.Field("command"));
        Assert.Equal("dotnet test tests/Shammy.Core.Tests", AgentText.Detail(e));
    }

    [Theory]
    [InlineData("UserPromptSubmit", "", AgentEventKind.Prompt)]
    [InlineData("PostToolUse", "", AgentEventKind.ToolDone)]
    [InlineData("PostToolUseFailure", "", AgentEventKind.ToolDone)]
    [InlineData("PermissionRequest", "", AgentEventKind.Permission)]
    [InlineData("Notification", "permission_prompt", AgentEventKind.NeedsInput)]
    [InlineData("Notification", "elicitation_dialog", AgentEventKind.NeedsInput)]
    [InlineData("Notification", "idle_prompt", AgentEventKind.Idle)]
    [InlineData("Notification", "auth_success", AgentEventKind.Other)]
    [InlineData("Stop", "", AgentEventKind.Stop)]
    [InlineData("StopFailure", "", AgentEventKind.StopFailed)]
    [InlineData("SessionEnd", "", AgentEventKind.SessionEnd)]
    [InlineData("SessionStart", "", AgentEventKind.Other)]
    [InlineData("SubagentStop", "", AgentEventKind.Other)]
    [InlineData("SomethingNew", "", AgentEventKind.Other)]
    public void Each_hook_event_means_one_thing(string hookEvent, string notification, AgentEventKind kind)
    {
        var e = Parse($$"""{"session_id":"s1","cwd":"/home/me/app","hook_event_name":"{{hookEvent}}","notification_type":"{{notification}}"}""")!;
        Assert.Equal(kind, e.Kind);
        Assert.Equal(hookEvent, e.HookEvent);
    }

    [Fact]
    public void A_file_edit_gives_its_path_and_never_its_content()
    {
        var e = Parse("""{"session_id":"s1","cwd":"C:\\dev\\app","hook_event_name":"PermissionRequest","tool_name":"Write","tool_input":{"file_path":"C:\\dev\\app\\src\\index.ts","content":"secret = 42"}}""")!;
        Assert.Equal(@"C:\dev\app\src\index.ts", AgentText.Detail(e));
        Assert.False(e.Fields.ContainsKey("content"));
    }

    [Fact]
    public void A_payload_cut_off_inside_a_huge_value_still_gives_what_came_before()
    {
        string start = """{"session_id":"s1","cwd":"C:\\dev\\app","hook_event_name":"PostToolUse","tool_name":"Read","tool_input":{"file_path":"C:\\dev\\app\\big.txt"},"tool_response":{"content":" """.TrimEnd();
        var e = Parse(start + new string('x', 300_000))!;
        Assert.Equal(AgentEventKind.ToolDone, e.Kind);
        Assert.Equal("s1", e.SessionId);
        Assert.Equal("Read", e.ToolName);
        Assert.Equal(@"C:\dev\app\big.txt", e.Field("file_path"));
    }

    [Fact]
    public void Fields_inside_the_tools_result_are_not_mistaken_for_its_input()
    {
        var e = Parse("""{"session_id":"s1","hook_event_name":"PostToolUse","tool_name":"Bash","tool_input":{"command":"ls","nested":{"command":"inner"}},"tool_response":{"command":"other","file_path":"x"}}""")!;
        Assert.Equal("ls", e.Field("command"));
        Assert.Equal("", e.Field("file_path"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("[1,2,3]")]
    [InlineData("{}")]
    [InlineData("{\"session_id\":\"\",\"hook_event_name\":\"Stop\"}")]
    [InlineData("{\"session_id\":\"s1\"}")]
    [InlineData("{\"session_id\":42,\"hook_event_name\":\"Stop\"}")]
    [InlineData("{\"hook_event_name\":\"Stop\"}")]
    [InlineData("{\"session_id\":\"a\\u0000b\",\"hook_event_name\":\"Stop\"}")]
    [InlineData("{{{{")]
    public void Anything_that_is_not_a_hook_payload_is_ignored(string body) => Assert.Null(Parse(body));

    [Fact]
    public void A_session_id_too_long_to_be_real_is_ignored() =>
        Assert.Null(Parse("""{"session_id":"AAA","hook_event_name":"Stop"}""".Replace("AAA", new string('a', 200))));

    [Fact]
    public void A_very_long_command_is_kept_to_a_sane_length()
    {
        var e = Parse("""{"session_id":"s1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"YYY"}}""".Replace("YYY", new string('y', 20_000)))!;
        Assert.Equal(HookPayload.MaxField, e.Field("command").Length);
    }

    [Fact]
    public void A_byte_order_mark_in_front_is_fine()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"session_id\":\"s1\",\"hook_event_name\":\"Stop\"}")).ToArray();
        Assert.Equal(AgentEventKind.Stop, HookPayload.Parse(bytes)!.Kind);
    }
}

public class AgentSessionTests
{
    static readonly DateTime T0 = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
    static readonly DateOnly Day = new(2026, 11, 10);

    static AgentEvent Ev(AgentEventKind kind, string session = "s1", string tool = "", string cwd = @"C:\dev\shammy") =>
        new(kind, session, cwd, kind.ToString(), tool, new Dictionary<string, string>());

    [Fact]
    public void A_session_works_waits_works_again_and_finishes()
    {
        var all = new AgentSessions();
        Assert.Equal(AgentChange.Working, all.Apply(Ev(AgentEventKind.Prompt), T0, Day));
        var s = all.Find("s1")!;
        Assert.Equal(AgentState.Working, s.State);
        Assert.Equal(T0, s.TurnStarted);

        Assert.Equal(AgentChange.Waiting, all.Apply(Ev(AgentEventKind.Permission, tool: "Bash"), T0.AddSeconds(30), Day));
        Assert.Equal(AgentState.Waiting, s.State);
        Assert.Equal("Wants to run a command", s.Request);
        Assert.Equal(T0.AddSeconds(30), s.WaitingSince);

        Assert.Equal(AgentChange.Working, all.Apply(Ev(AgentEventKind.ToolDone, tool: "Bash"), T0.AddSeconds(42), Day));
        Assert.Equal(AgentState.Working, s.State);
        // The turn keeps its clock through the question.
        Assert.Equal(T0, s.TurnStarted);
        Assert.Equal("2:14", AgentText.PetValue(s, T0.AddSeconds(134)));

        Assert.Equal(AgentChange.Finished, all.Apply(Ev(AgentEventKind.Stop), T0.AddSeconds(140), Day));
        Assert.Equal(AgentState.Done, s.State);
        Assert.False(s.Failed);
        Assert.Equal("Done", AgentText.PetValue(s, T0.AddSeconds(141)));
    }

    [Fact]
    public void A_new_prompt_starts_the_clock_again()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        all.Apply(Ev(AgentEventKind.Stop), T0.AddMinutes(2), Day);
        Assert.Equal(AgentChange.Working, all.Apply(Ev(AgentEventKind.Prompt), T0.AddMinutes(5), Day));
        Assert.Equal("0:10", AgentText.PetValue(all.Find("s1")!, T0.AddMinutes(5).AddSeconds(10)));
    }

    [Fact]
    public void A_tool_finishing_while_it_works_changes_nothing_but_keeps_it_alive()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.ToolDone), T0.AddMinutes(20), Day));
        Assert.Equal(0, all.Sweep(T0.AddMinutes(45)));
        Assert.Equal(1, all.Sweep(T0.AddMinutes(50)));
    }

    [Fact]
    public void A_late_tool_ping_does_not_bring_a_finished_turn_back()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        all.Apply(Ev(AgentEventKind.Stop), T0.AddSeconds(50), Day);
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.ToolDone), T0.AddSeconds(51), Day));
        Assert.Equal(AgentState.Done, all.Find("s1")!.State);
    }

    [Fact]
    public void A_session_first_heard_of_mid_turn_counts_as_working_from_then()
    {
        var all = new AgentSessions();
        Assert.Equal(AgentChange.Working, all.Apply(Ev(AgentEventKind.ToolDone), T0, Day));
        Assert.Equal(AgentState.Working, all.Find("s1")!.State);
        Assert.Equal("0:05", AgentText.PetValue(all.Find("s1")!, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_session_with_no_events_for_thirty_minutes_is_forgotten()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt, "old"), T0, Day);
        all.Apply(Ev(AgentEventKind.Prompt, "new"), T0.AddMinutes(10), Day);
        Assert.Equal(0, all.Sweep(T0.AddMinutes(29).AddSeconds(59)));
        Assert.Equal(1, all.Sweep(T0.AddMinutes(30)));
        Assert.Null(all.Find("old"));
        Assert.NotNull(all.Find("new"));
    }

    [Fact]
    public void A_closed_session_goes_at_once()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        Assert.Equal(AgentChange.Removed, all.Apply(Ev(AgentEventKind.SessionEnd), T0.AddSeconds(5), Day));
        Assert.Equal(0, all.Count);
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.SessionEnd), T0.AddSeconds(6), Day));
    }

    [Fact]
    public void Events_that_say_nothing_about_work_do_not_invent_a_session()
    {
        var all = new AgentSessions();
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.Other), T0, Day));
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.Idle), T0, Day));
        Assert.Equal(0, all.Count);
        Assert.Equal(0, all.SeenToday(Day));
    }

    [Fact]
    public void The_most_recently_active_session_comes_first()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt, "a"), T0, Day);
        all.Apply(Ev(AgentEventKind.Prompt, "b"), T0.AddSeconds(10), Day);
        Assert.Equal(new[] { "b", "a" }, all.Live.Select(s => s.Id));
        all.Apply(Ev(AgentEventKind.ToolDone, "a"), T0.AddSeconds(20), Day);
        Assert.Equal(new[] { "a", "b" }, all.Live.Select(s => s.Id));
        all.Apply(Ev(AgentEventKind.Stop, "a"), T0.AddSeconds(30), Day);
        Assert.Equal(new[] { "b" }, all.Live.Select(s => s.Id));
        Assert.Equal(new[] { "a", "b" }, all.Sessions.Select(s => s.Id));
    }

    [Fact]
    public void While_Shammy_holds_the_question_another_tool_finishing_does_not_end_the_wait()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        all.Apply(Ev(AgentEventKind.Permission, tool: "Bash"), T0.AddSeconds(5), Day);
        all.Hold("s1");
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.ToolDone, tool: "Read"), T0.AddSeconds(6), Day));
        Assert.Equal(AgentState.Waiting, all.Find("s1")!.State);

        Assert.True(all.Release("s1", answered: true, T0.AddSeconds(12)));
        Assert.Equal(AgentState.Working, all.Find("s1")!.State);
    }

    [Fact]
    public void A_question_handed_back_unanswered_keeps_the_session_waiting()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        all.Apply(Ev(AgentEventKind.Permission, tool: "Edit"), T0.AddSeconds(5), Day);
        all.Hold("s1");
        Assert.False(all.Release("s1", answered: false, T0.AddSeconds(295)));
        Assert.Equal(AgentState.Waiting, all.Find("s1")!.State);
        Assert.Equal("Needs you", AgentText.PetValue(all.Find("s1")!, T0.AddSeconds(296)));
        // Answered in the terminal later: the tool runs.
        Assert.Equal(AgentChange.Working, all.Apply(Ev(AgentEventKind.ToolDone, tool: "Edit"), T0.AddSeconds(300), Day));
    }

    [Fact]
    public void Two_questions_at_once_both_have_to_be_answered()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Permission, tool: "Bash"), T0, Day);
        all.Hold("s1");
        all.Apply(Ev(AgentEventKind.Permission, tool: "Edit"), T0.AddSeconds(1), Day);
        all.Hold("s1");
        Assert.False(all.Release("s1", answered: true, T0.AddSeconds(2)));
        Assert.Equal(AgentState.Waiting, all.Find("s1")!.State);
        Assert.True(all.Release("s1", answered: true, T0.AddSeconds(3)));
    }

    [Fact]
    public void Claude_Code_saying_it_went_idle_ends_a_turn_that_never_said_stop()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        Assert.Equal(AgentChange.Quietened, all.Apply(Ev(AgentEventKind.Idle), T0.AddMinutes(3), Day));
        Assert.Equal(AgentState.Done, all.Find("s1")!.State);
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.Idle), T0.AddMinutes(4), Day));
    }

    [Fact]
    public void A_turn_that_ends_on_an_error_reads_stopped()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        Assert.Equal(AgentChange.Finished, all.Apply(Ev(AgentEventKind.StopFailed), T0.AddSeconds(9), Day));
        var s = all.Find("s1")!;
        Assert.True(s.Failed);
        Assert.Equal("Stopped", AgentText.PetValue(s, T0.AddSeconds(10)));
        Assert.Equal("Stopped on an error · 2 min ago", AgentText.StateLine(s, T0.AddSeconds(9).AddMinutes(2)));
    }

    [Fact]
    public void A_notification_that_it_needs_you_makes_it_wait_without_a_request()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt), T0, Day);
        Assert.Equal(AgentChange.Waiting, all.Apply(Ev(AgentEventKind.NeedsInput), T0.AddSeconds(8), Day));
        Assert.Equal("", all.Find("s1")!.Request);
        // The same wait, told twice (the hook, then the notification), is one wait.
        all.Apply(Ev(AgentEventKind.Permission, tool: "Bash"), T0.AddSeconds(20), Day);
        Assert.Equal(AgentChange.None, all.Apply(Ev(AgentEventKind.NeedsInput), T0.AddSeconds(26), Day));
        Assert.Equal(T0.AddSeconds(20), all.Find("s1")!.WaitingSince);
        Assert.Equal("Wants to run a command", all.Find("s1")!.Request);
    }

    [Fact]
    public void Sessions_today_counts_each_session_once_and_starts_over_the_next_day()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt, "a"), T0, Day);
        all.Apply(Ev(AgentEventKind.Prompt, "a"), T0.AddMinutes(1), Day);
        all.Apply(Ev(AgentEventKind.Prompt, "b"), T0.AddMinutes(2), Day);
        all.Apply(Ev(AgentEventKind.SessionEnd, "a"), T0.AddMinutes(3), Day);
        Assert.Equal(2, all.SeenToday(Day));
        Assert.Equal(0, all.SeenToday(Day.AddDays(1)));
        all.Apply(Ev(AgentEventKind.Prompt, "c"), T0.AddDays(1), Day.AddDays(1));
        Assert.Equal(1, all.SeenToday(Day.AddDays(1)));

        var again = new AgentSessions();
        again.RestoreSeen(all.Seen.Day, all.Seen.Ids);
        Assert.Equal(1, again.SeenToday(Day.AddDays(1)));
    }

    [Fact]
    public void The_folder_follows_the_session_when_it_moves()
    {
        var all = new AgentSessions();
        all.Apply(Ev(AgentEventKind.Prompt, cwd: @"C:\dev\shammy"), T0, Day);
        all.Apply(Ev(AgentEventKind.ToolDone, cwd: @"C:\dev\shammy\src"), T0.AddSeconds(1), Day);
        all.Apply(Ev(AgentEventKind.ToolDone, cwd: ""), T0.AddSeconds(2), Day);
        Assert.Equal(@"C:\dev\shammy\src", all.Find("s1")!.Cwd);
    }
}

public class AgentTextTests
{
    [Theory]
    [InlineData("Bash", "Wants to run a command")]
    [InlineData("PowerShell", "Wants to run a command")]
    [InlineData("Edit", "Wants to edit a file")]
    [InlineData("Write", "Wants to edit a file")]
    [InlineData("NotebookEdit", "Wants to edit a file")]
    [InlineData("Read", "Wants to read a file")]
    [InlineData("WebFetch", "Wants to open a web page")]
    [InlineData("WebSearch", "Wants to search the web")]
    [InlineData("Agent", "Wants to use Agent")]
    [InlineData("mcp__github__create_issue", "Wants to use create issue (github)")]
    [InlineData("mcp__memory", "Wants to use memory")]
    [InlineData("", "Wants to use a tool")]
    [InlineData(null, "Wants to use a tool")]
    public void The_request_is_one_plain_line(string? tool, string expected) => Assert.Equal(expected, AgentText.Wants(tool));

    [Fact]
    public void A_long_tool_name_cannot_stretch_the_card() =>
        Assert.True(AgentText.Wants("mcp__server__" + new string('x', 300)).Length <= "Wants to use ".Length + 36);

    [Fact]
    public void A_long_command_is_cut_in_the_middle_so_both_ends_show()
    {
        string command = "dotnet test tests/Shammy.Core.Tests --filter FullyQualifiedName~Agents --logger trx --results-directory out";
        string box = AgentText.Box(command, presenting: false);
        Assert.Equal(AgentText.BoxChars, box.Length);
        Assert.StartsWith("dotnet test tests/Shammy.Cor", box);
        Assert.EndsWith("--results-directory out", box);
        Assert.Contains("…", box);
    }

    [Fact]
    public void A_short_command_is_shown_whole() =>
        Assert.Equal("dotnet test tests/Shammy.Core.Tests", AgentText.Box("dotnet test tests/Shammy.Core.Tests", presenting: false));

    [Fact]
    public void A_script_over_several_lines_becomes_one_line() =>
        Assert.Equal("cd src && npm test | tee out.log", AgentText.Box("cd src &&\r\n\tnpm test   |\n tee out.log\n", presenting: false));

    [Fact]
    public void While_the_screen_is_shared_the_command_is_hidden()
    {
        Assert.Equal("Hidden while you share your screen", AgentText.Box("curl -H 'Authorization: Bearer sk-secret' https://api", presenting: true));
        Assert.Equal("Hidden while you share your screen", AgentText.Box("", presenting: true));
    }

    [Theory]
    [InlineData("abcdefghij", 10, "abcdefghij")]
    [InlineData("abcdefghijk", 10, "abcde…hijk")]
    [InlineData("abcdefghijk", 3, "a…k")]
    [InlineData("abcdefghijk", 2, "ab")]
    [InlineData("", 10, "")]
    public void Middle_trim_keeps_to_its_length(string text, int max, string expected) => Assert.Equal(expected, AgentText.MiddleTrim(text, max));

    [Fact]
    public void Middle_trim_never_splits_an_emoji()
    {
        string text = string.Concat(Enumerable.Repeat("😀", 12));
        string cut = AgentText.MiddleTrim(text, 9);
        Assert.DoesNotContain("\uFFFD", Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(cut)));
        Assert.True(cut.Length <= 9);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Desktop\Love\Halo", "Halo")]
    [InlineData(@"C:\dev\shammy\", "shammy")]
    [InlineData("/home/me/my-project", "my-project")]
    [InlineData("/home/me/my-project/", "my-project")]
    [InlineData("shammy", "shammy")]
    [InlineData(@"C:\", "C:")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void The_folder_is_the_last_part_of_the_path(string? cwd, string expected) => Assert.Equal(expected, AgentText.Folder(cwd));

    [Fact]
    public void The_card_label_names_the_folder_when_there_is_one()
    {
        Assert.Equal("Claude Code · shammy", AgentText.CardLabel(@"C:\dev\shammy"));
        Assert.Equal("Claude Code", AgentText.CardLabel(""));
    }

    [Theory]
    [InlineData(0, "waiting 0:00")]
    [InlineData(12, "waiting 0:12")]
    [InlineData(75, "waiting 1:15")]
    public void The_wait_counts_up(int seconds, string expected) => Assert.Equal(expected, AgentText.WaitingFor(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(60, "1 min ago")]
    [InlineData(59 * 60, "59 min ago")]
    [InlineData(2 * 3600 + 5, "2 h ago")]
    public void How_long_ago_reads_plainly(int seconds, string expected) => Assert.Equal(expected, AgentText.Ago(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(false, HookState.NotConnected, true, 0, "Claude Code not found", "Connect", false)]
    [InlineData(true, HookState.NotConnected, false, 0, "Not connected", "Connect", true)]
    [InlineData(true, HookState.Connected, true, 2, "Connected · 2 sessions today", "Disconnect", true)]
    [InlineData(true, HookState.Connected, true, 1, "Connected · 1 session today", "Disconnect", true)]
    [InlineData(true, HookState.Connected, true, 0, "Connected · no sessions today", "Disconnect", true)]
    [InlineData(true, HookState.Connected, false, 3, "Connected, but Shammy’s port is taken", "Reconnect", true)]
    [InlineData(true, HookState.Stale, true, 0, "Needs reconnecting", "Reconnect", true)]
    [InlineData(true, HookState.Unreadable, true, 0, "Claude Code’s settings can’t be read", "Connect", true)]
    public void The_settings_row_says_where_things_stand(bool found, HookState state, bool listening, int today, string status, string button, bool enabled) =>
        Assert.Equal(new AgentConnectView(status, button, enabled), AgentText.ConnectView(found, state, listening, today));

    [Theory]
    [InlineData(1, 1, 0, "1 waiting for you · 1 working")]
    [InlineData(2, 0, 1, "2 working · 1 done")]
    [InlineData(0, 2, 0, "2 waiting for you")]
    [InlineData(0, 0, 0, "Nothing running")]
    public void The_card_sums_up_the_sessions(int working, int waiting, int done, string expected) =>
        Assert.Equal(expected, AgentText.Summary(working, waiting, done));

    [Fact]
    public void Allow_and_deny_are_the_documented_answers_on_one_line()
    {
        using var allow = JsonDocument.Parse(AgentText.AllowJson);
        var a = allow.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PermissionRequest", a.GetProperty("hookEventName").GetString());
        Assert.Equal("allow", a.GetProperty("decision").GetProperty("behavior").GetString());
        // Nothing that would let it run again without asking, or change what runs.
        Assert.Equal(new[] { "behavior" }, a.GetProperty("decision").EnumerateObject().Select(p => p.Name));

        using var deny = JsonDocument.Parse(AgentText.DenyJson);
        var d = deny.RootElement.GetProperty("hookSpecificOutput").GetProperty("decision");
        Assert.Equal("deny", d.GetProperty("behavior").GetString());
        Assert.Equal("Denied from the Shammy notch.", d.GetProperty("message").GetString());
        Assert.False(d.TryGetProperty("interrupt", out _));

        foreach (var json in new[] { AgentText.AllowJson, AgentText.DenyJson })
        {
            Assert.DoesNotContain('\n', json);
            Assert.All(json, c => Assert.InRange(c, ' ', '~'));
        }
        Assert.Equal("", AgentText.NoDecision);
    }

    [Theory]
    [InlineData("Bash", true)]
    [InlineData("Edit", true)]
    [InlineData("mcp__github__create_issue", true)]
    [InlineData("AskUserQuestion", false)]
    [InlineData("ExitPlanMode", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Questions_only_the_terminal_can_answer_get_no_buttons(string? tool, bool canAnswer) =>
        Assert.Equal(canAnswer, AgentText.CanAnswer(tool));
}

public class AgentHookHttpTests
{
    const string Token = "0123456789abcdef0123456789abcdef";

    static HookRequestHead? Head(string text) => HookHttp.ParseHead(Encoding.ASCII.GetBytes(text));

    [Fact]
    public void A_curl_post_is_read()
    {
        var head = Head($"POST /shammy-hook/{Token}/ask HTTP/1.1\r\nHost: 127.0.0.1:47611\r\nUser-Agent: curl/8.22.0\r\nAccept: */*\r\nContent-Type:application/json\r\nContent-Length: 423\r\n\r\n{{\"session")!;
        Assert.Equal("POST", head.Method);
        Assert.Equal($"/shammy-hook/{Token}/ask", head.Path);
        Assert.Equal(423, head.ContentLength);
        Assert.False(head.ExpectsContinue);
        Assert.Equal("ask", HookHttp.Route(head.Method, head.Path, Token));
    }

    [Fact]
    public void A_request_that_waits_for_a_go_ahead_is_noticed() =>
        Assert.True(Head("POST /x HTTP/1.1\r\nExpect: 100-continue\r\nContent-Length: 5\r\n\r\n")!.ExpectsContinue);

    [Theory]
    [InlineData("")]
    [InlineData("POST /x HTTP/1.1\r\nContent-Length: 5\r\n")]
    [InlineData("GARBAGE\r\n\r\n")]
    [InlineData("POST x HTTP/1.1\r\n\r\n")]
    [InlineData("POST /x SPDY/3\r\n\r\n")]
    [InlineData("POST /x HTTP/1.1\r\nContent-Length: -5\r\n\r\n")]
    [InlineData("POST /x HTTP/1.1\r\nContent-Length: lots\r\n\r\n")]
    [InlineData("POST /x HTTP/1.1\r\nno colon here\r\n\r\n")]
    [InlineData("POST /x HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")]
    public void A_malformed_request_is_refused(string text) => Assert.Null(Head(text));

    [Fact]
    public void Headers_that_go_on_too_long_are_refused() =>
        Assert.Null(Head("POST /x HTTP/1.1\r\nX-Pad: " + new string('a', HookHttp.MaxHead) + "\r\n\r\n"));

    [Fact]
    public void A_request_without_a_length_says_so() => Assert.Equal(-1, Head("POST /x HTTP/1.1\r\nHost: a\r\n\r\n")!.ContentLength);

    [Theory]
    [InlineData("POST", "/shammy-hook/0123456789abcdef0123456789abcdef/status", "status")]
    [InlineData("POST", "/shammy-hook/0123456789abcdef0123456789abcdef/ask", "ask")]
    [InlineData("POST", "/shammy-hook/0123456789abcdef0123456789abcdef/ask?x=1", "ask")]
    [InlineData("GET", "/shammy-hook/0123456789abcdef0123456789abcdef/status", null)]
    [InlineData("POST", "/shammy-hook/0123456789abcdef0123456789abcdeX/status", null)]
    [InlineData("POST", "/shammy-hook/0123456789abcdef/status", null)]
    [InlineData("POST", "/shammy-hook//status", null)]
    [InlineData("POST", "/shammy-hook/0123456789abcdef0123456789abcdef/other", null)]
    [InlineData("POST", "/shammy-hook/0123456789abcdef0123456789abcdef/ask/more", null)]
    [InlineData("POST", "/other/0123456789abcdef0123456789abcdef/ask", null)]
    [InlineData("POST", "/", null)]
    public void Only_the_exact_address_with_the_right_secret_is_let_in(string method, string path, string? route) =>
        Assert.Equal(route, HookHttp.Route(method, path, Token));

    [Fact]
    public void With_no_secret_set_nothing_is_let_in() => Assert.Null(HookHttp.Route("POST", "/shammy-hook//status", ""));

    [Fact]
    public void A_reply_states_its_length_and_closes()
    {
        string reply = Encoding.UTF8.GetString(HookHttp.Response(200, AgentText.AllowJson));
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", reply);
        Assert.Contains($"Content-Length: {AgentText.AllowJson.Length}\r\n", reply);
        Assert.Contains("Connection: close\r\n", reply);
        Assert.EndsWith("\r\n\r\n" + AgentText.AllowJson, reply);

        string empty = Encoding.UTF8.GetString(HookHttp.Response(404));
        Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", empty);
        Assert.EndsWith("Content-Length: 0\r\nConnection: close\r\n\r\n", empty);
    }
}
