using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace TodoTracker.Cli.Tests;

/// <summary>
/// <c>tt mcp</c> as Claude Desktop, Claude Code, Copilot CLI or VS Code launch it: a child process speaking MCP over stdio.
/// </summary>
public sealed class McpStdioTests : IDisposable
{
    private readonly CliHarness _tt = new();

    public void Dispose() => _tt.Dispose();

    [Fact]
    public async Task Tt_mcp_serves_the_task_tools_over_stdio()
    {
        await using var client = await Connect();

        Assert.Equal("todo-tracker", client.ServerInfo.Name);
        Assert.Contains("get_dashboard", client.ServerInstructions ?? string.Empty, StringComparison.Ordinal);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "create_task");
        Assert.Contains(tools, t => t.Name == "search_tasks");
        Assert.DoesNotContain(tools, t => t.Name.Contains("delete", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tasks_created_over_mcp_land_in_the_vault_and_are_attributed_to_the_client()
    {
        await using var client = await Connect();

        var result = await client.CallToolAsync(
            "create_task",
            new Dictionary<string, object?> { ["title"] = "Prepare demo", ["tags"] = new[] { "demo" } },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(true, result.IsError);
        var created = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text).RootElement;
        Assert.Equal("Prepare demo", created.GetProperty("title").GetString());
        Assert.True(File.Exists(Path.Combine(_tt.VaultDirectory, "Work", "Prepare demo.md")));

        // The CLI (and the app) see it immediately: same vault, shared lock.
        Assert.Equal(["Prepare demo"], (await _tt.Json("list", "#demo")).Titles());
        var activity = await File.ReadAllTextAsync(Path.Combine(_tt.VaultDirectory, ".todo-tracker", "activity.jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains("tt-tests", activity, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changes_made_elsewhere_are_visible_to_the_running_server()
    {
        await using var client = await Connect();
        await _tt.Ok("add", "Added from the terminal");

        var result = await client.CallToolAsync("search_tasks", new Dictionary<string, object?> { ["query"] = "terminal" }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("Added from the terminal", ((TextContentBlock)result.Content[0]).Text, StringComparison.Ordinal);
    }

    private async Task<McpClient> Connect()
    {
        var tt = Path.Combine(AppContext.BaseDirectory, "tt.dll");
        Assert.True(File.Exists(tt), $"{tt} is missing");
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "tt",
            Command = "dotnet",
            Arguments = [tt, "mcp", "--data", _tt.DataDirectory, "--vault", _tt.VaultDirectory, "--no-history"],
            EnvironmentVariables = new Dictionary<string, string?> { ["TODOTRACKER_VAULT"] = null, ["TODOTRACKER_DATA"] = null },
        });
        return await McpClient.CreateAsync(
            transport,
            new McpClientOptions { ClientInfo = new Implementation { Name = "tt-tests", Version = "1.0" } },
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
