using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        _client = _server.Client();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private Task<JsonNode> Dashboard(string? group = null) => _client.GetJson(group is null ? "/api/dashboard" : $"/api/dashboard?group={group}");

    [Fact]
    public async Task Created_task_shows_up_as_focus_on_dashboard()
    {
        var created = await _client.PostAsJsonAsync("/api/items", new { title = "Roll out feature X", priority = "critical", details = "A then B" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Json();

        var dashboard = await Dashboard();

        Assert.Equal("Roll out feature X", item["title"]!.GetValue<string>());
        Assert.Equal("critical", item["priority"]!.GetValue<string>());
        Assert.Equal(item.Id(), dashboard["focus"]!.Id());
        Assert.Equal(["Roll out feature X"], dashboard["now"].Titles());
        Assert.Equal(ServerFixture.T0, dashboard["serverTime"]!.GetValue<DateTimeOffset>());
    }

    [Fact]
    public async Task Invalid_input_maps_to_problem_details()
    {
        var blank = await _client.PostAsJsonAsync("/api/items", new { title = " " });
        var missing = await _client.PostAsJsonAsync($"/api/items/{Guid.NewGuid()}/complete", new { });
        var badPriority = await _client.PostAsJsonAsync("/api/items", new { title = "x", priority = "urgent-ish" });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("application/problem+json", blank.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badPriority.StatusCode);
    }

    [Fact]
    public async Task Rollout_steps_are_gated_and_out_of_order_completion_conflicts()
    {
        var feature = await _client.PostJson("/api/items", new { title = "Feature A" });
        var steps = await _client.PostJson($"/api/items/{feature.Id()}/steps", new { titles = new[] { "Ring 0", "Ring 1", "Ring 2" }, stepDelayMinutes = 1440 });
        var ids = steps.AsArray().Select(s => s!.Id()).ToList();

        var conflict = await _client.PostAsJsonAsync($"/api/items/{ids[1]}/complete", new { });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        await _client.PostJson($"/api/items/{ids[0]}/complete");
        var dashboard = await Dashboard();
        Assert.Empty(dashboard["now"]!.AsArray());
        var waiting = Assert.Single(dashboard["waiting"]!.AsArray())!;
        Assert.Equal("Ring 1", waiting["title"]!.GetValue<string>());
        Assert.Equal(ServerFixture.T0.AddDays(1), waiting["wakeAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal(["Feature A"], waiting["breadcrumb"]!.AsArray().Select(b => b!.GetValue<string>()));

        _server.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(["Ring 1"], (await Dashboard())["now"].Titles());
    }

    [Fact]
    public async Task Schedule_defers_with_reminder_and_clear_brings_it_back()
    {
        var item = await _client.PostJson("/api/items", new { title = "Feature B" });

        var scheduled = await _client.PostJson($"/api/items/{item.Id()}/schedule", new { inMinutes = 60, notify = true, message = "Continue B" });

        Assert.Equal(ServerFixture.T0.AddHours(1), scheduled["nextActionAt"]!.GetValue<DateTimeOffset>());
        Assert.Equal("Continue B", scheduled["reminders"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(["Feature B"], (await Dashboard())["waiting"].Titles());

        await _client.PostJson($"/api/items/{item.Id()}/schedule", new { clear = true });
        Assert.Equal(["Feature B"], (await Dashboard())["now"].Titles());
    }

    [Fact]
    public async Task Schedule_requires_a_time()
    {
        var item = await _client.PostJson("/api/items", new { title = "Feature B" });
        var response = await _client.PostAsJsonAsync($"/api/items/{item.Id()}/schedule", new { notify = true });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reminders_can_be_added_and_dismissed()
    {
        var item = await _client.PostJson("/api/items", new { title = "Feature B" });
        var reminder = await _client.PostJson($"/api/items/{item.Id()}/reminders", new { at = ServerFixture.T0.AddMinutes(-1), message = "Now!" });

        var dashboard = await Dashboard();
        Assert.True(dashboard["focus"]!["needsAttention"]!.GetValue<bool>());
        Assert.Equal("Now!", dashboard["focus"]!["reminderMessage"]!.GetValue<string>());

        await _client.PostJson($"/api/items/{item.Id()}/reminders/{reminder.Id()}/dismiss");
        Assert.False((await Dashboard())["focus"]!["needsAttention"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Notes_record_actor_and_source_and_appear_in_recent_notes()
    {
        var item = await _client.PostJson("/api/items", new { title = "Research" });
        var browser = _server.Client(actor: "browser-extension");

        var note = await browser.PostJson($"/api/items/{item.Id()}/notes", new { text = "Found the doc", sourceUrl = "https://learn.example/doc", sourceTitle = "Doc" });

        Assert.Equal("browser", note["authorKind"]!.GetValue<string>());
        var recent = (await Dashboard())["recentNotes"]![0]!;
        Assert.Equal("Found the doc", recent["text"]!.GetValue<string>());
        Assert.Equal("Research", recent["itemTitle"]!.GetValue<string>());
        Assert.Equal("https://learn.example/doc", recent["sourceUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Agent_actor_header_is_attributed()
    {
        var item = await _client.PostJson("/api/items", new { title = "Research" });
        var agent = _server.Client(actor: "agent:copilot-cli");

        var note = await agent.PostJson($"/api/items/{item.Id()}/notes", new { text = "Opened PR" });

        Assert.Equal("agent", note["authorKind"]!.GetValue<string>());
        Assert.Equal("Agent: copilot-cli", note["author"]!.GetValue<string>());
    }

    [Fact]
    public async Task Patch_can_clear_step_delay_and_rejects_zero()
    {
        var item = await _client.PostJson("/api/items", new { title = "Rollout", sequential = true, stepDelayMinutes = 60 });

        var zero = await _client.PatchAsJsonAsync($"/api/items/{item.Id()}", new { stepDelayMinutes = 0 });
        var cleared = await (await _client.PatchAsJsonAsync($"/api/items/{item.Id()}", new { clearStepDelay = true })).Json();

        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Null(cleared["stepDelayMinutes"]);
    }

    [Fact]
    public async Task Patch_updates_fields_and_delete_removes()
    {
        var item = await _client.PostJson("/api/items", new { title = "Draft", deadline = ServerFixture.T0.AddDays(1) });

        var patched = await (await _client.PatchAsJsonAsync($"/api/items/{item.Id()}", new { title = "Final", priority = "high", clearDeadline = true })).Json();
        Assert.Equal("Final", patched["title"]!.GetValue<string>());
        Assert.Equal("high", patched["priority"]!.GetValue<string>());
        Assert.Null(patched["deadline"]);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/items/{item.Id()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/items/{item.Id()}")).StatusCode);
    }

    [Fact]
    public async Task Complete_and_reopen_round_trip()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });

        var done = await _client.PostJson($"/api/items/{item.Id()}/complete");
        Assert.Equal("done", done["state"]!.GetValue<string>());
        Assert.Empty((await Dashboard())["now"]!.AsArray());

        var reopened = await _client.PostJson($"/api/items/{item.Id()}/reopen");
        Assert.Equal("actionable", reopened["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Item_detail_includes_children_notes_and_path()
    {
        var parent = await _client.PostJson("/api/items", new { title = "Feature X" });
        var child = await _client.PostJson("/api/items", new { title = "Feature A", parentId = parent.Id() });
        await _client.PostJson($"/api/items/{child.Id()}/notes", new { text = "started" });

        var detail = await _client.GetJson($"/api/items/{parent.Id()}");
        var tree = await _client.GetJson("/api/items");

        Assert.Equal("container", detail["state"]!.GetValue<string>());
        Assert.Equal(["Feature A"], detail["children"].Titles());
        Assert.Equal("started", detail["children"]![0]!["notes"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(["Feature X", "Feature A"], detail["children"]![0]!["path"]!.AsArray().Select(p => p!.GetValue<string>()));
        Assert.Equal(["Feature X"], tree.Titles());
    }

    [Fact]
    public async Task Quick_capture_parses_priority_and_defer()
    {
        var item = await _client.PostJson("/api/capture", new { text = "Check canary !! @2h" });

        Assert.Equal("Check canary", item["title"]!.GetValue<string>());
        Assert.Equal("critical", item["priority"]!.GetValue<string>());
        Assert.Equal(ServerFixture.T0.AddHours(2), item["nextActionAt"]!.GetValue<DateTimeOffset>());
        Assert.Single(item["reminders"]!.AsArray());
    }

    [Fact]
    public async Task Groups_filter_dashboard_and_support_crud_and_move()
    {
        var dashboard = await Dashboard();
        var groups = dashboard["groups"]!.AsArray();
        Assert.Equal(["Work", "Personal"], groups.Select(g => g!["name"]!.GetValue<string>()));
        var work = groups[0]!.Id();
        var personal = groups[1]!.Id();

        await _client.PostJson("/api/items", new { title = "Work thing" });
        var dentist = await _client.PostJson("/api/items", new { title = "Dentist", groupId = personal });
        var hobby = await _client.PostJson("/api/groups", new { name = "Hobby", color = "#f97316" });

        Assert.Equal(["Dentist"], (await Dashboard(personal))["now"].Titles());
        Assert.Equal(1, (await Dashboard())["groups"]![1]!["now"]!.GetValue<int>());

        await _client.PostJson($"/api/items/{dentist.Id()}/move", new { groupId = hobby.Id() });
        Assert.Empty((await Dashboard(personal))["now"]!.AsArray());

        var renamed = await (await _client.PatchAsJsonAsync($"/api/groups/{hobby.Id()}", new { name = "Fun", color = "#f97316" })).Json();
        Assert.Equal("Fun", renamed["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/groups/{hobby.Id()}?moveTo={work}")).StatusCode);
        Assert.Equal(["Dentist", "Work thing"], (await Dashboard(work))["now"].Titles().Order().ToList());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/groups", new { name = "work" })).StatusCode);
    }

    [Fact]
    public async Task Timeline_lists_activity_newest_first_for_item_or_board()
    {
        var item = await _client.PostJson("/api/items", new { title = "Feature X" });
        _server.Time.Advance(TimeSpan.FromMinutes(1));
        await _client.PostJson($"/api/items/{item.Id()}/notes", new { text = "did a thing" });

        var timeline = await _client.GetJson($"/api/items/{item.Id()}/timeline");
        var all = await _client.GetJson("/api/timeline?limit=1");

        Assert.Equal(["noteAdded", "created"], timeline.AsArray().Select(t => t!["kind"]!.GetValue<string>()));
        Assert.Equal("Feature X", timeline[0]!["itemTitle"]!.GetValue<string>());
        Assert.Single(all.AsArray());
    }

    [Fact]
    public async Task Pomodoro_can_be_driven_through_the_api()
    {
        var item = await _client.PostJson("/api/items", new { title = "Deep work" });

        var started = await _client.PostJson("/api/pomodoro/start", new { itemId = item.Id() });
        Assert.Equal("focus", started["phase"]!.GetValue<string>());
        Assert.Equal("Deep work", started["itemTitle"]!.GetValue<string>());
        Assert.Equal(1500, started["remainingSeconds"]!.GetValue<int>());

        _server.Time.Advance(TimeSpan.FromMinutes(5));
        var paused = await _client.PostJson("/api/pomodoro/pause");
        Assert.False(paused["running"]!.GetValue<bool>());
        Assert.Equal(1200, paused["remainingSeconds"]!.GetValue<int>());

        Assert.True((await _client.PostJson("/api/pomodoro/resume"))["running"]!.GetValue<bool>());
        Assert.Equal("shortBreak", (await _client.PostJson("/api/pomodoro/skip"))["phase"]!.GetValue<string>());
        Assert.Equal("idle", (await _client.PostJson("/api/pomodoro/reset"))["phase"]!.GetValue<string>());
        Assert.Equal("idle", (await Dashboard())["pomodoro"]!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task Export_returns_the_versioned_board_document()
    {
        await _client.PostJson("/api/items", new { title = "Backup me" });

        var export = await _client.GetJson("/api/export");

        Assert.Equal(1, export["schemaVersion"]!.GetValue<int>());
        Assert.Equal(["Backup me"], export["items"].Titles());
    }

    [Fact]
    public async Task Teams_settings_are_validated_and_masked()
    {
        var bad = await _client.PutAsJsonAsync("/api/settings", new { teamsWebhookUrl = "http://insecure.example/hook" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var saved = await (await _client.PutAsJsonAsync("/api/settings", new { teamsWebhookUrl = "https://prod.example.webhook.office.com/hook?sig=secret" })).Json();
        var read = await _client.GetJson("/api/settings");

        Assert.True(saved["teamsConfigured"]!.GetValue<bool>());
        Assert.True(read["teamsConfigured"]!.GetValue<bool>());
        Assert.DoesNotContain("secret", read.ToJsonString(), StringComparison.Ordinal);

        var cleared = await (await _client.PutAsJsonAsync("/api/settings", new { teamsWebhookUrl = "" })).Json();
        Assert.False(cleared["teamsConfigured"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Connection_info_exposes_mcp_config_for_agents()
    {
        var info = await _client.GetJson("/api/connection");

        Assert.EndsWith("/mcp", info["mcpUrl"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(ServerFixture.Token, info["mcpConfig"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Board_is_persisted_to_the_data_directory()
    {
        await _client.PostJson("/api/items", new { title = "Durable" });

        var json = await _server.Store.ReadAsync(BoardSerializer.Serialize);

        Assert.Contains("Durable", json, StringComparison.Ordinal);
        Assert.Contains("Durable", await File.ReadAllTextAsync(Path.Combine(_server.DataDirectory, "board.json")), StringComparison.Ordinal);
    }
}
