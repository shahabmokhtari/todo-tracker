using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

/// <summary>
/// The Mac app's requests, byte for byte as it sends them (apple/…/ServerAPI.swift; its tests pin the same bodies):
/// each must work here, and /api/export must give back the board the Mac app reads.
/// </summary>
public sealed class MacAppContractTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        _client = _server.Client();
    }

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private async Task<JsonNode?> Send(HttpMethod method, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path} {body}: {(int)response.StatusCode} {text}");
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    [Fact]
    public async Task Everything_the_mac_app_sends_works()
    {
        var group = (await Send(HttpMethod.Post, "/api/groups", """{"name":"Home"}"""))!.Id();
        var task = (await Send(HttpMethod.Post, "/api/capture", $$"""{"groupId":"{{group}}","text":"Plan the trip !high"}"""))!.Id();
        await Send(HttpMethod.Post, "/api/capture", """{"text":"Call mum @tomorrow"}""");
        var step = (await Send(HttpMethod.Post, "/api/items", $$"""{"parentId":"{{task}}","title":"Step"}"""))!.Id();
        await Send(HttpMethod.Post, $"/api/items/{task}/steps", """{"stepDelayMinutes":90,"titles":["A","B"]}""");
        await Send(HttpMethod.Post, $"/api/items/{task}/notes", """{"text":"Called \"Sam\" / done"}""");
        await Send(HttpMethod.Patch, $"/api/items/{task}", """{"priority":"critical","title":"Plan the big trip"}""");
        await Send(HttpMethod.Post, $"/api/items/{step}/schedule", """{"at":"2026-01-05T10:00:00.000Z","notify":true}""");
        var reminder = (await Send(HttpMethod.Get, $"/api/items/{step}"))!["reminders"]!.AsArray().Single()!.Id();
        await Send(HttpMethod.Post, $"/api/items/{step}/reminders/{reminder}/dismiss");
        await Send(HttpMethod.Post, $"/api/items/{step}/schedule", """{"clear":true}""");
        await Send(HttpMethod.Post, $"/api/items/{step}/complete");
        await Send(HttpMethod.Post, $"/api/items/{step}/reopen");
        await Send(HttpMethod.Post, "/api/pomodoro/start", $$"""{"itemId":"{{task}}"}""");
        await Send(HttpMethod.Post, "/api/pomodoro/pause");
        await Send(HttpMethod.Post, "/api/pomodoro/resume");
        await Send(HttpMethod.Post, "/api/pomodoro/skip");
        await Send(HttpMethod.Post, "/api/pomodoro/reset");
        await Send(HttpMethod.Put, "/api/settings/theme", """{"theme":"dark"}""");
        await Send(HttpMethod.Delete, $"/api/items/{step}");

        var board = BoardSerializer.Deserialize((await Send(HttpMethod.Get, "/api/export"))!.ToJsonString());
        var plan = board.Get(Guid.Parse(task));
        Assert.Equal("Plan the big trip", plan.Title);
        Assert.Equal(Priority.Critical, plan.Priority);
        Assert.Equal(["A", "B"], plan.Children.Select(c => c.Title));
        Assert.Equal("Called \"Sam\" / done", plan.Notes.Single().Text);
        Assert.Contains(board.Items, i => i.Title == "Call mum" && i.NextActionAt is not null);
        Assert.Equal("dark", (await Send(HttpMethod.Get, "/api/settings"))!["theme"]!.GetValue<string>());
        var launch = await Send(HttpMethod.Post, "/api/launch", """{"return":"/"}""");
        Assert.StartsWith("http://", launch!["url"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_answers_without_the_token()
    {
        using var anonymous = _server.Client(authenticated: false);
        using var response = await anonymous.GetAsync("/health", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
