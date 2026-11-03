using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using OneThing.Core;
using OneThing.Server.Plugins;

namespace OneThing.Server.Tests;

/// <summary>
/// Send feedback from the web app: the server builds the note for the issues relay and sends it (the browser never talks
/// to the relay). Only the person at this computer can send one. Every test answers with a pretend relay.
/// </summary>
public sealed class FeedbackApiTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private static object Note(string clientId, string message = "The timer stops when I lock the screen.", string? contact = null) =>
        new { kind = "bug", message, contact, clientID = clientId, source = "button", language = "en-US", screen = "1920x1080" };

    /// <summary>The owner's session: opened from a link the app made itself (as the sidebar and the tray do).</summary>
    private async Task<HttpClient> AppClientAsync()
    {
        var link = new Uri(OneThingHost.CreateLaunchUrl(_server.App.Services));
        using var signIn = _server.Client(authenticated: false);
        using var response = await signIn.GetAsync(link.PathAndQuery);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tt_session=", StringComparison.Ordinal)).Split(';')[0];
        var client = _server.Client(authenticated: false);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("X-OneThing-Client", "web");
        return client;
    }

    [Fact]
    public async Task A_note_goes_to_the_relay_with_the_public_key_coarse_facts_and_nothing_from_the_tasks()
    {
        await _server.Client().PostJson("/api/items", new { title = "Secret project Falcon" });
        using var app = await AppClientAsync();
        var id = Guid.NewGuid().ToString();

        using var response = await app.PostAsJsonAsync("/api/plugins/feedback", Note(id, "  The timer stops when I lock the screen.\u0007 ", contact: " me@example.com "));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("IS-TEST0001", (await response.Json())["ref"]!.GetValue<string>());
        var sent = Assert.Single(_server.Relay.Requests);
        Assert.Equal(FakeRelay.Url, sent.Uri);
        Assert.Equal($"Bearer {Feedback.PublicKey}", sent.Authorization);
        Assert.False(sent.HasOrigin);
        Assert.Equal("application/json", sent.ContentType);
        Assert.DoesNotContain("Falcon", sent.Body, StringComparison.Ordinal);

        var body = JsonNode.Parse(sent.Body)!.AsObject();
        var platform = Feedback.ThisPlatform();
        Assert.Equal(["message", "title", "contact", "clientID", "labels", "metadata"], body.Select(p => p.Key));
        Assert.Equal("The timer stops when I lock the screen.", body["message"]!.GetValue<string>());
        Assert.Equal($"Bug · {platform}: The timer stops when I lock the screen.", body["title"]!.GetValue<string>());
        Assert.Equal("me@example.com", body["contact"]!.GetValue<string>());
        Assert.Equal(id, body["clientID"]!.GetValue<string>());
        Assert.Equal(["kind:bug", $"platform:{platform.ToLowerInvariant()}", "source:button"], body["labels"]!.AsArray().Select(l => l!.GetValue<string>()));
        var metadata = body["metadata"]!.AsObject();
        Assert.Equal(["Platform", "App version", "OS", "Device", "Language", "Screen", "Source"], metadata.Select(p => p.Key));
        Assert.Equal(Feedback.ThisOs(), metadata["OS"]!.GetValue<string>());
        Assert.Equal(McpInfo.Version, metadata["App version"]!.GetValue<string>());
        Assert.Equal("en-US", metadata["Language"]!.GetValue<string>());
        Assert.Equal("1920x1080", metadata["Screen"]!.GetValue<string>());
    }

    [Fact]
    public async Task When_the_relay_is_out_of_reach_the_note_can_be_sent_again_with_the_same_id()
    {
        using var app = await AppClientAsync();
        var id = Guid.NewGuid().ToString();
        _server.Relay.Answers.Enqueue(null);
        _server.Relay.Answers.Enqueue(429);
        _server.Relay.Answers.Enqueue(503);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            using var retry = await app.PostAsJsonAsync("/api/plugins/feedback", Note(id));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
            Assert.Equal(Feedback.Texts.Retry, (await retry.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>());
        }

        using var sent = await app.PostAsJsonAsync("/api/plugins/feedback", Note(id));
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal(4, _server.Relay.Requests.Count);
        Assert.All(_server.Relay.Requests, r => Assert.Equal(id, JsonNode.Parse(r.Body)!["clientID"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("{\"ref\":5,\"status\":[\"queued\"]}")]
    [InlineData("not json")]
    public async Task A_note_the_relay_took_is_sent_even_when_its_answer_looks_odd(string body)
    {
        using var app = await AppClientAsync();
        _server.Relay.Bodies.Enqueue(body);

        using var response = await app.PostAsJsonAsync("/api/plugins/feedback", Note(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(_server.Relay.Requests);
    }

    [Fact]
    public async Task A_note_the_relay_refuses_says_something_went_wrong()
    {
        using var app = await AppClientAsync();
        _server.Relay.Answers.Enqueue(400);

        using var response = await app.PostAsJsonAsync("/api/plugins/feedback", Note(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(Feedback.Texts.Failed, (await response.Content.ReadFromJsonAsync<JsonNode>())!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_note_a_known_kind_or_a_proper_id()
    {
        using var app = await AppClientAsync();
        foreach (var bad in new object[]
        {
            new { kind = "bug", message = "  \u0007 ", clientID = Guid.NewGuid().ToString(), source = "button" },
            new { kind = "rant", message = "Hi", clientID = Guid.NewGuid().ToString(), source = "button" },
            new { kind = "bug", message = "Hi", clientID = "not-a-uuid", source = "button" },
            new { kind = "bug", message = "Hi", clientID = Guid.NewGuid().ToString(), source = "email" },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/api/plugins/feedback", bad)).StatusCode);
        }

        Assert.Empty(_server.Relay.Requests);
    }

    [Fact]
    public async Task Language_and_screen_that_dont_look_like_one_are_left_out()
    {
        using var app = await AppClientAsync();
        await app.PostAsJsonAsync("/api/plugins/feedback", new { kind = "idea", message = "Hi", clientID = Guid.NewGuid().ToString(), source = "button", language = "<script>", screen = new string('9', 80) });

        var metadata = JsonNode.Parse(Assert.Single(_server.Relay.Requests).Body)!["metadata"]!.AsObject();
        Assert.False(metadata.ContainsKey("Language"));
        Assert.False(metadata.ContainsKey("Screen"));
    }

    [Fact]
    public async Task Only_the_person_at_this_computer_can_send_feedback_not_an_agent_with_the_token()
    {
        using var agent = _server.Client(actor: "agent:claude");

        using var response = await agent.PostAsJsonAsync("/api/plugins/feedback", Note(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_server.Relay.Requests);
    }

    [Fact]
    public async Task Switched_off_it_sends_nothing()
    {
        using var app = await AppClientAsync();
        await _server.Client().PutAsJsonAsync("/api/plugins/feedback", new { enabled = false });

        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsJsonAsync("/api/plugins/feedback", Note(Guid.NewGuid().ToString()))).StatusCode);
        Assert.Empty(_server.Relay.Requests);
    }

    [Fact]
    public async Task A_debug_build_logs_the_note_instead_of_sending_it_unless_asked_to_send()
    {
        Assert.False(FeedbackSettings.From(_ => null, debugBuild: true).Send);
        Assert.True(FeedbackSettings.From(_ => null, debugBuild: false).Send);
        Assert.True(FeedbackSettings.From(n => n == "ONETHING_FEEDBACK_SEND" ? "1" : null, debugBuild: true).Send);
        Assert.False(FeedbackSettings.From(n => n == "ONETHING_FEEDBACK_SEND" ? "0" : null, debugBuild: false).Send);
        Assert.Equal(new Uri(Feedback.RelayUrl), FeedbackSettings.From(_ => null, debugBuild: false).RelayUrl);
        Assert.Equal(new Uri("http://127.0.0.1:5397/v1/issues"), FeedbackSettings.From(n => n == "ONETHING_FEEDBACK_URL" ? "http://127.0.0.1:5397/v1/issues" : null, debugBuild: false).RelayUrl);

        await using var server = await ServerFixture.StartAsync(services: s => s.AddSingleton(new FeedbackSettings(FakeRelay.Url, Send: false)));
        var link = new Uri(OneThingHost.CreateLaunchUrl(server.App.Services));
        using var signIn = server.Client(authenticated: false);
        using var launched = await signIn.GetAsync(link.PathAndQuery);
        using var app = server.Client(authenticated: false);
        app.DefaultRequestHeaders.Add("Cookie", launched.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tt_session=", StringComparison.Ordinal)).Split(';')[0]);
        app.DefaultRequestHeaders.Add("X-OneThing-Client", "web");

        using var response = await app.PostAsJsonAsync("/api/plugins/feedback", Note(Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("logged", (await response.Json())["status"]!.GetValue<string>());
        Assert.Empty(server.Relay.Requests);
    }

    [Fact]
    public async Task A_note_kept_in_the_log_of_a_debug_build_leaves_out_the_email()
    {
        var log = new CapturingLogger();
        var relay = new FeedbackRelay(null!, new FeedbackSettings(FakeRelay.Url, Send: false), log);
        var body = Feedback.Body(new FeedbackDraft("bug", "The timer stops.", "me@example.com", "button", Guid.NewGuid().ToString()), new FeedbackDevice("Windows", "1.0.0", null, null, null, null, null))!;

        var answer = await relay.SendAsync(body, TestContext.Current.CancellationToken);

        Assert.Equal("logged", answer.State);
        var line = Assert.Single(log.Lines);
        Assert.Contains("The timer stops.", line, StringComparison.Ordinal);
        Assert.DoesNotContain("me@example.com", line, StringComparison.Ordinal);
        Assert.Equal("me@example.com", body["contact"]!.GetValue<string>());
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<FeedbackRelay>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
