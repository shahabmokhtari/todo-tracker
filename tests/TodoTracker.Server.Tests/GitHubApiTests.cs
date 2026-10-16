using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TodoTracker.Core;
using TodoTracker.Core.Tests.Connectors;
using TodoTracker.Server.GitHub;

namespace TodoTracker.Server.Tests;

/// <summary>GitHub Issues: each group in step with its own private repository, made with the GitHub CLI.</summary>
public sealed class GitHubApiTests
{
    private const string Base = "/api/plugins/connector-github";

    private sealed class FakeCli(FakeGitHub github) : IGitHubCli
    {
        public string? Token { get; set; } = "gho_test";

        public List<(string Name, string Description)> Created { get; } = [];

        public bool Installed => true;

        public Task<string?> TokenAsync(CancellationToken cancellationToken) => Task.FromResult(Token);

        public Task<string> CreateRepoAsync(string name, string description, CancellationToken cancellationToken)
        {
            Created.Add((name, description));
            github.AddRepo($"me/{name}", description);
            return Task.FromResult($"me/{name}");
        }
    }

    private static async Task<(ServerFixture Server, FakeGitHub GitHub, FakeCli Cli)> StartAsync(bool on = true)
    {
        var github = new FakeGitHub();
        var cli = new FakeCli(github);
        var server = await ServerFixture.StartAsync(
            o =>
            {
                Directory.CreateDirectory(o.DataDirectory);
                File.WriteAllText(Path.Combine(o.DataDirectory, "plugins.json"), $$"""{"connector-github": {{(on ? "true" : "false")}}}""");
            },
            s =>
            {
                s.AddHttpClient(GitHubIssues.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => github);
                s.Replace(ServiceDescriptor.Singleton<IGitHubCli>(cli));
            });
        return (server, github, cli);
    }

    [Fact]
    public async Task Connecting_a_group_makes_its_private_repository_with_gh_tags_it_and_sends_its_tasks()
    {
        var (server, github, cli) = await StartAsync();
        await using var _ = server;
        var client = server.Client();
        var group = await server.Store.UpdateAsync(b => b.AddGroup("Side projects", null, Actor.User, ServerFixture.T0).Id);
        await server.Store.UpdateAsync(b => b.AddTask(new NewTask("Ship the beta") { GroupId = group, Priority = Priority.High }, Actor.User, ServerFixture.T0));

        var view = await client.GetJson(Base);
        Assert.Equal("me", view["account"]!.GetValue<string>());
        Assert.Null(view["missing"]);

        var connected = await (await client.PostAsJsonAsync($"{Base}/groups/{group}/connect", new { })).Json();

        Assert.Equal([("todo-side-projects", "Todo Tracker: Side projects")], cli.Created);
        Assert.Equal("me/todo-side-projects", connected["repo"]!.GetValue<string>());
        Assert.Equal(1, connected["linked"]!.GetValue<int>());
        Assert.Equal(["todo-tracker"], github.Repos["me/todo-side-projects"]["topics"]!.AsArray().Select(t => t!.GetValue<string>()));
        github.Repo = "me/todo-side-projects";
        var issue = Assert.Single(github.Issues.Values);
        Assert.Equal("Ship the beta", issue["title"]!.GetValue<string>());
        Assert.Contains("priority:high", github.LabelsOf(1));

        // Connecting again (or from another computer) finds the repository made for it: no second one.
        await client.DeleteAsync($"{Base}/groups/{group}");
        await (await client.PostAsJsonAsync($"{Base}/groups/{group}/connect", new { })).Json();
        Assert.Single(cli.Created);
        Assert.Single(github.Issues.Values);
    }

    [Fact]
    public async Task Every_group_gets_its_own_repository_and_a_name_taken_by_another_repository_gets_a_number()
    {
        var (server, github, cli) = await StartAsync();
        await using var _ = server;
        var client = server.Client();
        github.AddRepo("me/todo-personal", "My own notes");
        var groups = await server.Store.ReadAsync(b => b.Groups.Select(g => g.Name).ToList());

        Assert.True((await (await client.PutAsJsonAsync($"{Base}/every-group", new { on = true })).Json())["everyGroup"]!.GetValue<bool>());

        // The repositories are made in the background (one at a time, so this waits for that run).
        await server.App.Services.GetRequiredService<GitHubIssues>().SyncAllAsync(CancellationToken.None);
        var view = await client.GetJson(Base);
        Assert.Equal(groups.Count - 1, cli.Created.Count); // every group but Work (below)
        Assert.Contains(cli.Created, c => c.Name == "todo-personal-2");
        Assert.All(view["groups"]!.AsArray(), g => Assert.StartsWith("me/todo-", g!["repo"]!.GetValue<string>(), StringComparison.Ordinal));
        // The Work repository was made for Work before (its description and topic): used, not made again.
        Assert.DoesNotContain(cli.Created, c => c.Name == "todo-work");
    }

    [Fact]
    public async Task Without_gh_sign_in_it_says_what_to_do_and_off_it_does_nothing()
    {
        var (server, _, cli) = await StartAsync();
        await using (server)
        {
            cli.Token = null;
            var view = await server.Client().GetJson(Base);
            Assert.Equal("Sign in to GitHub: run gh auth login (then try again).", view["missing"]!.GetValue<string>());
            var group = await server.Store.ReadAsync(b => b.Groups[0].Id);
            Assert.Equal(HttpStatusCode.Conflict, (await server.Client().PostAsJsonAsync($"{Base}/groups/{group}/connect", new { })).StatusCode);
        }

        var (off, _, offCli) = await StartAsync(on: false);
        await using (off)
        {
            var group = await off.Store.ReadAsync(b => b.Groups[0].Id);
            Assert.Equal(HttpStatusCode.NotFound, (await off.Client().PostAsJsonAsync($"{Base}/groups/{group}/connect", new { })).StatusCode);
            Assert.Empty(offCli.Created);
        }
    }

    [Fact]
    public async Task A_public_repository_is_refused_and_a_stopped_group_stays_stopped()
    {
        var (server, github, cli) = await StartAsync();
        await using var _ = server;
        var client = server.Client();
        var group = await server.Store.UpdateAsync(b => b.AddGroup("Errands", null, Actor.User, ServerFixture.T0).Id);
        github.AddRepo("me/open-notes")["private"] = false;

        var refused = await client.PostAsJsonAsync($"{Base}/groups/{group}/connect", new { repo = "me/open-notes" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("is public", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Stopped while every group has its repository: it isn't given one again.
        await (await client.PostAsJsonAsync($"{Base}/groups/{group}/connect", new { })).Json();
        await client.DeleteAsync($"{Base}/groups/{group}");
        await client.PutAsJsonAsync($"{Base}/every-group", new { on = true });
        var github2 = server.App.Services.GetRequiredService<GitHubIssues>();
        await github2.SyncAllAsync(CancellationToken.None);
        Assert.Null((await client.GetJson(Base))["groups"]!.AsArray().Single(g => g!["group"]!.GetValue<string>() == "Errands")!["repo"]);
        Assert.Single(cli.Created, c => c.Name == "todo-errands");
    }

    /// <summary>The owner's session: opened from a link the app made itself (as the sidebar and the tray do).</summary>
    private static async Task<HttpClient> AppClientAsync(ServerFixture server)
    {
        var link = new Uri(TodoTrackerHost.CreateLaunchUrl(server.App.Services));
        using var client = server.Client(authenticated: false);
        using var response = await client.GetAsync(link.PathAndQuery);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tt_session=", StringComparison.Ordinal)).Split(';')[0]["tt_session=".Length..];
        var session = server.Client(authenticated: false);
        session.DefaultRequestHeaders.Add("Cookie", $"tt_session={cookie}");
        session.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");
        return session;
    }

    [Fact]
    public async Task Tasks_can_live_on_GitHub_without_a_tasks_folder_and_go_back_with_nothing_lost()
    {
        var (server, _, _) = await StartAsync();
        await using var _ = server;
        using var app = await AppClientAsync(server);
        await server.Store.UpdateAsync(b => b.AddTask(new NewTask("Water the plants"), Actor.User, ServerFixture.T0));
        var options = server.App.Services.GetRequiredService<TodoTrackerServerOptions>();
        var settings = server.App.Services.GetRequiredService<SettingsStore>();
        var folder = TodoTrackerHost.VaultPathOf(options, settings);

        // Only the person at this computer moves the tasks (not an agent with the API token).
        Assert.Equal(HttpStatusCode.Forbidden, (await server.Client().PutAsJsonAsync($"{Base}/main-place", new { on = true })).StatusCode);
        var on = await (await app.PutAsJsonAsync($"{Base}/main-place", new { on = true })).Json();

        // Asked, not done: tt and tt mcp keep using the tasks folder until the app moves them at its next start.
        var copy = TodoTrackerHost.GitHubCopyOf(options);
        Assert.Equal((copy, true), (on["path"]!.GetValue<string>(), on["restartRequired"]!.GetValue<bool>()));
        Assert.Equal(folder, TodoTrackerHost.VaultPathOf(options, settings));
        var view = await app.GetJson(Base);
        Assert.Equal((false, true), (view["mainPlace"]!.GetValue<bool>(), view["mainPlaceAsked"]!.GetValue<bool>()));

        Assert.Null(TodoTrackerHost.PrepareStorage(options, settings, server.Time));
        Assert.Equal(copy, TodoTrackerHost.VaultPathOf(options, settings));
        Assert.Contains(Directory.EnumerateFiles(copy, "*.md", SearchOption.AllDirectories), f => f.EndsWith("Water the plants.md", StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories), f => f.EndsWith("Water the plants.md", StringComparison.Ordinal));

        // Back to the tasks folder: the copy goes there; what the folder had, and the copy, are kept renamed.
        File.WriteAllText(Path.Combine(copy, "Made while on GitHub.md"), "# Made while on GitHub\n");
        await (await app.PutAsJsonAsync($"{Base}/main-place", new { on = false })).Json();
        Assert.Equal(copy, TodoTrackerHost.VaultPathOf(options, settings));
        Assert.Null(TodoTrackerHost.PrepareStorage(options, settings, server.Time));

        Assert.Equal(folder, TodoTrackerHost.VaultPathOf(options, settings));
        Assert.True(File.Exists(Path.Combine(folder, "Made while on GitHub.md")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(folder)!, Path.GetFileName(folder) + ".before-github-*"));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(copy)!, Path.GetFileName(copy) + ".returned-*"));
    }

    [Fact]
    public async Task A_move_that_something_gets_in_the_way_of_is_tried_again_and_never_stops_the_app()
    {
        var (server, _, _) = await StartAsync();
        await using var _ = server;
        var options = server.App.Services.GetRequiredService<TodoTrackerServerOptions>();
        var settings = server.App.Services.GetRequiredService<SettingsStore>();
        settings.SetStorage(SettingsStore.GitHubStorage);
        Assert.Null(TodoTrackerHost.PrepareStorage(options, settings, server.Time));
        var copy = TodoTrackerHost.GitHubCopyOf(options);
        settings.SetStorage(null);

        // A file in the way of the copy going back (a file where the new folder goes): it says so, and nothing moved.
        var folder = options.ResolveVaultPath(settings.Current.VaultPath);
        File.WriteAllText($"{folder}.new-{server.Time.GetUtcNow():yyyyMMdd-HHmmss}", "in the way");
        Assert.NotNull(TodoTrackerHost.PrepareStorage(options, settings, server.Time));
        Assert.Equal(copy, TodoTrackerHost.VaultPathOf(options, settings));

        // Next start, it goes through (the folder already put aside isn't put aside again).
        server.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(TodoTrackerHost.PrepareStorage(options, settings, server.Time));
        Assert.Equal(folder, TodoTrackerHost.VaultPathOf(options, settings));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(folder)!, Path.GetFileName(folder) + ".before-github-*"));
    }

    [Fact]
    public void A_tasks_folder_holding_the_data_folder_is_refused()
    {
        var data = Path.Combine(Path.GetTempPath(), "tt-nest", "data");
        var options = new TodoTrackerServerOptions { DataDirectory = data };
        Assert.NotNull(TodoTrackerHost.StorageProblem(options, Path.Combine(Path.GetTempPath(), "tt-nest")));
        Assert.NotNull(TodoTrackerHost.StorageProblem(options, Path.Combine(data, "github-tasks", "inner")));
        Assert.Null(TodoTrackerHost.StorageProblem(options, Path.Combine(data, "vault")));
        Assert.Null(TodoTrackerHost.StorageProblem(options, Path.Combine(Path.GetTempPath(), "elsewhere")));
    }

    [Fact]
    public async Task On_GitHub_other_computers_repositories_become_groups_and_their_issues_come_in_with_their_ids()
    {
        var (server, github, cli) = await StartAsync();
        await using var _ = server;
        var settings = server.App.Services.GetRequiredService<SettingsStore>();
        settings.SetStorage(SettingsStore.GitHubStorage);
        settings.SetActiveStorage(SettingsStore.GitHubStorage);
        var elsewhere = Guid.NewGuid();
        github.AddRepo("me/todo-errands", "Todo Tracker: Errands", GitHubIssues.Topic);
        github.AddTo("me/todo-errands", "Buy stamps", $"<!-- todotracker:id={elsewhere:D} -->", false, "snooze:2026-01-09T08:00Z");
        github.AddRepo("me/notes", "Not ours");

        await server.App.Services.GetRequiredService<GitHubIssues>().SyncAllAsync(CancellationToken.None);

        var groups = await server.Store.ReadAsync(b => b.Groups.ToDictionary(g => g.Name, g => g.Id));
        Assert.True(groups.ContainsKey("Errands"));
        Assert.False(groups.ContainsKey("notes"));
        var stamps = await server.Store.ReadAsync(b => b.Find(elsewhere));
        Assert.NotNull(stamps);
        Assert.Equal((groups["Errands"], new DateTimeOffset(2026, 1, 9, 8, 0, 0, TimeSpan.Zero)), (stamps.GroupId, stamps.NextActionAt));

        // Work had a repository made for it elsewhere (its description): joined, not made again. The rest get theirs.
        var view = await server.Client().GetJson(Base);
        Assert.Equal("me/todo-work", view["groups"]!.AsArray().Single(g => g!["group"]!.GetValue<string>() == "Work")!["repo"]!.GetValue<string>());
        Assert.DoesNotContain(cli.Created, c => c.Name is "todo-work" or "todo-errands");
        Assert.All(view["groups"]!.AsArray(), g => Assert.NotNull(g!["repo"]));

        // A group deleted here stays deleted (its repository isn't taken for one made elsewhere).
        await server.Store.UpdateAsync(b => { b.DeleteGroup(groups["Errands"], groups["Work"], Actor.User, ServerFixture.T0); return 0; });
        await server.App.Services.GetRequiredService<GitHubIssues>().SyncAllAsync(CancellationToken.None);
        Assert.False(await server.Store.ReadAsync(b => b.Groups.Any(g => g.Name == "Errands")));
    }

    [Theory]
    [InlineData("Work", "todo-work")]
    [InlineData("Side projects!", "todo-side-projects")]
    [InlineData("Café & Crème", "todo-cafe-creme")]
    [InlineData("💡", "todo-tasks")]
    public void Repository_names_come_from_the_group(string group, string name) => Assert.Equal(name, GitHubIssues.RepoName(group));

    [Theory]
    [InlineData("https://github.com/me/todo-work\n", "me/todo-work")]
    [InlineData("✓ Created repository me/todo-work on GitHub\n  https://github.com/me/todo-work\n", "me/todo-work")]
    [InlineData("nothing useful", null)]
    public void The_repository_gh_made_is_read_from_what_it_prints(string output, string? repo) => Assert.Equal(repo, GitHubCli.RepoOf(output));

    [Fact]
    public void gh_is_found_where_installers_put_it_when_the_app_has_a_short_PATH()
    {
        // A Mac app started from the Dock: /usr/bin:/bin only, gh from Homebrew.
        Assert.Equal("/opt/homebrew/bin/gh", GitHubCli.Find("/usr/bin:/bin", p => p == "/opt/homebrew/bin/gh", windows: false, home: "/Users/me"));
        // Linux: ~/.local/bin, and the PATH first when it's there.
        Assert.Equal("/home/me/.local/bin/gh", GitHubCli.Find("/usr/bin", p => p == "/home/me/.local/bin/gh", windows: false, home: "/home/me"));
        Assert.Equal("/custom/gh", GitHubCli.Find("/custom:/usr/bin", p => p is "/custom/gh" or "/usr/bin/gh", windows: false, home: "/home/me"));
        Assert.Null(GitHubCli.Find(null, _ => false, windows: false, home: "/home/me"));
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
            Assert.Equal(programFiles, GitHubCli.Find(@"C:\Windows", p => p == programFiles, windows: true));
        }
    }
}
