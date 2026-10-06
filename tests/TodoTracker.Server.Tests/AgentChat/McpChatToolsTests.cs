using System.Text.Json.Nodes;
using TodoTracker.Core;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>API models use Todo Tracker's own MCP tools, so they can do what agents can, credited to the model.</summary>
public sealed class McpChatToolsTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private McpChatTools _tools = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        _tools = new McpChatTools(() => _server.Client(), "GPT test");
    }

    public async ValueTask DisposeAsync()
    {
        await _tools.DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task The_model_gets_the_task_tools_with_their_schemas_and_which_only_read()
    {
        var tools = await _tools.ListAsync(TestContext.Current.CancellationToken);

        var create = tools.Single(t => t.Name == "create_task");
        Assert.False(create.ReadOnly);
        Assert.Equal("object", create.InputSchema["type"]!.GetValue<string>());
        Assert.NotNull(create.InputSchema["properties"]!["title"]);
        Assert.True(tools.Single(t => t.Name == "get_dashboard").ReadOnly);
    }

    [Fact]
    public async Task A_change_made_by_the_model_is_credited_to_it()
    {
        var result = await _tools.CallAsync("create_task", new JsonObject { ["title"] = "Water the plants" }.ToJsonString(), TestContext.Current.CancellationToken);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Water the plants", result.Text, StringComparison.Ordinal);
        var actor = await _server.Store.ReadAsync(b => b.Activity.Last(a => a.Kind == ActivityKind.Created).Actor);
        Assert.Equal(ActorKind.Agent, actor.Kind);
        Assert.Equal("GPT test", actor.Name);
    }

    [Fact]
    public async Task Tools_connect_again_when_the_app_let_go_of_the_session()
    {
        // Review finding: an MCP session ended while the chat was idle, and every later tool call failed until a restart.
        var current = await ServerFixture.StartAsync();
        await using var tools = new McpChatTools(() => current.Client(), "GPT test");
        Assert.False((await tools.CallAsync("get_dashboard", "{}", TestContext.Current.CancellationToken)).IsError);

        await current.DisposeAsync();
        current = await ServerFixture.StartAsync();
        try
        {
            ChatToolResult result = null!;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                result = await tools.CallAsync("get_dashboard", "{}", TestContext.Current.CancellationToken);
                if (!result.IsError)
                {
                    break;
                }

                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.False(result.IsError, result.Text);
        }
        finally
        {
            await current.DisposeAsync();
        }
    }

    [Fact]
    public async Task Tool_errors_and_bad_arguments_come_back_as_errors_for_the_model()
    {
        var missing = await _tools.CallAsync("complete_task", new JsonObject { ["taskId"] = Guid.NewGuid().ToString() }.ToJsonString(), TestContext.Current.CancellationToken);
        var garbled = await _tools.CallAsync("create_task", "{ not json", TestContext.Current.CancellationToken);

        Assert.True(missing.IsError);
        Assert.True(garbled.IsError);
        Assert.Contains("arguments", garbled.Text, StringComparison.OrdinalIgnoreCase);
    }
}
