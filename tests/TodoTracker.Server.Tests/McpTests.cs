using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

public sealed class McpTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private McpClient _mcp = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        var http = _server.Client();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http,
            ownsHttpClient: true);
        _mcp = await McpClient.CreateAsync(transport, new McpClientOptions { ClientInfo = new Implementation { Name = "copilot-test", Version = "1.0" } });
    }

    public async ValueTask DisposeAsync()
    {
        await _mcp.DisposeAsync();
        await _server.DisposeAsync();
    }

    private async Task<JsonElement> Call(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp.CallToolAsync(tool, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.False(result.IsError ?? false, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Exposes_task_tools_without_destructive_delete()
    {
        var tools = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.Contains("get_dashboard", tools);
        Assert.Contains("create_task", tools);
        Assert.Contains("add_steps", tools);
        Assert.Contains("add_note", tools);
        Assert.Contains("schedule_next_action", tools);
        Assert.Contains("complete_task", tools);
        Assert.Contains("get_report", tools);
        Assert.DoesNotContain(tools, t => t.Contains("delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Agent_can_plan_a_rollout_and_log_progress_with_attribution()
    {
        var feature = await Call("create_task", new() { ["title"] = "Roll out feature X", ["priority"] = "high", ["group"] = "work" });
        var featureId = feature.GetProperty("id").GetString()!;
        var steps = await Call("add_steps", new() { ["parentId"] = featureId, ["steps"] = new[] { "Ring 0", "Ring 1" }, ["stepDelayHours"] = 24 });
        var ring0 = steps[0].GetProperty("id").GetString()!;

        await Call("add_note", new() { ["taskId"] = ring0, ["text"] = "Deployed ring 0", ["sourceUrl"] = "https://dev.example/pr/7" });
        await Call("complete_task", new() { ["taskId"] = ring0 });
        var dashboard = await Call("get_dashboard", new());

        Assert.Equal("Ring 1", dashboard.GetProperty("waiting")[0].GetProperty("title").GetString());
        var note = await _server.Store.ReadAsync(b => b.Get(Guid.Parse(ring0)).Notes.Single());
        Assert.Equal(ActorKind.Agent, note.Author.Kind);
        Assert.Equal("copilot-test", note.Author.Name);
    }

    [Fact]
    public async Task Agent_can_put_what_matters_most_first()
    {
        await Call("create_task", new() { ["title"] = "Urgent", ["priority"] = "critical" });
        var bank = await Call("create_task", new() { ["title"] = "Call the bank" });

        var dashboard = await Call("put_first", new() { ["taskIds"] = new[] { bank.GetProperty("id").GetString() } });

        Assert.Equal("Call the bank", dashboard.GetProperty("focus").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Agent_can_schedule_follow_up_with_reminder()
    {
        var task = await Call("create_task", new() { ["title"] = "Check canary" });

        var scheduled = await Call("schedule_next_action", new() { ["taskId"] = task.GetProperty("id").GetString(), ["inMinutes"] = 90, ["message"] = "Canary results" });

        Assert.Equal(ServerFixture.T0.AddMinutes(90), scheduled.GetProperty("nextActionAt").GetDateTimeOffset());
        Assert.Equal("Canary results", scheduled.GetProperty("reminders")[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task Agent_can_snooze_in_words_or_until_another_task_is_done()
    {
        var keys = await Call("create_task", new() { ["title"] = "Get the keys" });
        var move = await Call("create_task", new() { ["title"] = "Move in" });
        var trip = await Call("create_task", new() { ["title"] = "Plan trip" });

        var later = await Call("schedule_next_action", new() { ["taskId"] = trip.GetProperty("id").GetString(), ["when"] = "next week" });
        var waiting = await Call("wait_for_task", new() { ["taskId"] = move.GetProperty("id").GetString(), ["afterTaskId"] = keys.GetProperty("id").GetString() });

        Assert.Equal(new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero), later.GetProperty("nextActionAt").GetDateTimeOffset());
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());
        Assert.Equal("Get the keys", waiting.GetProperty("waitingForTitle").GetString());
    }

    [Fact]
    public async Task Agent_can_move_cards_time_work_and_read_the_time_report()
    {
        var task = await Call("create_task", new() { ["title"] = "Write report" });
        var id = task.GetProperty("id").GetString()!;
        Assert.Equal("inbox", task.GetProperty("stage").GetString());

        var moved = await Call("move_card", new() { ["taskId"] = id, ["stage"] = "next" });
        var timer = await Call("start_timer", new() { ["taskId"] = id });
        _server.Time.Advance(TimeSpan.FromMinutes(30));
        var stopped = await Call("stop_timer", new());
        await Call("log_time", new() { ["taskId"] = id, ["start"] = ServerFixture.T0.AddHours(-2), ["minutes"] = 45 });
        var report = await Call("get_time_report", new() { ["from"] = "2026-01-05", ["to"] = "2026-01-05" });

        Assert.Equal("next", moved.GetProperty("stage").GetString());
        Assert.True(timer.GetProperty("running").GetBoolean());
        Assert.False(stopped.GetProperty("running").GetBoolean());
        Assert.Equal((30 + 45) * 60, report.GetProperty("trackedSeconds").GetInt64());
    }

    [Fact]
    public async Task Agent_can_archive_finished_tasks()
    {
        var task = await Call("create_task", new() { ["title"] = "Old trip" });
        var id = task.GetProperty("id").GetString()!;
        await Call("complete_task", new() { ["taskId"] = id });

        var archived = await Call("archive_task", new() { ["taskId"] = id });
        var back = await Call("unarchive_task", new() { ["taskId"] = id });

        Assert.Equal(JsonValueKind.String, archived.GetProperty("archivedAt").ValueKind);
        Assert.False(back.TryGetProperty("archivedAt", out var cleared) && cleared.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Tool_errors_are_reported_not_thrown()
    {
        var result = await _mcp.CallToolAsync("complete_task", new Dictionary<string, object?> { ["taskId"] = Guid.NewGuid().ToString() });

        Assert.True(result.IsError);
        Assert.Contains("not found", string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Report_contains_timeline_entries()
    {
        var task = await Call("create_task", new() { ["title"] = "Feature B" });
        await Call("add_note", new() { ["taskId"] = task.GetProperty("id").GetString(), ["text"] = "Halfway" });

        var report = await Call("get_report", new() { ["taskId"] = task.GetProperty("id").GetString() });

        Assert.Equal("Halfway", report.GetProperty("timeline")[0].GetProperty("summary").GetString());
        Assert.Equal("Agent: copilot-test", report.GetProperty("timeline")[0].GetProperty("actor").GetString());
    }

    [Fact]
    public async Task Unknown_group_or_priority_is_an_error()
    {
        var badGroup = await _mcp.CallToolAsync("create_task", new Dictionary<string, object?> { ["title"] = "x", ["group"] = "nope" });
        var badPriority = await _mcp.CallToolAsync("create_task", new Dictionary<string, object?> { ["title"] = "x", ["priority"] = "meh" });

        Assert.True(badGroup.IsError);
        Assert.True(badPriority.IsError);
    }

    [Fact]
    public async Task Agents_organize_with_tags_labels_search_and_moves()
    {
        var tools = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.Contains("search_tasks", tools);
        Assert.Contains("move_task", tools);
        Assert.Contains("list_labels", tools);

        var project = await Call("create_task", new() { ["title"] = "Upgrade cluster", ["tags"] = new[] { "infra/k8s" }, ["labels"] = new[] { "Deep work" } });
        var idea = await Call("create_task", new() { ["title"] = "Drain nodes first" });
        await Call("move_task", new() { ["taskId"] = idea.GetProperty("id").GetString(), ["parentId"] = project.GetProperty("id").GetString() });
        var updated = await Call("update_task", new() { ["taskId"] = idea.GetProperty("id").GetString(), ["tags"] = new[] { "risky" } });
        var found = await Call("search_tasks", new() { ["query"] = "#infra" });

        Assert.Equal("infra/k8s", project.GetProperty("tags")[0].GetString());
        Assert.Equal("Deep work", project.GetProperty("labels")[0].GetProperty("name").GetString());
        Assert.Equal("risky", updated.GetProperty("tags")[0].GetString());
        Assert.Equal(["Upgrade cluster", "Drain nodes first"], found.EnumerateArray().Select(t => t.GetProperty("title").GetString()));
        Assert.Equal("Deep work", (await Call("list_labels", new()))[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Agents_can_attach_text_and_keep_a_rich_html_version()
    {
        var tools = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.DoesNotContain("attach_file", tools);
        var task = await Call("create_task", new() { ["title"] = "Incident review" });
        var id = task.GetProperty("id").GetString();

        var attachment = await Call("attach_text", new() { ["taskId"] = id, ["fileName"] = "timeline.md", ["text"] = "# Timeline\n- 10:00 alert" });
        await Call("set_rich_html", new() { ["taskId"] = id, ["html"] = "<table><tr><td>MTTR</td><td>42m</td></tr></table>" });
        var rich = await _mcp.CallToolAsync("get_rich_html", new Dictionary<string, object?> { ["taskId"] = id });

        Assert.Equal("timeline.md", attachment.GetProperty("fileName").GetString());
        Assert.Contains("MTTR", string.Concat(rich.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.Ordinal);
        var stored = await _server.Store.ReadAsync(b => b.Get(Guid.Parse(id!)).Attachments.Single());
        Assert.Equal(ActorKind.Agent, stored.AddedBy.Kind);
    }

    [Fact]
    public async Task Vault_info_teaches_agents_the_file_format()
    {
        var info = await Call("vault_info", new());

        Assert.Equal(_server.VaultDirectory, info.GetProperty("path").GetString());
        Assert.Contains("Todo Tracker vault", info.GetProperty("guide").GetString(), StringComparison.Ordinal);
    }
}
