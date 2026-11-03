using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace OneThing.Server.Tests;

/// <summary>
/// What only the person at this computer may do: anything holding just the API token (an AI agent, a script) is turned
/// away; the owner's own apps (the app's session, or the token with the owner key, as the Mac app sends it) aren't.
/// </summary>
public sealed class OwnerOnlyTests : IAsyncLifetime
{
    private static readonly string Id = Guid.NewGuid().ToString();
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync(o =>
    {
        Directory.CreateDirectory(o.DataDirectory);
        File.WriteAllText(Path.Combine(o.DataDirectory, "plugins.json"), """{"connector-github": true, "connector-notion": true}""");
    });

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    public static TheoryData<string, string, string?> OwnerOnlyRoutes => new()
    {
        { "PUT", "/api/settings/vault", """{"path":"C:\\elsewhere"}""" },
        { "PUT", "/api/settings", """{"teamsWebhookUrl":"https://collector.example/reminders"}""" },
        { "PUT", "/api/plugins/history", """{"enabled":false}""" },
        { "GET", "/api/plugins/agent-chat", null },
        { "POST", "/api/plugins/agent-chat/select", """{"agent":"copilot"}""" },
        { "POST", "/api/plugins/agent-chat/message", """{"text":"run this"}""" },
        { "POST", "/api/plugins/agent-chat/answer", """{"entryId":"e1","choice":"allow"}""" },
        { "GET", "/api/plugins/agent-chat/models", null },
        { "POST", "/api/plugins/agent-chat/models", """{"preset":"openai","name":"x","baseUrl":"https://collector.example/v1","model":"m","key":"k"}""" },
        { "POST", "/api/hooks/test", "{}" },
        { "POST", "/api/hooks/abc/reject", """{"version":"0123456789abcdef"}""" },
        { "DELETE", $"/api/items/{Id}", null },
        { "DELETE", $"/api/groups/{Id}?moveTo={Id}", null },
        { "DELETE", "/api/labels/Deep%20work", null },
        { "DELETE", $"/api/items/{Id}/time/{Id}", null },
        { "DELETE", $"/api/items/{Id}/attachments/{Id}", null },
        { "POST", "/api/plugins/sync/now", null },
        { "PUT", "/api/plugins/sync/provider", """{"provider":"gist"}""" },
        { "POST", "/api/plugins/sync/forget", """{"device":"laptop"}""" },
        { "PUT", "/api/plugins/connectors/notion", """{"groupId":null}""" },
        { "PUT", "/api/plugins/connectors/notion/token", """{"token":"secret_x"}""" },
        { "POST", "/api/plugins/connectors/notion/sync", null },
        { "DELETE", "/api/plugins/connectors/notion", null },
        { "PUT", "/api/plugins/connector-github/every-group", """{"on":true}""" },
        { "POST", $"/api/plugins/connector-github/groups/{Id}/connect", "{}" },
        { "DELETE", $"/api/plugins/connector-github/groups/{Id}", null },
    };

    private static (string Actor, string Kind) Who(JsonArray timeline, string kind) =>
        timeline.Where(e => e!["kind"]!.GetValue<string>() == kind).Select(e => (e!["actor"]!.GetValue<string>(), e["actorKind"]!.GetValue<string>())).Single();

    private static HttpRequestMessage Request(string method, string path, string? body) => new(new HttpMethod(method), path)
    {
        Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Theory]
    [MemberData(nameof(OwnerOnlyRoutes))]
    public async Task Something_with_only_the_api_token_is_turned_away(string method, string path, string? body)
    {
        using var agent = _server.Client(actor: "agent:claude");

        using var response = await agent.SendAsync(Request(method, path, body), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Only you", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/api/plugins/agent-chat", null)]
    [InlineData("PUT", "/api/plugins/teams", """{"enabled":false}""")]
    [InlineData("PUT", "/api/settings", """{"teamsWebhookUrl":null}""")]
    [InlineData("POST", "/api/hooks/test", "{}")]
    [InlineData("GET", "/api/plugins/agent-chat/models", null)]
    public async Task The_owners_own_apps_may(string method, string path, string? body)
    {
        using var mac = _server.OwnerClient();
        using var web = await _server.AppSessionClientAsync();

        using var fromMac = await mac.SendAsync(Request(method, path, body), TestContext.Current.CancellationToken);
        using var fromWeb = await web.SendAsync(Request(method, path, body), TestContext.Current.CancellationToken);

        Assert.True(fromMac.IsSuccessStatusCode, $"{(int)fromMac.StatusCode}: {await fromMac.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        Assert.True(fromWeb.IsSuccessStatusCode, $"{(int)fromWeb.StatusCode}: {await fromWeb.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
    }

    [Fact]
    public async Task You_can_delete_a_task_an_agent_can_not()
    {
        var task = await _server.Client().PostJson("/api/items", new { title = "Old idea" });

        using var agent = _server.Client(actor: "agent:claude");
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync(new Uri($"/api/items/{task.Id()}", UriKind.Relative))).StatusCode);
        using var mac = _server.OwnerClient();
        Assert.Equal(HttpStatusCode.NoContent, (await mac.DeleteAsync(new Uri($"/api/items/{task.Id()}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Reading_connections_and_sync_stays_open_to_the_api_token()
    {
        using var agent = _server.Client(actor: "agent:claude");

        (await agent.GetAsync(new Uri("/api/plugins/sync", UriKind.Relative))).EnsureSuccessStatusCode();
        (await agent.GetAsync(new Uri("/api/plugins/connectors", UriKind.Relative))).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_wrong_owner_key_counts_for_nothing()
    {
        using var client = _server.Client();
        client.DefaultRequestHeaders.Add(OwnerKey.Header, "0123456789abcdef0123456789abcdef0123456789abcdef");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/plugins/teams", new { enabled = false })).StatusCode);
    }

    [Fact]
    public async Task The_tasks_folder_can_not_be_moved_to_a_folder_holding_the_data_folder()
    {
        using var mac = _server.OwnerClient();

        using var response = await mac.PutAsJsonAsync("/api/settings/vault", new { path = Path.GetDirectoryName(_server.DataDirectory) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("data folder", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Null(_server.App.Services.GetRequiredService<SettingsStore>().Current.VaultPath);
    }

    [Fact]
    public async Task A_change_sent_with_only_the_token_and_no_name_is_an_api_clients_not_yours()
    {
        var task = await _server.Client().PostJson("/api/items", new { title = "From a script" });
        await _server.OwnerClient().PostJson($"/api/items/{task.Id()}/notes", new { text = "From the Mac app" });

        var timeline = (await _server.Client().GetJson($"/api/items/{task.Id()}/timeline")).AsArray();

        Assert.Contains(timeline, e => e!.ToJsonString().Contains(Security.ApiClient, StringComparison.Ordinal) && e.ToJsonString().Contains("Created", StringComparison.Ordinal));
        Assert.Contains(timeline, e => e!.ToJsonString().Contains("From the Mac app", StringComparison.Ordinal) && e.ToJsonString().Contains("\"user\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Saying_you_are_the_user_needs_the_owner_key()
    {
        var task = await _server.Client(actor: "user").PostJson("/api/items", new { title = "Pretending" });

        var timeline = (await _server.Client().GetJson($"/api/items/{task.Id()}/timeline")).AsArray();

        Assert.Equal(($"Agent: {Security.ApiClient}", "agent"), Who(timeline, "created"));
    }

    [Fact]
    public async Task Putting_a_task_first_says_who_did_it()
    {
        var task = await _server.Client().PostJson("/api/items", new { title = "Most important" });

        await _server.Client(actor: "agent:claude").PostOk("/api/now/first", new { ids = new[] { task.Id() } });

        var timeline = (await _server.Client().GetJson($"/api/items/{task.Id()}/timeline")).AsArray();
        var entry = timeline.Single(e => e!["summary"]!.GetValue<string>() == "Put \"Most important\" first")!;
        Assert.Equal("Agent: claude", entry["actor"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sign_in_links_asked_for_with_the_token_never_block_the_owners()
    {
        using var agent = _server.Client();
        for (var i = 0; i < 256; i++)
        {
            await agent.PostJson("/api/launch", new { @return = "/" });
        }

        Assert.Equal(HttpStatusCode.Conflict, (await agent.PostAsJsonAsync("/api/launch", new { @return = "/" })).StatusCode);
        using var mac = _server.OwnerClient();
        (await mac.PostAsJsonAsync("/api/launch", new { @return = "/" })).EnsureSuccessStatusCode();
        Assert.StartsWith("http://", OneThingHost.CreateLaunchUrl(_server.App.Services), StringComparison.Ordinal);
    }
}
