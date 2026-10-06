using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Server.Plugins.AgentChat;

namespace TodoTracker.Server.Tests.AgentChat;

/// <summary>Plugins are listed and can be switched off; Ask AI works end to end over HTTP with a scripted agent.</summary>
public sealed class PluginApiTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync(services: s => s.AddSingleton(_ =>
        {
            var catalog = new AgentCatalog(searchPath: Path.GetTempPath());
            catalog.Extra.Add((new AgentOption("fake", "Fake agent", true, null), work => AcpConnectionTests.FakeAgent() with { WorkingDirectory = work }));
            return catalog;
        }));
        _client = _server.Client();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.App.Services.GetRequiredService<AgentChatService>().DisposeAsync();
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Plugins_are_listed_with_their_web_module_and_can_be_switched_off_after_a_restart()
    {
        var plugins = (await _client.GetJson("/api/plugins")).AsArray();
        Assert.Contains(plugins, p => p!["id"]!.GetValue<string>() == "teams");
        var chat = plugins.Single(p => p!["id"]!.GetValue<string>() == "agent-chat")!;
        Assert.True(chat["enabled"]!.GetValue<bool>());
        Assert.Equal("/js/plugins/agent-chat.js", chat["webModule"]!.GetValue<string>());

        var off = await (await _client.PutAsJsonAsync("/api/plugins/agent-chat", new { enabled = false })).Json();
        Assert.True(off["restartRequired"]!.GetValue<bool>());
        var saved = await File.ReadAllTextAsync(Path.Combine(_server.DataDirectory, "plugins.json"), TestContext.Current.CancellationToken);

        // The next start (another data folder with the same plugins.json) runs without it.
        var data = Directory.CreateTempSubdirectory("tt-plugins-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(data, "plugins.json"), saved, TestContext.Current.CancellationToken);
            await using var restarted = await ServerFixture.StartAsync(o => o.DataDirectory = data);
            using var client = restarted.Client();
            Assert.False((await client.GetJson("/api/plugins")).AsArray().Single(p => p!["id"]!.GetValue<string>() == "agent-chat")!["enabled"]!.GetValue<bool>());
            Assert.False((await client.GetAsync(new Uri("/api/plugins/agent-chat", UriKind.Relative))).IsSuccessStatusCode);
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task Every_optional_feature_is_a_plugin()
    {
        var ids = (await _client.GetJson("/api/plugins")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList();

        Assert.Equal(["agent-chat", "connect-ai", "focus-timer", "history", "obsidian", "teams"], ids);
    }

    [Fact]
    public async Task Turning_version_history_off_means_no_versions_even_when_git_is_there()
    {
        var data = Directory.CreateTempSubdirectory("tt-plugins-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(data, "plugins.json"), "{\"history\": false}", TestContext.Current.CancellationToken);
            await using var server = await ServerFixture.StartAsync(o =>
            {
                o.DataDirectory = data;
                o.EnableHistory = true;
            });

            Assert.Null(server.App.Services.GetRequiredService<HistoryService>().History);
            Assert.False(TodoTracker.Server.Plugins.PluginHost.IsEnabled(data, "history"));
            Assert.True(TodoTracker.Server.Plugins.PluginHost.IsEnabled(data, "teams"));
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }

    [Fact]
    public async Task Plugin_routes_need_the_token()
    {
        using var anonymous = _server.Client(authenticated: false);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync(new Uri("/api/plugins/agent-chat", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_message_is_answered_and_permissions_are_asked_over_http()
    {
        var accepted = await _client.PostAsJsonAsync("/api/plugins/agent-chat/message", new { text = "add Milk" });
        Assert.Equal(System.Net.HttpStatusCode.Accepted, accepted.StatusCode);

        var ask = await Until(s => s["entries"]!.AsArray().FirstOrDefault(e => e!["kind"]!.GetValue<string>() == "permission" && e["status"]!.GetValue<string>() == "waiting"));
        var busy = await _client.PostAsJsonAsync("/api/plugins/agent-chat/message", new { text = "another" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, busy.StatusCode);

        await _client.PostJson("/api/plugins/agent-chat/answer", new { entryId = ask["id"]!.GetValue<string>(), choice = "allow" });
        var done = await Until(s => s["status"]!.GetValue<string>() == "ready" ? s : null);
        Assert.Equal("Added \"Milk\".", done["entries"]!.AsArray().Last(e => e!["kind"]!.GetValue<string>() == "agent")!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_stream_sends_the_state_now_and_after_each_change()
    {
        using var stream = await _client.GetStreamAsync(new Uri("/api/plugins/agent-chat/stream", UriKind.Relative), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(stream);

        var first = await NextState(reader);
        Assert.Equal("idle", first["status"]!.GetValue<string>());

        await _client.PostAsJsonAsync("/api/plugins/agent-chat/message", new { text = "hi" });
        JsonNode state;
        do
        {
            state = await NextState(reader);
        }
        while (state["status"]!.GetValue<string>() != "ready");

        Assert.Equal("Hello, there.", state["entries"]!.AsArray().Last()!["text"]!.GetValue<string>());
    }

    private static async Task<JsonNode> NextState(StreamReader reader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeout);
        while (await reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                return JsonNode.Parse(line[5..])!;
            }
        }

        throw new InvalidOperationException("stream ended");
    }

    private async Task<JsonNode> Until(Func<JsonNode, JsonNode?> pick)
    {
        var until = DateTime.UtcNow + Timeout;
        while (true)
        {
            var state = await _client.GetJson("/api/plugins/agent-chat");
            if (pick(state) is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < until, "timed out: " + state.ToJsonString());
            await Task.Delay(50);
        }
    }
}
