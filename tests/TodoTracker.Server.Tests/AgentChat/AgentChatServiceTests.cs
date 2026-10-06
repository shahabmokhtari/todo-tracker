using System.Text.Json.Nodes;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>The chat: a warm agent with Todo Tracker's tools, safe permissions, and a transcript the UIs show.</summary>
public sealed class AgentChatServiceTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly string _dir = Directory.CreateTempSubdirectory("tt-chat-").FullName;
    private AgentChatService _chat = null!;

    private string Log => Path.Combine(_dir, "agent.log");

    public ValueTask InitializeAsync()
    {
        _chat = Create();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _chat.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private AgentChatService Create(TimeSpan? permissionTimeout = null, bool withAgent = true, bool httpOnly = false, string? crashOnce = null, bool noSession = false, TimeSpan? stopTimeout = null)
    {
        var catalog = new AgentCatalog(searchPath: _dir);
        if (withAgent)
        {
            var fake = AcpConnectionTests.FakeAgent();
            catalog.Extra.Add((new AgentOption("fake", "Fake agent", true, null), work => fake with
            {
                WorkingDirectory = work,
                Environment = new Dictionary<string, string?> { ["FAKE_ACP_LOG"] = Log, ["FAKE_ACP_HTTP"] = httpOnly ? "1" : null, ["FAKE_ACP_CRASH_ONCE"] = crashOnce, ["FAKE_ACP_NO_SESSION"] = noSession ? "1" : null },
            }));
        }

        return new AgentChatService(catalog, new AgentChatOptions
        {
            WorkDirectory = Path.Combine(_dir, "work"),
            SettingsPath = Path.Combine(_dir, "agent-chat.json"),
            Mcp = new AgentLaunch("todo-tracker", "C:\\Tools\\tt.exe", ["mcp"], _dir, new Dictionary<string, string?> { ["TODOTRACKER_VAULT"] = "C:\\Tasks" }),
            McpHttp = new McpHttpServer("http://127.0.0.1:5317/mcp", "secret-token"),
            PermissionTimeout = permissionTimeout ?? TimeSpan.FromMinutes(5),
            StopTimeout = stopTimeout ?? TimeSpan.FromSeconds(10),
        });
    }

    private async Task<ChatEntry> WaitForPermission()
    {
        var until = DateTime.UtcNow + Timeout;
        while (true)
        {
            if (_chat.State.Entries.LastOrDefault(e => e.Kind == "permission" && e.Status == "waiting") is { } entry)
            {
                return entry;
            }

            Assert.True(DateTime.UtcNow < until, "no permission request");
            await Task.Delay(25);
        }
    }

    private string LastAgentText() => _chat.State.Entries.Last(e => e.Kind == "agent").Text;

    private List<JsonNode> Received() => File.ReadAllLines(Log).Select(l => JsonNode.Parse(l)!).ToList();

    [Fact]
    public async Task The_agent_starts_on_the_first_message_and_its_reply_is_one_entry()
    {
        Assert.Equal("idle", _chat.State.Status);
        Assert.Equal("fake", _chat.State.Agent);

        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.Equal("ready", _chat.State.Status);
        Assert.Equal(["user", "agent"], _chat.State.Entries.Select(e => e.Kind));
        Assert.Equal("Hello, there.", LastAgentText());
    }

    [Fact]
    public async Task Sessions_get_the_todo_tracker_tools_a_work_folder_and_context_once()
    {
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        await _chat.SendAsync("hello again").WaitAsync(Timeout);

        var messages = Received();
        var session = messages.Single(m => m["method"]?.GetValue<string>() == "session/new")["params"]!;
        Assert.Equal(Path.Combine(_dir, "work"), session["cwd"]!.GetValue<string>());
        Assert.Equal("todo-tracker", session["mcpServers"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("C:\\Tools\\tt.exe", session["mcpServers"]![0]!["command"]!.GetValue<string>());
        Assert.Equal("TODOTRACKER_VAULT", session["mcpServers"]![0]!["env"]![0]!["name"]!.GetValue<string>());
        var prompts = messages.Where(m => m["method"]?.GetValue<string>() == "session/prompt").Select(m => m["params"]!["prompt"]!.ToJsonString()).ToList();
        Assert.Contains("Todo Tracker", prompts[0], StringComparison.Ordinal);
        Assert.Contains("hi", prompts[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Todo Tracker", prompts[1], StringComparison.Ordinal);
        Assert.Equal("hello again", _chat.State.Entries[2].Text);
    }

    [Fact]
    public async Task Agents_that_only_take_http_mcp_servers_get_the_apps_own_endpoint()
    {
        // Copilot's ACP server rejects stdio MCP servers from clients; it gets the running app's /mcp instead.
        await _chat.DisposeAsync();
        _chat = Create(httpOnly: true);

        await _chat.SendAsync("hi").WaitAsync(Timeout);

        var server = Received().Single(m => m["method"]?.GetValue<string>() == "session/new")["params"]!["mcpServers"]![0]!;
        Assert.Equal("http", server["type"]!.GetValue<string>());
        Assert.Equal("todo-tracker", server["name"]!.GetValue<string>());
        Assert.Equal("http://127.0.0.1:5317/mcp", server["url"]!.GetValue<string>());
        Assert.Equal("Bearer secret-token", server["headers"]![0]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reading_tasks_with_todo_tracker_tools_needs_no_permission()
    {
        await _chat.SendAsync("read my list").WaitAsync(Timeout);

        Assert.Equal("You have 3 things to do.", LastAgentText());
        Assert.DoesNotContain(_chat.State.Entries, e => e.Kind == "permission");
        Assert.Contains(_chat.State.Entries, e => e.Kind == "tool" && e.Status == "completed");
    }

    [Fact]
    public async Task Changing_tasks_asks_first_and_can_be_allowed_for_the_rest_of_the_chat()
    {
        var turn = _chat.SendAsync("add Milk");
        var ask = await WaitForPermission();
        Assert.Equal(["allow", "allow-chat", "reject"], ask.Choices);

        await _chat.AnswerAsync(ask.Id, "allow-chat");
        await turn.WaitAsync(Timeout);
        Assert.Equal("Added \"Milk\".", LastAgentText());
        Assert.Equal("allowed", _chat.State.Entries.Single(e => e.Id == ask.Id).Status);

        await _chat.SendAsync("add Eggs").WaitAsync(Timeout);
        Assert.Equal("Added \"Eggs\".", LastAgentText());
        Assert.Single(_chat.State.Entries, e => e.Kind == "permission");
    }

    [Fact]
    public async Task Anything_else_always_asks_and_can_be_refused()
    {
        var turn = _chat.SendAsync("run a shell command");
        var ask = await WaitForPermission();
        Assert.Equal(["allow", "reject"], ask.Choices);

        await _chat.AnswerAsync(ask.Id, "reject");
        await turn.WaitAsync(Timeout);

        Assert.Equal("reject-once", LastAgentText());
        Assert.Equal("rejected", _chat.State.Entries.Single(e => e.Id == ask.Id).Status);
    }

    [Fact]
    public async Task An_unanswered_request_is_refused_after_a_while()
    {
        await _chat.DisposeAsync();
        _chat = Create(permissionTimeout: TimeSpan.FromMilliseconds(300));

        await _chat.SendAsync("add Milk").WaitAsync(Timeout);

        Assert.Equal("Okay, I didn't add it.", LastAgentText());
        Assert.Equal("expired", _chat.State.Entries.Single(e => e.Kind == "permission").Status);
    }

    [Fact]
    public async Task One_message_at_a_time()
    {
        var turn = _chat.SendAsync("slow");
        await WaitFor(() => _chat.State.Status == "busy");

        Assert.Throws<InvalidOperationException>(() => { _ = _chat.SendAsync("another"); });

        await _chat.CancelAsync();
        await turn.WaitAsync(Timeout);
        Assert.Equal("ready", _chat.State.Status);
        Assert.Contains(_chat.State.Entries, e => e.Kind == "note" && e.Text.Contains("Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_while_a_permission_waits_answers_it_as_cancelled()
    {
        var turn = _chat.SendAsync("add Milk");
        var ask = await WaitForPermission();

        await _chat.CancelAsync();
        await turn.WaitAsync(Timeout);

        Assert.Equal("cancelled", _chat.State.Entries.Single(e => e.Id == ask.Id).Status);
        Assert.Equal("ready", _chat.State.Status);
    }

    [Fact]
    public async Task After_a_crash_the_chat_says_so_and_the_next_message_starts_the_agent_again()
    {
        await _chat.SendAsync("crash").WaitAsync(Timeout);

        Assert.Equal("error", _chat.State.Status);
        Assert.NotNull(_chat.State.Problem);

        await _chat.SendAsync("hi").WaitAsync(Timeout);
        Assert.Equal("ready", _chat.State.Status);
        Assert.Equal("Hello, there.", LastAgentText());
    }

    [Fact]
    public async Task An_agent_that_crashes_while_starting_is_started_again()
    {
        await _chat.DisposeAsync();
        _chat = Create(crashOnce: Path.Combine(_dir, "crashed"));

        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.Equal("ready", _chat.State.Status);
        Assert.Equal("Hello, there.", LastAgentText());
    }

    [Fact]
    public async Task A_crash_names_the_exit_code()
    {
        await _chat.SendAsync("crash").WaitAsync(Timeout);

        Assert.Contains("exit code 3", _chat.State.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_new_chat_starts_fresh_with_the_same_warm_agent()
    {
        await _chat.SendAsync("hi").WaitAsync(Timeout);

        await _chat.NewChatAsync();
        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.Equal(["user", "agent"], _chat.State.Entries.Select(e => e.Kind));
        Assert.Single(Received(), m => m["method"]?.GetValue<string>() == "initialize");
        Assert.Equal(2, Received().Count(m => m["method"]?.GetValue<string>() == "session/new"));
    }

    [Fact]
    public async Task The_chosen_agent_is_remembered()
    {
        await _chat.SelectAsync("fake");

        await using var again = Create();
        Assert.Equal("fake", again.State.Agent);
        await Assert.ThrowsAsync<ArgumentException>(() => _chat.SelectAsync("nope"));
    }

    [Fact]
    public async Task Without_an_agent_the_chat_explains_what_to_install()
    {
        await _chat.DisposeAsync();
        _chat = Create(withAgent: false);

        Assert.Null(_chat.State.Agent);
        var error = Assert.Throws<InvalidOperationException>(() => { _ = _chat.SendAsync("hi"); });
        Assert.Contains("Install", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_are_announced_for_the_screens_that_show_the_chat()
    {
        var updates = 0;
        _chat.Changed += (_, _) => Interlocked.Increment(ref updates);

        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.True(updates >= 3, $"{updates} updates");
    }

    [Fact]
    public async Task A_shell_command_that_mentions_a_todo_tracker_tool_still_asks()
    {
        // Review finding: tool names were found anywhere in the request (even in the command), so this was allowed.
        var turn = _chat.SendAsync("sneaky");
        var ask = await WaitForPermission();

        Assert.Equal(["allow", "reject"], ask.Choices);
        await _chat.AnswerAsync(ask.Id, "reject");
        await turn.WaitAsync(Timeout);
        Assert.Equal("reject-once", LastAgentText());
    }

    [Fact]
    public async Task A_tool_with_our_name_from_another_server_is_not_ours()
    {
        var turn = _chat.SendAsync("imposter");
        var ask = await WaitForPermission();

        Assert.Equal(["allow", "reject"], ask.Choices);
        await _chat.AnswerAsync(ask.Id, "reject");
        await turn.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Claude_style_names_for_our_read_only_tools_need_no_permission()
    {
        await _chat.SendAsync("claude").WaitAsync(Timeout);

        Assert.Equal("allow-once", LastAgentText());
        Assert.DoesNotContain(_chat.State.Entries, e => e.Kind == "permission");
    }

    [Fact]
    public async Task Allowing_changes_for_the_chat_never_allows_shell_commands()
    {
        var turn = _chat.SendAsync("add Milk");
        await _chat.AnswerAsync((await WaitForPermission()).Id, "allow-chat");
        await turn.WaitAsync(Timeout);

        turn = _chat.SendAsync("sneaky");
        var ask = await WaitForPermission();
        await _chat.AnswerAsync(ask.Id, "reject");
        await turn.WaitAsync(Timeout);

        Assert.Equal("reject-once", LastAgentText());
    }

    [Fact]
    public async Task An_answer_is_shown_as_soon_as_it_is_given()
    {
        // Review finding: the POST's state could still show the question waiting and overwrite the newer one.
        var turn = _chat.SendAsync("add Milk");
        var ask = await WaitForPermission();

        await _chat.AnswerAsync(ask.Id, "reject");

        Assert.Equal("rejected", _chat.State.Entries.Single(e => e.Id == ask.Id).Status);
        await turn.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Every_announced_state_is_newer_than_the_last()
    {
        // Review finding: a late state could overwrite a newer one; screens drop states older than the one shown.
        var versions = new System.Collections.Concurrent.ConcurrentBag<long>();
        _chat.Changed += (_, s) => versions.Add(s.Version);
        var turn = _chat.SendAsync("add Milk");
        var ask = await WaitForPermission();
        var before = _chat.State.Version;

        await _chat.AnswerAsync(ask.Id, "allow");

        Assert.True(_chat.State.Version > before);
        Assert.Equal("allowed", _chat.State.Entries.Single(e => e.Id == ask.Id).Status);
        await turn.WaitAsync(Timeout);
        Assert.Equal(versions.Count, versions.Distinct().Count());
        Assert.Equal(versions.Max(), _chat.State.Version);
        Assert.Equal(_chat.State.Version, _chat.State.Version);
    }

    [Fact]
    public async Task A_request_whose_announcement_names_something_else_asks()
    {
        var turn = _chat.SendAsync("mismatch");
        var ask = await WaitForPermission();

        Assert.Equal(["allow", "reject"], ask.Choices);
        await _chat.AnswerAsync(ask.Id, "reject");
        await turn.WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_new_chat_stops_an_agent_that_ignores_stop_and_starts_clean()
    {
        // Review finding: the forced stop left the new chat showing the old "agent stopped" error.
        await _chat.DisposeAsync();
        _chat = Create(stopTimeout: TimeSpan.FromMilliseconds(300));
        var turn = _chat.SendAsync("stubborn");
        await WaitFor(() => _chat.State.Entries.Any(e => e.Kind == "agent"));

        await _chat.NewChatAsync().WaitAsync(Timeout);

        Assert.True(turn.IsCompleted);
        Assert.Empty(_chat.State.Entries);
        Assert.Null(_chat.State.Problem);
        Assert.NotEqual("error", _chat.State.Status);
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        Assert.Equal("Hello, there.", LastAgentText());
    }

    [Fact]
    public async Task A_request_without_choices_is_refused_and_the_chat_goes_on()
    {
        await _chat.SendAsync("badask").WaitAsync(Timeout);

        Assert.Equal("refused", LastAgentText());
        Assert.Equal("ready", _chat.State.Status);
    }

    [Fact]
    public async Task Odd_messages_from_the_agent_do_not_stop_the_chat()
    {
        // Review finding: one numeric messageId stopped all reading until the 15-minute turn timeout.
        await _chat.SendAsync("garbage").WaitAsync(Timeout);

        Assert.EndsWith("still here", LastAgentText(), StringComparison.Ordinal);
        Assert.Equal("ready", _chat.State.Status);
    }

    [Fact]
    public async Task Updates_for_another_session_are_ignored()
    {
        await _chat.SendAsync("elsewhere").WaitAsync(Timeout);

        Assert.DoesNotContain(_chat.State.Entries, e => e.Text.Contains("LEAK", StringComparison.Ordinal));
        Assert.Equal("ok", LastAgentText());
    }

    [Fact]
    public async Task A_new_chat_while_answering_stops_the_answer_first()
    {
        // Review finding: the old turn kept running into the new chat and both answers were mixed.
        var turn = _chat.SendAsync("slow");
        await WaitFor(() => _chat.State.Status == "busy" && _chat.State.Entries.Any(e => e.Kind == "agent"));

        await _chat.NewChatAsync().WaitAsync(Timeout);
        Assert.True(turn.IsCompleted);
        Assert.Empty(_chat.State.Entries);
        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.Equal(["user", "agent"], _chat.State.Entries.Select(e => e.Kind));
        Assert.Equal("Hello, there.", LastAgentText());
        Assert.Equal("ready", _chat.State.Status);
    }

    [Fact]
    public async Task Switching_agents_while_answering_stops_the_answer_first()
    {
        var turn = _chat.SendAsync("slow");
        await WaitFor(() => _chat.State.Status == "busy" && _chat.State.Entries.Any(e => e.Kind == "agent"));

        await _chat.SelectAsync("fake").WaitAsync(Timeout);

        Assert.True(turn.IsCompleted);
        Assert.Equal("idle", _chat.State.Status);
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        Assert.Equal("Hello, there.", LastAgentText());
    }

    [Fact]
    public async Task A_session_the_agent_could_not_start_is_an_error_not_stuck_starting()
    {
        // Review finding: an unexpected exception left the chat "starting" for good.
        await _chat.DisposeAsync();
        _chat = Create(noSession: true);

        await _chat.SendAsync("hi").WaitAsync(Timeout);

        Assert.Equal("error", _chat.State.Status);
        await _chat.SendAsync("hi").WaitAsync(Timeout);
        Assert.Equal("error", _chat.State.Status);
    }

    [Fact]
    public async Task Installed_agents_are_looked_up_once_not_for_every_update()
    {
        // Review finding: every streamed chunk searched the whole PATH (70 ms each) while holding the chat's lock.
        var catalog = new AgentCatalog(searchPath: _dir);
        await using var chat = new AgentChatService(catalog, new AgentChatOptions { WorkDirectory = Path.Combine(_dir, "work") });
        Assert.Null(chat.State.Agent);

        catalog.Extra.Add((new AgentOption("late", "Late agent", true, null), work => AcpConnectionTests.FakeAgent()));
        Assert.DoesNotContain(chat.State.Agents, a => a.Id == "late");

        chat.RefreshAgents();
        Assert.Contains(chat.State.Agents, a => a.Id == "late");
        Assert.Equal("late", chat.State.Agent);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, "timed out waiting");
            await Task.Delay(25);
        }
    }
}
