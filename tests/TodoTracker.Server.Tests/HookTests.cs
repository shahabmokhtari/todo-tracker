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

        using var restarted = new HookService(_server.Store, _server.App.Services.GetRequiredService<ServerEvents>(), new HookStore(_server.DataDirectory), _server.App.Services.GetRequiredService<TodoTrackerServerOptions>(), _server.Time, NullLogger<HookService>.Instance);
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
    public async Task Settings_can_try_a_hook_and_shows_what_is_wrong_with_one()
    {
        // (This test used to also check that hooks couldn't be changed through the API: the hook-editing PR adds that on
        // purpose, with approval for anything but the signed-in app; see the tests below.)
        WriteHooks("""{ "hooks": { "taskCompleted": [ { "type": "command", "powershell": "exit 3", "bash": "exit 3" } ], "nope": [ { "type": "ftp" } ] } }""");

        var runs = (await _client.PostJson("/api/hooks/test", new { @event = "taskCompleted" })).AsArray();
        var status = await _client.GetJson("/api/hooks");

        Assert.Equal((false, 3), (runs[0]!["ok"]!.GetValue<bool>(), runs[0]!["exitCode"]!.GetValue<int>()));
        Assert.Contains(status["hooks"]!.AsArray(), h => h!["problem"]?.GetValue<string>()?.Contains("isn't a hook type", StringComparison.Ordinal) == true);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/hooks/test", new { @event = "made-up" })).StatusCode);

    }

    /// <summary>A browser session from <paramref name="cookie"/> (with the app's header, as the web app sends).</summary>
    private HttpClient SessionClient(string cookie)
    {
        var client = _server.Client(authenticated: false);
        client.DefaultRequestHeaders.Add("Cookie", $"tt_session={cookie}");
        client.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");
        return client;
    }

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tt_session=", StringComparison.Ordinal)).Split(';')[0]["tt_session=".Length..];

    /// <summary>The owner's session: opened from a link the app made itself (as the sidebar and the tray do).</summary>
    private HttpClient AppClient()
    {
        var link = new Uri(TodoTrackerHost.CreateLaunchUrl(_server.App.Services));
        using var client = _server.Client(authenticated: false);
        using var response = client.GetAsync(link.PathAndQuery).GetAwaiter().GetResult();
        return SessionClient(SessionCookie(response));
    }

    [Fact]
    public async Task Only_a_session_the_app_opened_itself_can_approve_not_one_from_the_api_token()
    {
        using var agent = _server.Client(actor: "agent:claude");
        var id = (await agent.PostJson("/api/hooks", new { @event = "taskCompleted", hook = Exit0 }))["id"]!.GetValue<string>();

        // Whatever holds the token can sign in, or send the token as the cookie, or ask for a launch link over HTTP:
        // none of those is the person at this computer.
        using var tokenCookie = SessionClient(ServerFixture.Token);
        using var signIn = _server.Client(authenticated: false);
        signIn.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");
        using var loggedIn = SessionClient(SessionCookie(await signIn.PostAsJsonAsync("/api/login", new { token = ServerFixture.Token })));
        var link = (await agent.PostJson("/api/launch", new { }))["url"]!.GetValue<string>();
        using var launched = SessionClient(SessionCookie(await _server.Client(authenticated: false).GetAsync(new Uri(link).PathAndQuery)));
        foreach (var client in new[] { tokenCookie, loggedIn, launched })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/hooks/{id}/approve", new { })).StatusCode);
            Assert.Equal("needs approval", (await client.PostJson("/api/hooks", new { @event = "noteAdded", hook = Exit0 }))["state"]!.GetValue<string>());
            Assert.False((await client.GetJson("/api/hooks"))["canApprove"]!.GetValue<bool>());
        }

        using var app = AppClient();
        Assert.True((await app.GetJson("/api/hooks"))["canApprove"]!.GetValue<bool>());
        Assert.Equal("on", (await app.PostJson($"/api/hooks/{id}/approve", new { }))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Approving_is_for_the_version_you_looked_at_and_agents_cant_set_variables_or_a_folder()
    {
        using var agent = _server.Client(actor: "agent:claude");
        using var app = AppClient();
        var seen = await agent.PostJson("/api/hooks", new { @event = "taskCompleted", hook = Exit0 });
        var id = seen["id"]!.GetValue<string>();
        await agent.PutAsJsonAsync($"/api/hooks/{id}", new { hook = new { type = "command", powershell = "exit 7", bash = "exit 7" } });

        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsJsonAsync($"/api/hooks/{id}/approve", new { version = seen["version"]!.GetValue<string>() })).StatusCode);

        // Your change to it doesn't approve it either.
        Assert.Equal("needs approval", (await (await app.PutAsJsonAsync($"/api/hooks/{id}", new { hook = new { type = "command", powershell = "exit 7", bash = "exit 7", name = "renamed" } })).Json())["state"]!.GetValue<string>());

        var sneaky = await agent.PostAsJsonAsync("/api/hooks", new { @event = "taskCompleted", hook = new { type = "command", bash = "echo hi", powershell = "echo hi", env = new { BASH_ENV = "/tmp/x.sh" } } });
        Assert.Equal(HttpStatusCode.BadRequest, sneaky.StatusCode);
    }

    [Fact]
    public async Task Apps_on_this_computer_with_the_owner_key_open_the_owners_session_and_a_lesser_link_never_replaces_it()
    {
        using var agent = _server.Client(actor: "agent:claude");
        var id = (await agent.PostJson("/api/hooks", new { @event = "taskCompleted", hook = Exit0 }))["id"]!.GetValue<string>();

        // The Mac app and a second tray read the owner key next to the token and send it.
        using var mac = _server.Client();
        mac.DefaultRequestHeaders.Add("X-TodoTracker-Owner-Key", await File.ReadAllTextAsync(Path.Combine(_server.DataDirectory, "owner-key")));
        var link = new Uri((await mac.PostJson("/api/launch", new { }))["url"]!.GetValue<string>());
        using var browser = _server.Client(authenticated: false);
        var cookie = SessionCookie(await browser.GetAsync(link.PathAndQuery));
        using var owner = SessionClient(cookie);
        Assert.True((await owner.GetJson("/api/hooks"))["canApprove"]!.GetValue<bool>());

        // An ordinary link opened in that browser leaves its owner session as it is.
        var ordinary = new Uri((await agent.PostJson("/api/launch", new { }))["url"]!.GetValue<string>());
        using var again = new HttpRequestMessage(HttpMethod.Get, ordinary.PathAndQuery);
        again.Headers.Add("Cookie", $"tt_session={cookie}");
        Assert.False((await browser.SendAsync(again)).Headers.Contains("Set-Cookie"));
        Assert.Equal("on", (await owner.PostJson($"/api/hooks/{id}/approve", new { }))["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Hidden_values_sent_back_keep_what_they_were_and_agents_cant_change_variables()
    {
        using var app = AppClient();
        using var agent = _server.Client(actor: "agent:claude");
        var id = (await app.PostJson("/api/hooks", new { @event = "taskCompleted", hook = new { type = "command", bash = "exit 0", powershell = "exit 0", env = new Dictionary<string, string> { ["TOKEN"] = "s3cret" } } }))["id"]!.GetValue<string>();

        // An agent renames it, sending back what it was shown: the variable keeps its value (and it now needs approval).
        var shown = (await agent.GetJson("/api/hooks"))["hooks"]![0]!["hook"]!;
        Assert.Equal("•••", shown["env"]!["TOKEN"]!.GetValue<string>());
        var renamed = await (await agent.PutAsJsonAsync($"/api/hooks/{id}", new { hook = new { type = "command", bash = "exit 0", powershell = "exit 0", name = "renamed", env = new Dictionary<string, string> { ["TOKEN"] = "•••" } } })).Json();
        Assert.Equal("needs approval", renamed["state"]!.GetValue<string>());
        Assert.Equal("s3cret", (await app.GetJson("/api/hooks"))["hooks"]![0]!["hook"]!["env"]!["TOKEN"]!.GetValue<string>());

        var changed = await agent.PutAsJsonAsync($"/api/hooks/{id}", new { hook = new { type = "command", bash = "exit 0", powershell = "exit 0", env = new Dictionary<string, string> { ["TOKEN"] = "other", ["BASH_ENV"] = "/tmp/x" } } });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
    }

    [Fact]
    public async Task Header_and_variable_values_are_hidden_from_anything_but_the_owners_session()
    {
        using var app = AppClient();
        var added = await app.PostJson("/api/hooks", new { @event = "reminderDue", hook = new { type = "http", url = "https://example.com/hook", headers = new Dictionary<string, string> { ["Authorization"] = "Bearer secret" } } });


        // Names come back as written (the API's camelCase doesn't touch them: variable names are case-sensitive).
        Assert.Equal("•••", (await _client.GetJson("/api/hooks"))["hooks"]![0]!["hook"]!["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("Bearer secret", (await app.GetJson("/api/hooks"))["hooks"]![0]!["hook"]!["headers"]!["Authorization"]!.GetValue<string>());
    }

    private static object Exit0 => new { type = "command", powershell = "exit 0", bash = "exit 0", name = "quiet" };

    [Fact]
    public async Task The_app_is_asked_once_about_each_version_of_a_hook_an_agent_adds_or_changes()
    {
        using var agent = _server.Client(actor: "agent:claude");
        using var app = AppClient();
        var asked = new List<HookEntry>();
        var events = _server.App.Services.GetRequiredService<ServerEvents>();

        // Until the app listens (it shows the question), nobody is asked, and nothing counts as asked.
        var early = (await agent.PostJson("/api/hooks", new { @event = "noteAdded", hook = Exit0 }))["id"]!.GetValue<string>();
        Assert.Empty(Hooks.AskForApprovals());
        await app.DeleteAsync($"/api/hooks/{early}");
        events.HookApprovalNeeded += (_, e) => asked.Add(e);

        // Yours never ask; an agent's does, once.
        await app.PostJson("/api/hooks", new { @event = "taskCompleted", hook = Exit0 });
        var id = (await agent.PostJson("/api/hooks", new { @event = "noteAdded", hook = Exit0 }))["id"]!.GetValue<string>();
        Hooks.AskForApprovals();
        Hooks.AskForApprovals();
        Assert.Equal([(id, "claude")], asked.Select(e => (e.Id, e.Hook.AddedBy)));

        // Allowed: it runs and nothing more is asked. The agent changing it asks again (what runs changed).
        await app.PostJson($"/api/hooks/{id}/approve", new { version = asked[0].Version });
        Hooks.AskForApprovals();
        Assert.Single(asked);
        await agent.PutAsJsonAsync($"/api/hooks/{id}", new { @event = "noteAdded", hook = new { type = "command", powershell = "exit 1", bash = "exit 1" } });
        Assert.Single(Hooks.AskForApprovals());
        Assert.Equal(2, asked.Count);
        Assert.NotEqual(asked[0].Version, asked[1].Version);
    }

    [Fact]
    public async Task Hooks_an_agent_adds_or_changes_wait_until_you_approve_them_in_the_app()
    {
        using var agent = _server.Client(actor: "agent:claude");
        using var app = AppClient();

        var added = await agent.PostJson("/api/hooks", new { @event = "taskCompleted", hook = Exit0 });
        var id = added["id"]!.GetValue<string>();
        Assert.Equal(("needs approval", "claude"), (added["state"]!.GetValue<string>(), added["hook"]!["addedBy"]!.GetValue<string>()));
        Assert.Empty((await agent.PostJson("/api/hooks/test", new { @event = "taskCompleted" })).AsArray());

        // The agent (anything with the token) can't approve its own hook; the app can.
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync($"/api/hooks/{id}/approve", null)).StatusCode);
        Assert.Equal("on", (await app.PostJson($"/api/hooks/{id}/approve"))["state"]!.GetValue<string>());
        Assert.Single((await agent.PostJson("/api/hooks/test", new { @event = "taskCompleted" })).AsArray());

        // A change by the agent needs approval again; your change to it keeps it waiting (only approving approves),
        // and one to a hook that isn't waiting runs at once.
        var edited = await (await agent.PutAsJsonAsync($"/api/hooks/{id}", new { hook = new { type = "command", powershell = "exit 1", bash = "exit 1" } })).Json();
        Assert.Equal("needs approval", edited["state"]!.GetValue<string>());
        var mine = await (await app.PutAsJsonAsync($"/api/hooks/{id}", new { @event = "noteAdded", hook = Exit0 })).Json();
        Assert.Equal(("needs approval", "noteAdded"), (mine["state"]!.GetValue<string>(), mine["event"]!.GetValue<string>()));
        Assert.Equal("on", (await app.PostJson($"/api/hooks/{id}/approve", new { version = mine["version"]!.GetValue<string>() }))["state"]!.GetValue<string>());
        Assert.Equal("on", (await (await app.PutAsJsonAsync($"/api/hooks/{id}", new { @event = "noteAdded", hook = new { type = "command", powershell = "exit 0", bash = "exit 0", name = "mine" } })).Json())["state"]!.GetValue<string>());

        Assert.Equal("off", (await app.PostJson($"/api/hooks/{id}/enabled", new { enabled = false }))["state"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await app.DeleteAsync($"/api/hooks/{id}")).StatusCode);
        Assert.Empty((await app.GetJson("/api/hooks"))["hooks"]!.AsArray());
    }

    [Fact]
    public async Task Hooks_are_checked_and_a_file_that_cant_be_read_is_never_overwritten()
    {
        using var app = AppClient();

        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/api/hooks", new { @event = "whenever", hook = Exit0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/api/hooks", new { @event = "taskCompleted", hook = new { type = "command" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/api/hooks", new { @event = "taskCompleted", hook = new { type = "http", url = "ftp://x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.PostAsJsonAsync("/api/hooks/nope/enabled", new { enabled = true })).StatusCode);

        WriteHooks("{ not json");
        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsJsonAsync("/api/hooks", new { @event = "taskCompleted", hook = Exit0 })).StatusCode);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(Path.Combine(_server.DataDirectory, "hooks.json")));
    }

    [Fact]
    public async Task A_hook_written_by_hand_gets_an_id_that_stays_the_same()
    {
        WriteHooks("""{ "hooks": { "taskCompleted": [ { "type": "command", "bash": "true", "powershell": "exit 0" } ] } }""");

        var first = (await _client.GetJson("/api/hooks"))["hooks"]![0]!["id"]!.GetValue<string>();
        var again = (await _client.GetJson("/api/hooks"))["hooks"]![0]!["id"]!.GetValue<string>();

        Assert.Equal(first, again);
        Assert.Equal("on", (await AppClient().PostJson($"/api/hooks/{first}/enabled", new { enabled = true }))["state"]!.GetValue<string>());
        Assert.Contains($"\"id\": \"{first}\"", await File.ReadAllTextAsync(Path.Combine(_server.DataDirectory, "hooks.json")), StringComparison.Ordinal);
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
