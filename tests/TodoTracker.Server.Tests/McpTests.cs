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
    public async Task Agent_can_schedule_follow_up_with_reminder()
    {
        var task = await Call("create_task", new() { ["title"] = "Check canary" });

        var scheduled = await Call("schedule_next_action", new() { ["taskId"] = task.GetProperty("id").GetString(), ["inMinutes"] = 90, ["message"] = "Canary results" });

        Assert.Equal(ServerFixture.T0.AddMinutes(90), scheduled.GetProperty("nextActionAt").GetDateTimeOffset());
        Assert.Equal("Canary results", scheduled.GetProperty("reminders")[0].GetProperty("message").GetString());
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
}
