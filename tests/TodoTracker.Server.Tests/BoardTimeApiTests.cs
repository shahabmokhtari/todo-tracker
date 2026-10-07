using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TodoTracker.Server.Tests;

/// <summary>The board, the archive, the timer and reports over HTTP (what the full window and AI tools use).</summary>
public sealed class BoardTimeApiTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        _client = _server.Client();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private async Task<string> Create(string title, string? parent = null) =>
        (await (await _client.PostAsJsonAsync("/api/items", new { title, parentId = parent })).Json()).Id();

    [Fact]
    public async Task Cards_move_along_the_board_and_the_tree_shows_them()
    {
        var id = await Create("Plan trip");
        var step = await Create("Book flights", id);

        var moved = await _client.PostJson($"/api/items/{id}/stage", new { stage = "doing" });
        var tree = (await _client.GetJson("/api/tree")).AsArray();

        Assert.Equal("doing", moved["stage"]!.GetValue<string>());
        var card = tree.Single(n => n!.Id() == id)!;
        Assert.Equal("doing", card["stage"]!.GetValue<string>());
        Assert.Equal(step, card["children"]![0]!.Id());
        Assert.Null(card["children"]![0]!["stage"]);
        Assert.Equal(0, card["doneCount"]!.GetValue<int>());
        Assert.Equal(1, card["totalCount"]!.GetValue<int>());
    }

    [Fact]
    public async Task New_tasks_start_in_the_inbox_unless_told_otherwise()
    {
        var inbox = await (await _client.PostAsJsonAsync("/api/items", new { title = "Thought" })).Json();
        var next = await (await _client.PostAsJsonAsync("/api/items", new { title = "Ready", stage = "next" })).Json();

        Assert.Equal("inbox", inbox["stage"]!.GetValue<string>());
        Assert.Equal("next", next["stage"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finished_tasks_can_be_archived_found_and_brought_back()
    {
        var id = await Create("Old trip");
        await _client.PostJson($"/api/items/{id}/complete");

        var archived = await _client.PostJson($"/api/items/{id}/archive");
        Assert.NotNull(archived["archivedAt"]);
        Assert.DoesNotContain((await _client.GetJson("/api/tree")).AsArray(), n => n!.Id() == id);
        Assert.Contains((await _client.GetJson("/api/tree?archived=true")).AsArray(), n => n!.Id() == id);
        Assert.Single((await _client.GetJson("/api/search?q=is:archived")).AsArray());

        await _client.PostJson($"/api/items/{id}/unarchive");
        Assert.Contains((await _client.GetJson("/api/tree")).AsArray(), n => n!.Id() == id);
    }

    [Fact]
    public async Task Everything_finished_long_enough_ago_can_be_archived_at_once()
    {
        var old = await Create("Old");
        await _client.PostJson($"/api/items/{old}/complete");
        _server.Time.Advance(TimeSpan.FromDays(20));
        var recent = await Create("Recent");
        await _client.PostJson($"/api/items/{recent}/complete");

        var result = await _client.PostJson("/api/archive", new { olderThanDays = 14 });

        Assert.Equal([old], result["archived"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task Archiving_an_open_task_says_to_finish_it_first()
    {
        var id = await Create("Open");

        var response = await _client.PostAsync(new Uri($"/api/items/{id}/archive", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task One_timer_runs_and_its_time_adds_up_on_the_task()
    {
        var id = await Create("Write report");

        var started = await _client.PostJson("/api/timer/start", new { itemId = id });
        _server.Time.Advance(TimeSpan.FromMinutes(25));
        var running = await _client.GetJson("/api/timer");
        var stopped = await _client.PostJson("/api/timer/stop");
        var item = await _client.GetJson($"/api/items/{id}");

        Assert.True(started["running"]!.GetValue<bool>());
        Assert.Equal(id, running["itemId"]!.GetValue<string>());
        Assert.Equal(25 * 60, running["elapsedSeconds"]!.GetValue<long>());
        Assert.False(stopped["running"]!.GetValue<bool>());
        Assert.Equal(25 * 60, item["timeSpentSeconds"]!.GetValue<long>());
        Assert.Equal("doing", item["stage"]!.GetValue<string>());
        var entry = item["timeEntries"]!.AsArray().Single()!;
        Assert.Equal("manual", entry["source"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_dashboard_says_what_is_being_timed()
    {
        var id = await Create("Write report");
        await _client.PostJson("/api/timer/start", new { itemId = id });

        var dashboard = await _client.GetJson("/api/dashboard");

        Assert.Equal(id, dashboard["timer"]!["itemId"]!.GetValue<string>());
        Assert.True(dashboard["now"]!.AsArray().Single(c => c!.Id() == id)!["timing"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Time_can_be_added_corrected_and_removed_by_hand()
    {
        var id = await Create("Meeting");
        var start = ServerFixture.T0.AddHours(-2);

        var entry = await _client.PostJson($"/api/items/{id}/time", new { start, end = start.AddMinutes(90) });
        var fixedEntry = await (await _client.PutAsJsonAsync($"/api/items/{id}/time/{entry.Id()}", new { start, end = start.AddMinutes(60) })).Json();
        Assert.Equal(3600, fixedEntry["seconds"]!.GetValue<long>());
        Assert.Equal(3600, (await _client.GetJson($"/api/items/{id}"))["timeSpentSeconds"]!.GetValue<long>());

        (await _client.DeleteAsync(new Uri($"/api/items/{id}/time/{entry.Id()}", UriKind.Relative))).EnsureSuccessStatusCode();
        Assert.Equal(0, (await _client.GetJson($"/api/items/{id}"))["timeSpentSeconds"]!.GetValue<long>());

        var bad = await _client.PostAsJsonAsync($"/api/items/{id}/time", new { start, end = start });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_focus_session_is_timed()
    {
        var id = await Create("Write report");

        await _client.PostJson("/api/pomodoro/start", new { itemId = id });
        _server.Time.Advance(TimeSpan.FromMinutes(10));
        await _client.PostJson("/api/pomodoro/pause");

        var entry = (await _client.GetJson($"/api/items/{id}"))["timeEntries"]!.AsArray().Single()!;
        Assert.Equal("focus", entry["source"]!.GetValue<string>());
        Assert.Equal(600, entry["seconds"]!.GetValue<long>());
    }

    [Fact]
    public async Task Reports_say_where_the_time_went()
    {
        var id = await Create("Write report");
        await _client.PostJson($"/api/items/{id}/time", new { start = ServerFixture.T0, end = ServerFixture.T0.AddHours(2) });
        await _client.PostJson($"/api/items/{id}/complete");

        var report = await _client.GetJson("/api/reports?from=2026-01-01&to=2026-01-07");

        Assert.Equal(7200, report["trackedSeconds"]!.GetValue<long>());
        Assert.Equal(1, report["completed"]!.GetValue<int>());
        Assert.Equal(7, report["days"]!.AsArray().Count);
        Assert.Equal("2026-01-05", report["days"]![4]!["date"]!.GetValue<string>());
        Assert.Equal("Write report", report["tasks"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("Work", report["groups"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(7, report["hourGrid"]!.AsArray().Count);
        Assert.Single(report["timeline"]!.AsArray());

        var bad = await _client.GetAsync(new Uri("/api/reports?from=2026-01-07&to=2026-01-01", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Reports_default_to_the_last_four_weeks()
    {
        var report = await _client.GetJson("/api/reports");

        Assert.Equal("2026-01-05", report["to"]!.GetValue<string>());
        Assert.Equal(28, report["days"]!.AsArray().Count);
    }
}
