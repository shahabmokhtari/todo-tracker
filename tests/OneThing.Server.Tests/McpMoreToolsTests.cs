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
}
