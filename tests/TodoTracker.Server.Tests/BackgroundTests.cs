using System.Net.Http.Json;
using System.Text.Json.Nodes;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

public sealed class BackgroundTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync(o => o.PublicBaseUrl = "http://127.0.0.1:5317");

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    private ReminderLoop Loop => _server.App.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<ReminderLoop>().Single();

    [Fact]
    public async Task Tick_dispatches_due_reminders_to_notifiers_once()
    {
        var client = _server.Client();
        var item = await client.PostJson("/api/items", new { title = "Feature B" });
        await client.PostJson($"/api/items/{item.Id()}/schedule", new { inMinutes = 30, notify = true, message = "Continue B" });

        await Loop.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_server.Notifier.Received);

        _server.Time.Advance(TimeSpan.FromMinutes(30));
        await Loop.RunOnceAsync(CancellationToken.None);
        await Loop.RunOnceAsync(CancellationToken.None);

        var sent = Assert.Single(_server.Notifier.Received);
        Assert.Equal("Continue B", sent.Message);
    }

    [Fact]
    public async Task Teams_webhook_receives_adaptive_card_when_configured()
    {
        var client = _server.Client();
        await client.PutAsJsonAsync("/api/settings", new { teamsWebhookUrl = "https://example.webhook.office.com/hook" });
        var item = await client.PostJson("/api/items", new { title = "Feature B", priority = "high" });
        await client.PostJson($"/api/items/{item.Id()}/reminders", new { at = ServerFixture.T0, message = "Time for ring 2" });

        await Loop.RunOnceAsync(CancellationToken.None);

        var (uri, body) = Assert.Single(_server.Teams.Requests);
        Assert.Equal("https://example.webhook.office.com/hook", uri.ToString());
        var card = JsonNode.Parse(body)!;
        Assert.Equal("message", card["type"]!.GetValue<string>());
        var content = card["attachments"]![0]!["content"]!;
        Assert.Equal("AdaptiveCard", content["type"]!.GetValue<string>());
        Assert.Contains("Feature B", content.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains("Time for ring 2", content.ToJsonString(), StringComparison.Ordinal);
        Assert.Contains($"http://127.0.0.1:5317/report.html?id={item.Id()}", content.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Teams_is_skipped_when_not_configured()
    {
        var client = _server.Client();
        var item = await client.PostJson("/api/items", new { title = "x" });
        await client.PostJson($"/api/items/{item.Id()}/reminders", new { at = ServerFixture.T0, message = "go" });

        await Loop.RunOnceAsync(CancellationToken.None);

        Assert.Empty(_server.Teams.Requests);
        Assert.Single(_server.Notifier.Received);
    }

    [Fact]
    public async Task Tick_advances_pomodoro_and_publishes_focus_events()
    {
        var events = _server.App.Services.GetRequiredService<ServerEvents>();
        var received = new List<PomodoroEvent>();
        events.Pomodoro += (_, e) => received.Add(e);
        var client = _server.Client();
        await client.PostJson("/api/pomodoro/start");

        _server.Time.Advance(TimeSpan.FromMinutes(25));
        await Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PomodoroEventKind.FocusCompleted, Assert.Single(received).Kind);
        Assert.Equal("shortBreak", (await client.GetJson("/api/dashboard"))["pomodoro"]!["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task Catch_up_after_sleep_raises_only_the_latest_focus_event()
    {
        var events = _server.App.Services.GetRequiredService<ServerEvents>();
        var received = new List<PomodoroEvent>();
        events.Pomodoro += (_, e) => received.Add(e);
        await _server.Client().PostJson("/api/pomodoro/start");

        _server.Time.Advance(TimeSpan.FromMinutes(45));
        await Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal(PomodoroEventKind.BreakCompleted, Assert.Single(received).Kind);
    }

    [Fact]
    public async Task Reminder_events_are_published_in_process_for_desktop_toasts()
    {
        var events = _server.App.Services.GetRequiredService<ServerEvents>();
        var received = new List<ReminderNotification>();
        events.Reminder += (_, e) => received.Add(e);
        var client = _server.Client();
        var item = await client.PostJson("/api/items", new { title = "x" });
        await client.PostJson($"/api/items/{item.Id()}/reminders", new { at = ServerFixture.T0, message = "go" });

        await Loop.RunOnceAsync(CancellationToken.None);

        Assert.Equal("go", Assert.Single(received).Message);
    }
}
