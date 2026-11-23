using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using OneThing.Core;

namespace OneThing.Server.Tests;

/// <summary>The MCP tools for groups, reminders, notes, order, focus, the plan, labels, history and export.</summary>
public sealed class McpMoreToolsTests : IAsyncLifetime
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

    private async Task<string> Fails(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp.CallToolAsync(tool, args);
        Assert.True(result.IsError ?? false);
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private async Task<string> NewTask(string title, string? group = null) =>
        (await Call("create_task", new() { ["title"] = title, ["group"] = group })).GetProperty("id").GetString()!;

    [Fact]
    public async Task An_agent_makes_a_group_and_puts_a_task_in_it()
    {
        var group = await Call("create_group", new() { ["name"] = "Garden", ["color"] = "#22c55e" });
        var task = await Call("create_task", new() { ["title"] = "Plant tulips", ["group"] = "garden" });

        Assert.Equal(group.GetProperty("id").GetString(), task.GetProperty("groupId").GetString());
        var groups = await Call("list_groups", new());
        Assert.Contains(groups.EnumerateArray(), g => g.GetProperty("name").GetString() == "Garden" && g.GetProperty("color").GetString() == "#22c55e");
        var entry = await _server.Store.ReadAsync(b => b.Activity.Last(a => a.Kind == ActivityKind.GroupChanged));
        Assert.Equal("copilot-test", entry.Actor.Name);
    }

    [Fact]
    public async Task An_unknown_group_is_an_error_that_says_how_to_make_it()
    {
        var error = await Fails("create_task", new() { ["title"] = "Plant tulips", ["group"] = "Gardn" });

        Assert.Contains("create_group", error, StringComparison.Ordinal);
        Assert.Equal(2, await _server.Store.ReadAsync(b => b.Groups.Count));
    }

    [Fact]
    public async Task Update_group_renames_and_recolors_and_keeps_what_isnt_given()
    {
        await Call("create_group", new() { ["name"] = "Garden", ["color"] = "#22c55e" });

        var renamed = await Call("update_group", new() { ["group"] = "Garden", ["name"] = "Back yard" });
        var recolored = await Call("update_group", new() { ["group"] = "Back yard", ["color"] = "" });

        Assert.Equal("#22c55e", renamed.GetProperty("color").GetString());
        Assert.Equal("Back yard", recolored.GetProperty("name").GetString());
        Assert.False(recolored.TryGetProperty("color", out var color) && color.ValueKind != JsonValueKind.Null);
    }

    private async Task<string> Text(string tool, Dictionary<string, object?> args)
    {
        var result = await _mcp.CallToolAsync(tool, args);
        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.False(result.IsError ?? false, text);
        return text;
    }

    [Fact]
    public async Task Capture_task_reads_quick_capture_words()
    {
        var item = await Call("capture_task", new() { ["text"] = "Renew passport !! due:7d #admin @tomorrow" });

        Assert.Equal("Renew passport", item.GetProperty("title").GetString());
        Assert.Equal("critical", item.GetProperty("priority").GetString());
        Assert.Equal("admin", item.GetProperty("tags")[0].GetString());
        Assert.Equal("waiting", item.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Reminders_are_dismissed_and_a_snooze_is_cleared()
    {
        var id = await NewTask("Call the bank");
        var reminded = await Call("add_reminder", new() { ["taskId"] = id, ["message"] = "Ask about fees", ["inMinutes"] = 60 });
        var reminder = reminded.GetProperty("reminders")[0].GetProperty("id").GetString();

        var dismissed = await Call("dismiss_reminder", new() { ["taskId"] = id, ["reminderId"] = reminder });
        Assert.NotEqual(JsonValueKind.Null, dismissed.GetProperty("reminders")[0].GetProperty("dismissedAt").ValueKind);

        await Call("schedule_next_action", new() { ["taskId"] = id, ["when"] = "tomorrow" });
        var back = await Call("clear_snooze", new() { ["taskId"] = id });
        Assert.Equal("actionable", back.GetProperty("state").GetString());
    }

    [Fact]
    public async Task A_note_is_corrected_and_keeps_its_author()
    {
        var id = await NewTask("Ship release");
        var note = await Call("add_note", new() { ["taskId"] = id, ["text"] = "Deployed ring 0" });

        var updated = await Call("update_note", new() { ["taskId"] = id, ["noteId"] = note.GetProperty("id").GetString(), ["text"] = "Deployed ring 0 and 1" });

        Assert.Equal("Deployed ring 0 and 1", updated.GetProperty("text").GetString());
        Assert.Equal(note.GetProperty("at").GetString(), updated.GetProperty("at").GetString());
    }

    [Fact]
    public async Task An_agent_can_not_rewrite_the_users_note()
    {
        var id = await NewTask("Ship release");
        var yours = await _server.Store.UpdateAsync(b => b.AddNote(Guid.Parse(id), "Waiting for legal", Actor.User, ServerFixture.T0).Id);

        var refused = await _mcp.CallToolAsync("update_note", new Dictionary<string, object?> { ["taskId"] = id, ["noteId"] = yours.ToString(), ["text"] = "Legal said yes" });

        Assert.True(refused.IsError);
        Assert.Contains("add a new note instead", string.Concat(refused.Content.OfType<TextContentBlock>().Select(c => c.Text)), StringComparison.Ordinal);
        Assert.Equal("Waiting for legal", await _server.Store.ReadAsync(b => b.Get(Guid.Parse(id)).Notes.Single().Text));
    }

    [Fact]
    public async Task Do_now_is_arranged_and_subtasks_reordered()
    {
        var a = await NewTask("Alpha");
        var b = await NewTask("Beta");

        var dashboard = await Call("arrange_now", new() { ["taskIds"] = new[] { b, a } });
        Assert.Equal("Beta", dashboard.GetProperty("focus").GetProperty("title").GetString());

        var parent = await NewTask("Trip");
        var one = (await Call("create_task", new() { ["title"] = "Flights", ["parentId"] = parent })).GetProperty("id").GetString();
        var two = (await Call("create_task", new() { ["title"] = "Hotel", ["parentId"] = parent })).GetProperty("id").GetString();
        await Call("reorder_task", new() { ["taskId"] = two, ["beforeTaskId"] = one });
        var trip = await Call("get_task", new() { ["taskId"] = parent });
        Assert.Equal(["Hotel", "Flights"], trip.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task Logged_time_is_corrected()
    {
        var id = await NewTask("Meeting");
        var entry = await Call("log_time", new() { ["taskId"] = id, ["start"] = "2026-01-05T07:00:00Z", ["minutes"] = 30 });

        var updated = await Call("update_time_entry", new() { ["taskId"] = id, ["entryId"] = entry.GetProperty("id").GetString(), ["start"] = "2026-01-05T07:00:00Z", ["minutes"] = 45 });

        Assert.Equal(45 * 60, updated.GetProperty("seconds").GetInt64());
    }

    [Fact]
    public async Task The_focus_timer_starts_on_a_task_and_resets()
    {
        var id = await NewTask("Write the draft");

        var started = await Call("focus_timer", new() { ["action"] = "start", ["taskId"] = id });
        Assert.Equal("focus", started.GetProperty("phase").GetString());
        Assert.Equal("Write the draft", started.GetProperty("itemTitle").GetString());
        Assert.False((await Call("focus_timer", new() { ["action"] = "pause" })).GetProperty("running").GetBoolean());
        Assert.Equal("idle", (await Call("focus_timer", new() { ["action"] = "reset" })).GetProperty("phase").GetString());
        Assert.Contains("start, pause", await Fails("focus_timer", new() { ["action"] = "explode" }), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_plan_shows_planned_blocks_and_its_settings()
    {
        var id = await NewTask("Deep work");
        await Call("plan_task", new() { ["taskId"] = id, ["start"] = "2026-01-05T14:00:00Z", ["minutes"] = 60 });

        var plan = await Call("get_plan", new() { ["from"] = "2026-01-05", ["days"] = 1 });

        Assert.Equal("Deep work", plan.GetProperty("blocks")[0].GetProperty("title").GetString());
        Assert.Equal("week", plan.GetProperty("settings").GetProperty("view").GetString());
    }

    [Fact]
    public async Task Labels_are_made_and_changed_and_tags_listed()
    {
        await Call("create_label", new() { ["name"] = "Deep work", ["color"] = "#7c3aed" });
        var label = await Call("update_label", new() { ["name"] = "deep work", ["newName"] = "Focus" });
        await Call("capture_task", new() { ["text"] = "Taxes #admin" });

        Assert.Equal("Focus", label.GetProperty("name").GetString());
        Assert.Equal("#7c3aed", label.GetProperty("color").GetString());
        var tags = await Call("list_tags", new());
        Assert.Equal("admin", tags[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_board_exports_and_a_text_attachment_reads_back()
    {
        var id = await NewTask("Taxes");
        var attachment = await Call("attach_text", new() { ["taskId"] = id, ["fileName"] = "notes.md", ["text"] = "paid 42" });

        Assert.Equal("paid 42", await Text("read_attachment", new() { ["taskId"] = id, ["attachmentId"] = attachment.GetProperty("id").GetString() }));
        Assert.Contains("Taxes", await Text("export_board", new()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_changes_its_own_hook_and_it_waits_for_approval_again()
    {
        var hook = await Call("add_hook", new() { ["event"] = "taskCompleted", ["bash"] = "true" });

        var changed = await Call("update_hook", new() { ["hookId"] = hook.GetProperty("id").GetString(), ["name"] = "Log it" });

        Assert.Equal("Log it", changed.GetProperty("hook").GetProperty("name").GetString());
        Assert.True(changed.GetProperty("hook").GetProperty("needsApproval").GetBoolean());
    }

    [Fact]
    public async Task There_is_still_no_tool_that_deletes_or_changes_settings()
    {
        var tools = (await _mcp.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.DoesNotContain(tools, t => t.Contains("delete", StringComparison.Ordinal) || t.Contains("setting", StringComparison.Ordinal) || t.Contains("plugin", StringComparison.Ordinal));
    }
}
