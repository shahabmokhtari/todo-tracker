using System.Net;
using System.Net.Http.Json;

namespace TodoTracker.Server.Tests;

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
