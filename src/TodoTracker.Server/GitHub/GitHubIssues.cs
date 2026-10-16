using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TodoTracker.Core;
using TodoTracker.Core.Connectors;
using TodoTracker.Core.Vault;
using TodoTracker.Server.Plugins;
using TodoTracker.Server.Plugins.Connectors;

namespace TodoTracker.Server.GitHub;

/// <summary>A group and its repository, as Settings shows them.</summary>
public sealed record GitHubGroupView(Guid GroupId, string Group, string? Repo, ConnectorDirection Direction, bool Enabled, DateTimeOffset? LastRun, string? LastResult, IReadOnlyList<string> Problems, int Linked);

/// <summary>
/// The GitHub connector as Settings shows it: whether <c>gh</c> is there and signed in (<see cref="Missing"/> says what
/// to do), whether every group gets its own repository, and each group.
/// </summary>
public sealed record GitHubView(bool On, bool CliInstalled, string? Account, string? Missing, bool EveryGroup, bool MainPlace, IReadOnlyList<GitHubGroupView> Groups);

/// <summary>Connect a group: to <paramref name="Repo"/> ("owner/name"), or (none) to a new private repository.</summary>
public sealed record GitHubConnectRequest(string? Repo, ConnectorDirection? Direction);

public sealed record GitHubEveryGroupRequest(bool On);

/// <summary>What switching where the tasks live did: the folder the app uses from its next start.</summary>
public sealed record GitHubMainPlaceResult(bool MainPlace, string Path, bool RestartRequired);

/// <summary>
/// Each group in step with its own private GitHub repository: tasks are issues (see <see cref="GitHubRemote"/>). A
/// group without a repository gets one made with <c>gh repo create</c> (named <c>todo-&lt;group&gt;</c>, private, with
/// the topic <see cref="Topic"/>, so other computers find it). With "every group", new groups get theirs on the next
/// sync. One thing at a time; nothing is ever deleted on either side.
/// </summary>
public sealed class GitHubIssues(
    IHttpClientFactory http,
    IGitHubCli cli,
    IBoardStore store,
    TimeProvider time,
    TodoTrackerServerOptions options,
    PluginHost plugins,
    SettingsStore settings,
    IHostApplicationLifetime lifetime) : IDisposable
{
    public const string HttpClientName = "github";

    /// <summary>The topic every repository Todo Tracker makes has.</summary>
    public const string Topic = "todo-tracker";

    private const string FilesPrefix = "github-";
    private readonly SemaphoreSlim _busy = new(1, 1);
    private string? _account;

    /// <summary>The whole connector's settings (<see cref="ConnectorSettings.Enabled"/>: every group gets a repository).</summary>
    public ConnectorFiles Main { get; } = new(options.DataDirectory, "github");

    /// <summary>Switched on (always, while the tasks live on GitHub).</summary>
    public bool On => MainPlace || plugins.IsRunning(GitHubConnectorPlugin.Definition.Id);

    /// <summary>
    /// GitHub is where the tasks live (no tasks folder): every group has its repository, repositories made for groups
    /// on other computers become groups here, and issues made elsewhere come in with their task's id.
    /// </summary>
    public bool MainPlace => settings.TasksOnGitHub;

    public void Dispose() => _busy.Dispose();

    public ConnectorFiles FilesOf(Guid groupId) => new(options.DataDirectory, FilesPrefix + groupId.ToString("N", CultureInfo.InvariantCulture));

    public async Task<GitHubView> ViewAsync(CancellationToken cancellationToken)
    {
        if (!On)
        {
            // Off: no gh, no network (Settings only offers to switch it on).
            return new GitHubView(false, false, null, null, false, MainPlace, []);
        }

        var token = await cli.TokenAsync(cancellationToken).ConfigureAwait(false);
        string? missing = null;
        if (token is null)
        {
            missing = cli.Installed ? "Sign in to GitHub: run gh auth login (then try again)." : GitHubCli.InstallHint;
        }
        else
        {
            try
            {
                _account ??= (await SendAsync(token, HttpMethod.Get, "user", null, cancellationToken).ConfigureAwait(false))?["login"]?.GetValue<string>();
            }
            catch (HttpRequestException ex)
            {
                missing = ex.StatusCode is HttpStatusCode.Unauthorized ? "GitHub didn't take the sign-in: run gh auth login again." : ex.Message;
            }
        }

        var groups = await store.ReadAsync(b => b.Groups.Select(g => (g.Id, g.Name)).ToList(), cancellationToken).ConfigureAwait(false);
        return new GitHubView(On, cli.Installed, _account, missing, EveryGroup, MainPlace, [.. groups.Select(g => GroupView(g.Id, g.Name))]);
    }

    private bool EveryGroup => MainPlace || Main.Settings.Enabled;

    private GitHubGroupView GroupView(Guid id, string name)
    {
        var files = FilesOf(id);
        var s = files.Settings;
        return new GitHubGroupView(id, name, s.Target, s.Direction, s.Enabled, s.LastRun, s.LastResult, s.Problems, files.Links.Count(l => l.State == LinkState.Active));
    }

    /// <summary>Keeps a group in step with <paramref name="repo"/>, or with a new private repository made for it; syncs it.</summary>
    public async Task<GitHubGroupView> ConnectAsync(Guid groupId, GitHubConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Running();
        await OneAtATimeAsync(async () =>
        {
            var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
            await ConnectLockedAsync(token, groupId, request.Repo, request.Direction ?? ConnectorDirection.Both, cancellationToken).ConfigureAwait(false);
            await SyncGroupLockedAsync(token, groupId, lifetime.ApplicationStopping).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return await GroupViewAsync(groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every group gets its own repository (now, and new groups at the next sync), or not (the ones there stay).</summary>
    public async Task<GitHubView> SetEveryGroupAsync(bool on, CancellationToken cancellationToken)
    {
        Running();
        Main.Update(s => s with { Enabled = on });
        if (on)
        {
            // Making the repositories takes a while (gh, then a first sync each): in the background; each group shows how it went.
            _ = Task.Run(() => SyncAllAsync(lifetime.ApplicationStopping), CancellationToken.None);
        }

        return await ViewAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops keeping a group in step (its repository and issues stay as they are).</summary>
    public async Task<GitHubGroupView> DisconnectAsync(Guid groupId, CancellationToken cancellationToken)
    {
        Running();
        await OneAtATimeAsync(() =>
        {
            var files = FilesOf(groupId);
            files.Clear();
            files.Update(s => s with { Stopped = true }); // stays stopped, "every group" or not
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return await GroupViewAsync(groupId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitHubGroupView> SyncAsync(Guid groupId, CancellationToken cancellationToken)
    {
        Running();
        await OneAtATimeAsync(async () => await SyncGroupLockedAsync(await TokenAsync(cancellationToken).ConfigureAwait(false), groupId, lifetime.ApplicationStopping).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        return await GroupViewAsync(groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The background sync: with "every group", groups without a repository get one; then every connected group syncs.
    /// Never throws for a problem (each group keeps its own).
    /// </summary>
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        if (!On)
        {
            return;
        }

        await OneAtATimeAsync(async () =>
        {
            var token = await cli.TokenAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                return;
            }

            if (MainPlace)
            {
                try
                {
                    await DiscoverLockedAsync(token, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ConnectorRunner.IsProblem(ex, cancellationToken))
                {
                    Main.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = "Couldn't look for your repositories", Problems = [ex.Message] });
                }
            }

            var groups = await store.ReadAsync(b => b.Groups.Select(g => g.Id).ToList(), cancellationToken).ConfigureAwait(false);
            foreach (var group in groups)
            {
                var files = FilesOf(group);
                if (files.Settings is { Target: null, Stopped: false } && EveryGroup)
                {
                    try
                    {
                        await ConnectLockedAsync(token, group, null, ConnectorDirection.Both, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ConnectorRunner.IsProblem(ex, cancellationToken))
                    {
                        files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = "Couldn't make its repository", Problems = [ex.Message] });
                        continue;
                    }
                }

                if (files.Settings is { Target: not null, Enabled: true })
                {
                    await SyncGroupLockedAsync(token, group, cancellationToken).ConfigureAwait(false);
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the tasks live from the next start (the copying happens then, see <see cref="TodoTrackerHost.PrepareStorage"/>):
    /// GitHub (every group gets its repository) or the tasks folder again. Nothing is deleted either way.
    /// </summary>
    public async Task<GitHubMainPlaceResult> SetMainPlaceAsync(bool on, CancellationToken cancellationToken)
    {
        if (options.IsVaultOverridden)
        {
            throw new InvalidOperationException("This run uses a tasks folder named for it (TODOTRACKER_VAULT or --vault): switch where the tasks live without it.");
        }

        if (on != MainPlace)
        {
            if (on)
            {
                await TokenAsync(cancellationToken).ConfigureAwait(false); // signed in first: the tasks must be able to get there
                plugins.Settings.SetEnabled(GitHubConnectorPlugin.Definition.Id, true);
                Main.Update(s => s with { Enabled = true });
            }

            settings.SetStorage(on ? SettingsStore.GitHubStorage : null);
        }

        return new GitHubMainPlaceResult(on, TodoTrackerHost.VaultPathOf(options, settings), RestartRequired: true);
    }

    /// <summary>
    /// Repositories made for groups (the <see cref="Topic"/> topic) that no group here has: each becomes a group (by
    /// the name in its description), or joins the group of that name that has no repository yet.
    /// </summary>
    private async Task DiscoverLockedAsync(string token, CancellationToken cancellationToken)
    {
        var mapped = (await store.ReadAsync(b => b.Groups.Select(g => g.Id).ToList(), cancellationToken).ConfigureAwait(false))
            .Select(id => FilesOf(id).Settings.Target).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page < 50; page++)
        {
            var repos = await SendAsync(token, HttpMethod.Get, $"user/repos?affiliation=owner&per_page=100&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            foreach (var repo in repos.OfType<JsonObject>())
            {
                var fullName = repo["full_name"]?.GetValue<string>();
                if (fullName is null || mapped.Contains(fullName) || repo["private"]?.GetValue<bool>() != true
                    || repo["topics"] is not JsonArray topics || !topics.Any(t => t?.GetValue<string>() == Topic))
                {
                    continue;
                }

                var description = repo["description"]?.GetValue<string>() ?? string.Empty;
                var name = description.StartsWith(Description(string.Empty), StringComparison.Ordinal) && description.Length > Description(string.Empty).Length
                    ? description[Description(string.Empty).Length..].Trim()
                    : repo["name"]?.GetValue<string>() ?? fullName;
                var found = await store.UpdateAsync(b =>
                {
                    // The group of that name joins it, unless it has a repository already or was stopped here.
                    if (b.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
                    {
                        return FilesOf(same.Id).Settings is { Target: null, Stopped: false } ? same.Id : (Guid?)null;
                    }

                    return b.AddGroup(name, null, new Actor(ActorKind.Connector, "GitHub"), time.GetUtcNow()).Id;
                }, cancellationToken).ConfigureAwait(false);
                if (found is not { } group)
                {
                    continue;
                }

                FilesOf(group).Update(s => s with { Target = fullName, TargetName = name, GroupId = group, Direction = ConnectorDirection.Both, Enabled = true });
                mapped.Add(fullName);
            }

            if (repos.Count < 100)
            {
                return;
            }
        }
    }

    // ---- inside --------------------------------------------------------------------------------

    private void Running()
    {
        if (!On)
        {
            throw new KeyNotFoundException("GitHub is switched off (Settings › Connected apps).");
        }
    }

    private async Task<string> TokenAsync(CancellationToken cancellationToken) =>
        await cli.TokenAsync(cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException(cli.Installed ? "Sign in to GitHub first: run gh auth login." : GitHubCli.InstallHint);

    private async Task<GitHubGroupView> GroupViewAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var name = await store.ReadAsync(b => b.Groups.FirstOrDefault(g => g.Id == groupId)?.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("There is no such group.");
        return GroupView(groupId, name);
    }

    private async Task ConnectLockedAsync(string token, Guid groupId, string? repo, ConnectorDirection direction, CancellationToken cancellationToken)
    {
        var name = await store.ReadAsync(b => b.Groups.FirstOrDefault(g => g.Id == groupId)?.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("There is no such group.");
        var target = repo is null
            ? await MakeRepoAsync(token, name, cancellationToken).ConfigureAwait(false)
            : await CheckRepoAsync(token, GitHubRemote.ValidRepo(repo), cancellationToken).ConfigureAwait(false);
        var files = FilesOf(groupId);
        files.Update(s =>
        {
            if (s.Target != target)
            {
                files.SaveLinks([]); // another repository: its links start over (issues carry task ids: nothing is copied)
            }

            return s with { Target = target, TargetName = name, GroupId = groupId, Direction = direction, Enabled = true, Stopped = false };
        });
    }

    /// <summary>
    /// The group's repository: one this app made for it before (its name, with the topic), else a new private one
    /// (<c>todo-work</c>, or <c>todo-work-2</c> when that name is someone else's).
    /// </summary>
    private async Task<string> MakeRepoAsync(string token, string group, CancellationToken cancellationToken)
    {
        var owner = (await SendAsync(token, HttpMethod.Get, "user", null, cancellationToken).ConfigureAwait(false))?["login"]?.GetValue<string>()
            ?? throw new InvalidOperationException("GitHub didn't say who you are.");
        var baseName = RepoName(group);
        for (var n = 1; n < 20; n++)
        {
            var name = n == 1 ? baseName : $"{baseName}-{n}";
            var existing = await SendAsync(token, HttpMethod.Get, $"repos/{owner}/{name}", null, cancellationToken, missingIsNull: true).ConfigureAwait(false);
            if (existing is null)
            {
                var made = await cli.CreateRepoAsync(name, Description(group), cancellationToken).ConfigureAwait(false);
                await TagAsync(token, made, cancellationToken).ConfigureAwait(false);
                return made;
            }

            if (existing["topics"] is JsonArray topics && topics.Any(t => t?.GetValue<string>() == Topic)
                && existing["description"]?.GetValue<string>() == Description(group) && existing["private"]?.GetValue<bool>() == true)
            {
                return $"{owner}/{name}"; // made for this group before (on this computer or another)
            }
        }

        throw new InvalidOperationException($"Couldn't find a free name for the repository of \"{group}\".");
    }

    private async Task<string> CheckRepoAsync(string token, string repo, CancellationToken cancellationToken)
    {
        var found = await SendAsync(token, HttpMethod.Get, $"repos/{repo}", null, cancellationToken, missingIsNull: true).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"There's no repository {repo} you can see.");
        if (found["has_issues"]?.GetValue<bool>() == false)
        {
            throw new InvalidOperationException($"{repo} has issues switched off (its Settings on GitHub).");
        }

        if (found["private"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException($"{repo} is public: your tasks there would be seen by everyone. Pick a private repository.");
        }

        return found["full_name"]?.GetValue<string>() ?? repo;
    }

    /// <summary>The topic, so the repository is found from other computers (a repository that can't be tagged still works).</summary>
    private async Task TagAsync(string token, string repo, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(token, HttpMethod.Put, $"repos/{repo}/topics", new JsonObject { ["names"] = new JsonArray(Topic) }, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Not tagged: it's still this group's repository here.
        }
    }

    public static string Description(string group) => $"Todo Tracker: {group}";

    /// <summary><c>todo-</c> and the group's name in letters, digits and dashes ("Side projects" → todo-side-projects).</summary>
    public static string RepoName(string group)
    {
        var slug = new StringBuilder();
        foreach (var c in (group ?? string.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var name = slug.ToString().Trim('-');
        return "todo-" + (name.Length == 0 ? "tasks" : name[..Math.Min(name.Length, 60)].TrimEnd('-'));
    }

    private async Task SyncGroupLockedAsync(string token, Guid groupId, CancellationToken cancellationToken)
    {
        var files = FilesOf(groupId);
        var current = files.Settings;
        if (current.Target is not { } repo)
        {
            throw new InvalidOperationException("Connect the group to a repository first.");
        }

        try
        {
            var remote = new GitHubRemote(http.CreateClient(HttpClientName), _ => Task.FromResult(token), repo);
            var runner = new ConnectorRunner($"github:{repo}", "GitHub", store, time, options.TimeZone);
            var run = await runner.RunAsync(remote, new ConnectorTarget(groupId, current.Direction, AdoptMarked: MainPlace, GroupOnly: true), files.Links, cancellationToken).ConfigureAwait(false);
            files.SaveLinks(run.Links);
            if (run.Done.Any(d => d.Op is UpdateRemote or ArchiveRemote))
            {
                await PruneAsync(remote, cancellationToken).ConfigureAwait(false);
            }
            files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = ConnectorService.Summary(run), Problems = run.Problems });
        }
        catch (Exception ex) when (ConnectorRunner.IsProblem(ex, cancellationToken))
        {
            files.Update(s => s with { LastRun = time.GetUtcNow(), LastResult = "Couldn't sync", Problems = [ex.Message] });
        }
    }

    /// <summary>Old time labels go (a failure here is no sync problem: they're tried again next time).</summary>
    private static async Task PruneAsync(GitHubRemote remote, CancellationToken cancellationToken)
    {
        try
        {
            await remote.PruneLabelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // next time
        }
    }

    private async Task OneAtATimeAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<JsonNode?> SendAsync(string token, HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("TodoTracker");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (missingIsNull && response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"GitHub: {(text.Length > 0 && text[0] == '{' ? JsonNode.Parse(text)?["message"]?.GetValue<string>() : null) ?? response.ReasonPhrase} ({(int)response.StatusCode})", null, response.StatusCode);
        }

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }
}

/// <summary>
/// Syncs the GitHub repositories every 10 minutes (a minute after the app starts). While GitHub is where the tasks live:
/// every 2 minutes, and half a minute after a change here.
/// </summary>
public sealed partial class GitHubLoop(GitHubIssues github, IBoardStore store, TodoTrackerServerOptions options, ILogger<GitHubLoop> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EveryAsMainPlace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan AfterAChange = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _changed = new(0, 1);

    [LoggerMessage(Level = LogLevel.Warning, Message = "GitHub sync failed")]
    private static partial void SyncFailed(ILogger logger, Exception exception);

    public override void Dispose()
    {
        store.Changed -= OnChanged;
        _changed.Dispose();
        base.Dispose();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        try
        {
            if (github.MainPlace && _changed.CurrentCount == 0)
            {
                _changed.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // already signalled, or stopping
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableBackgroundLoop)
        {
            return;
        }

        store.Changed += OnChanged;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await github.SyncAllAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ConnectorRunner.IsProblem(ex, stoppingToken))
                {
                    SyncFailed(logger, ex);
                }

                // Changes the sync itself made don't count as a reason to sync again.
                while (_changed.CurrentCount > 0)
                {
                    await _changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                }

                if (await _changed.WaitAsync(github.MainPlace ? EveryAsMainPlace : Every, stoppingToken).ConfigureAwait(false))
                {
                    await Task.Delay(AfterAChange, stoppingToken).ConfigureAwait(false); // a few changes in a row: one sync
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }
}
