using System.Net;
using System.Net.Http.Json;

namespace OneThing.Server.Tests;

public sealed class HistoryApiTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync(o => o.EnableHistory = true);
        _client = _server.Client();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Versions_can_be_listed_viewed_and_restored()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });
        await _client.PostJson("/api/history/commit");
        await (await _client.PatchAsJsonAsync($"/api/items/{item.Id()}", new { title = "Oops" })).Json();
        await _client.PostJson("/api/history/commit");

        var versions = await _client.GetJson($"/api/items/{item.Id()}/history");
        var first = versions.AsArray()[^1]!["id"]!.GetValue<string>();
        var old = await _client.GetStringAsync($"/api/items/{item.Id()}/history/{first}");
        await _client.PostJson($"/api/items/{item.Id()}/history/{first}/restore");

        Assert.Equal(2, versions.AsArray().Count);
        Assert.Contains("# Ship", old, StringComparison.Ordinal);
        Assert.Equal("Ship", (await _client.GetJson($"/api/items/{item.Id()}"))["title"]!.GetValue<string>());
        Assert.NotEmpty((await _client.GetJson("/api/history")).AsArray());
    }

    [Fact]
    public async Task Folders_left_out_of_history_are_listed_so_the_app_can_say_so()
    {
        Directory.CreateDirectory(Path.Combine(_server.VaultDirectory, "Work", "Project", ".git"));
        await File.WriteAllTextAsync(Path.Combine(_server.VaultDirectory, "Work", "Project", "Plan.md"), "# Plan\n");
        await _client.PostJson("/api/items", new { title = "Ship" });
        await _client.PostJson("/api/history/commit");

        var skipped = await _client.GetJson("/api/history/skipped");

        Assert.Equal(["Work/Project"], skipped.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task Bad_version_ids_are_rejected()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"/api/items/{item.Id()}/history/not-a-version")).StatusCode);
    }

    [Fact]
    public async Task Agents_see_and_restore_versions_through_mcp_tools()
    {
        var tools = await McpHelpers.ToolNamesAsync(_server);

        Assert.Contains("task_history", tools);
        Assert.Contains("restore_task_version", tools);
    }
}
