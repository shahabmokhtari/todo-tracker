using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TodoTracker.Core;
using TodoTracker.Server.Hooks;

namespace TodoTracker.Server.Tests;

public sealed class HookTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    private HookService Hooks => _server.App.Services.GetRequiredService<HookService>();

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        _client = _server.Client();
        await Hooks.ScanAsync(); // the first look: what's already there isn't news
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private void WriteHooks(string json) => File.WriteAllText(Path.Combine(_server.DataDirectory, "hooks.json"), json);

    /// <summary>A command hook that saves what it got on stdin to <paramref name="file"/> (in the data folder).</summary>
    private static JsonObject SaveTo(string file, JsonObject? match = null) => new()
    {
        ["type"] = "command",
        ["powershell"] = $"[Console]::In.ReadToEnd() | Set-Content -Encoding utf8 -NoNewline {file}; Write-Output \"saved $env:TT_EVENT\"",
        ["bash"] = $"cat > {file}; echo saved $TT_EVENT",
        ["match"] = match,
    };

    [Fact]
    public async Task What_happens_becomes_events_once_each_also_after_a_restart()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship the release", tags = new[] { "deploy" } });
        await _client.PostJson($"/api/items/{item.Id()}/complete", new { });

        var events = await Hooks.ScanAsync();

        Assert.Equal(["taskCreated", "taskCompleted"], events.Select(e => e.Event));
        var done = events[1];
        Assert.Equal(("Ship the release", "Work", "user"), (done.Task!.Title, done.Task.Group, done.Actor.Kind));
        Assert.Equal(["deploy"], done.Task.Tags);
        Assert.EndsWith($"/#/task/{item.Id()}", done.Task.Url, StringComparison.Ordinal);
        Assert.Empty(await Hooks.ScanAsync());

        using var restarted = new HookService(_server.Store, _server.App.Services.GetRequiredService<ServerEvents>(), _server.App.Services.GetRequiredService<TodoTrackerServerOptions>(), _server.Time, NullLogger<HookService>.Instance);
        Assert.Empty(await restarted.ScanAsync());
    }

    [Fact]
    public async Task A_command_hook_gets_the_event_on_stdin_and_only_runs_when_it_matches()
    {
        WriteHooks(new JsonObject
        {
            ["version"] = 1,
            ["hooks"] = new JsonObject
            {
                ["taskCompleted"] = new JsonArray(SaveTo("work.json", new JsonObject { ["groups"] = new JsonArray("work") }), SaveTo("home.json", new JsonObject { ["groups"] = new JsonArray("Personal") })),
                ["noteAdded"] = new JsonArray(SaveTo("note.json")),
            },
        }.ToJsonString());
        var item = await _client.PostJson("/api/items", new { title = "Café ✓ release" });
        await _client.PostJson($"/api/items/{item.Id()}/complete", new { });

        var completed = (await Hooks.ScanAsync()).Single(e => e.Event == "taskCompleted");
        var runs = await Hooks.RunAsync(completed);

        var run = Assert.Single(runs);
        Assert.True(run.Ok, run.Output);
        Assert.Contains("saved taskCompleted", run.Output, StringComparison.Ordinal);
        var received = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_server.DataDirectory, "work.json")))!;
        Assert.Equal(("taskCompleted", "Café ✓ release", completed.Id), (received["event"]!.GetValue<string>(), received["task"]!["title"]!.GetValue<string>(), received["id"]!.GetValue<string>()));
        Assert.False(File.Exists(Path.Combine(_server.DataDirectory, "home.json")));

        var status = await _client.GetJson("/api/hooks");
        Assert.Equal(3, status["hooks"]!.AsArray().Count);
        Assert.Equal("taskCompleted", status["runs"]![0]!["event"]!.GetValue<string>());
    }

    [Fact]
    public async Task Settings_can_try_a_hook_but_not_change_what_runs()
    {
        WriteHooks("""{ "hooks": { "taskCompleted": [ { "type": "command", "powershell": "exit 3", "bash": "exit 3" } ], "nope": [ { "type": "ftp" } ] } }""");

        var runs = (await _client.PostJson("/api/hooks/test", new { @event = "taskCompleted" })).AsArray();
        var status = await _client.GetJson("/api/hooks");

        Assert.Equal((false, 3), (runs[0]!["ok"]!.GetValue<bool>(), runs[0]!["exitCode"]!.GetValue<int>()));
        Assert.Contains(status["hooks"]!.AsArray(), h => h!["problem"]?.GetValue<string>()?.Contains("isn't a hook type", StringComparison.Ordinal) == true);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/hooks/test", new { @event = "made-up" })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.PutAsJsonAsync("/api/hooks", new { })).StatusCode);
    }

    [Fact]
    public async Task A_hook_that_runs_too_long_is_stopped()
    {
        WriteHooks("""{ "hooks": { "*": [ { "type": "command", "powershell": "Start-Sleep 60", "bash": "sleep 60", "timeoutSec": 1 } ] } }""");

        var runs = (await _client.PostJson("/api/hooks/test", new { })).AsArray();

        Assert.False(runs[0]!["ok"]!.GetValue<bool>());
        Assert.Contains("Stopped after 1 s", runs[0]!["output"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Events_stream_to_extensions_as_they_happen()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/events?events=taskCreated");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync());

        await _client.PostJson("/api/items", new { title = "Seen live" });
        await Hooks.ScanAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (lines.Count < 3)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            if (!string.IsNullOrEmpty(line))
            {
                lines.Add(line);
            }
        }

        Assert.StartsWith("id: ", lines[0], StringComparison.Ordinal);
        Assert.Equal("event: taskCreated", lines[1]);
        Assert.Contains("\"title\":\"Seen live\"", lines[2], StringComparison.Ordinal);
    }
}
