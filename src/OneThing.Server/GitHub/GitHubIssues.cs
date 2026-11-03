using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OneThing.Core;
using OneThing.Core.Connectors;
using OneThing.Core.Vault;
using OneThing.Server.Plugins;
using OneThing.Server.Plugins.Connectors;

namespace OneThing.Server.GitHub;

/// <summary>A group and its repository, as Settings shows them (<paramref name="Label"/>: its label there, <c>group:Work</c>).</summary>
public sealed record GitHubGroupView(Guid GroupId, string Group, string? Repo, ConnectorDirection Direction, bool Enabled, DateTimeOffset? LastRun, string? LastResult, IReadOnlyList<string> Problems, int Linked, string? Label = null);

/// <summary>
/// The GitHub connector as Settings shows it: whether <c>gh</c> is there and signed in (<see cref="Missing"/> says what
/// to do), whether every group gets a repository (<paramref name="SharedRepo"/>: all in that one), and each group.
/// </summary>
/// <param name="MainPlace">The tasks live on GitHub now.</param>
/// <param name="MainPlaceAsked">They're to live on GitHub (different from <paramref name="MainPlace"/>: until the app restarts).</param>
/// <param name="Problems">What went wrong moving the tasks, or finding repositories.</param>
public sealed record GitHubView(bool On, bool CliInstalled, string? Account, string? Missing, bool EveryGroup, bool MainPlace, IReadOnlyList<GitHubGroupView> Groups, bool MainPlaceAsked = false, IReadOnlyList<string>? Problems = null, string? SharedRepo = null);

/// <summary>Connect a group: to <paramref name="Repo"/> ("owner/name", one other groups may use too), or (none) to a new private repository.</summary>
public sealed record GitHubConnectRequest(string? Repo, ConnectorDirection? Direction);

/// <summary>Every group gets a repository (<paramref name="SharedRepo"/>: all go into that one, each with its label), or not.</summary>
public sealed record GitHubEveryGroupRequest(bool On, string? SharedRepo = null);

/// <summary>What switching where the tasks live did: the folder the app uses from its next start.</summary>
public sealed record GitHubMainPlaceResult(bool MainPlace, string Path, bool RestartRequired);

/// <summary>
/// Groups in step with private GitHub repositories: tasks are issues (see <see cref="GitHubRemote"/>), and each group's
/// issues carry its label (<c>group:Work</c>), so groups can share a repository (personal, work… in one). A group
/// without a repository gets one made with <c>gh repo create</c> (named <c>todo-&lt;group&gt;</c>, private, with the
/// topic <see cref="Topic"/>, so other computers find it), or goes into the shared repository when one is set. With
/// "every group", new groups get theirs on the next sync. One thing at a time; nothing is ever deleted on either side.
/// </summary>
public sealed class GitHubIssues(
    IHttpClientFactory http,
    IGitHubCli cli,
    IBoardStore store,
    TimeProvider time,
    OneThingServerOptions options,
    PluginHost plugins,
    SettingsStore settings,
    IHostApplicationLifetime lifetime) : IDisposable
{
    public const string HttpClientName = "github";

    /// <summary>The topic every repository One Thing makes has.</summary>
    public const string Topic = "onething";

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
    public bool MainPlace => settings.UsingGitHub;

    public void Dispose() => _busy.Dispose();

    public ConnectorFiles FilesOf(Guid groupId) => new(options.DataDirectory, FilesPrefix + groupId.ToString("N", CultureInfo.InvariantCulture));

    public async Task<GitHubView> ViewAsync(CancellationToken cancellationToken)
    {
        if (!On)
        {
            // Off: no gh, no network (Settings only offers to switch it on).
            return new GitHubView(false, false, null, null, false, MainPlace, [], settings.TasksOnGitHub);
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
        return new GitHubView(On, cli.Installed, _account, missing, EveryGroup, MainPlace, [.. groups.Select(g => GroupView(g.Id, g.Name))], settings.TasksOnGitHub, Main.Settings.Problems, Main.Settings.SharedRepo);
    }

    private bool EveryGroup => MainPlace || Main.Settings.Enabled;

    private GitHubGroupView GroupView(Guid id, string name)
    {
        var files = FilesOf(id);
        var s = files.Settings;
        return new GitHubGroupView(id, name, s.Target, s.Direction, s.Enabled, s.LastRun, s.LastResult, s.Problems, files.Links.Count(l => l.State == LinkState.Active), s.Label);
    }

    /// <summary>Keeps a group in step with <paramref name="repo"/>, or with a new private repository made for it; syncs it.</summary>
    public async Task<GitHubGroupView> ConnectAsync(Guid groupId, GitHubConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Running();
        await OneAtATimeAsync(async () =>
        {
            var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
            await ConnectLockedAsync(token, groupId, request.Repo, request.Direction ?? ConnectorDirection.Both, mayClaim: true, cancellationToken).ConfigureAwait(false);
            await SyncGroupLockedAsync(token, groupId, lifetime.ApplicationStopping).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return await GroupViewAsync(groupId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Every group gets a repository (now, and new groups at the next sync), or not (the ones there stay):
    /// its own, or <paramref name="sharedRepo"/> ("owner/name", private) for all of them, each with its label.
    /// </summary>
    public async Task<GitHubView> SetEveryGroupAsync(bool on, string? sharedRepo, CancellationToken cancellationToken)
    {
        Running();
        string? shared = null;
        if (!string.IsNullOrWhiteSpace(sharedRepo))
        {
            var token = await TokenAsync(cancellationToken).ConfigureAwait(false);
            shared = await CheckRepoAsync(token, GitHubRemote.ValidRepo(sharedRepo), cancellationToken).ConfigureAwait(false);
            await TagAsync(token, shared, cancellationToken).ConfigureAwait(false); // other computers find it
        }

        Main.Update(s => s with { Enabled = on, SharedRepo = shared });
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
                    await ForgetDeletedGroupsAsync(cancellationToken).ConfigureAwait(false);
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
                        var shared = Main.Settings.SharedRepo;
                        await ConnectLockedAsync(token, group, shared, ConnectorDirection.Both, mayClaim: shared is null, cancellationToken).ConfigureAwait(false);
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
    /// Where the tasks live from the next start (the copying happens then, see <see cref="OneThingHost.PrepareStorage"/>):
    /// GitHub (every group gets its repository) or the tasks folder again. Nothing is deleted either way.
    /// </summary>
    public async Task<GitHubMainPlaceResult> SetMainPlaceAsync(bool on, CancellationToken cancellationToken)
    {
        if (options.IsVaultOverridden)
        {
            throw new InvalidOperationException("This run uses a tasks folder named for it (ONETHING_VAULT or --vault): switch where the tasks live without it.");
        }

        if (OneThingHost.StorageProblem(options, options.ResolveVaultPath(settings.Current.VaultPath)) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        if (on != settings.TasksOnGitHub)
        {
            if (on)
            {
                await TokenAsync(cancellationToken).ConfigureAwait(false); // signed in first: the tasks must be able to get there
                plugins.Settings.SetEnabled(GitHubConnectorPlugin.Definition.Id, true);
                Main.Update(s => s with { Enabled = true });
            }

            settings.SetStorage(on ? SettingsStore.GitHubStorage : null);
            Main.Update(s => s with { Problems = [] });
        }

        var path = on ? OneThingHost.GitHubCopyOf(options) : options.ResolveVaultPath(settings.Current.VaultPath);
        return new GitHubMainPlaceResult(on, path, RestartRequired: on != MainPlace);
    }

    /// <summary>A group deleted here: its repository is remembered, so it isn't brought back as a group.</summary>
    private async Task ForgetDeletedGroupsAsync(CancellationToken cancellationToken)
    {
        var folder = Path.Combine(options.DataDirectory, "connectors");
        if (!Directory.Exists(folder))
        {
            return;
        }

        var groups = (await store.ReadAsync(b => b.Groups.Select(g => g.Id).ToList(), cancellationToken).ConfigureAwait(false)).ToHashSet();
        foreach (var dir in Directory.GetDirectories(folder, FilesPrefix + "*"))
        {
            if (Guid.TryParseExact(Path.GetFileName(dir)[FilesPrefix.Length..], "N", out var id) && !groups.Contains(id))
            {
                var files = FilesOf(id);
                if (files.Settings.Target is { } repo)
                {
                    var forgotten = Key(repo, files.Settings.Label);
                    Main.Update(s => s with { Forgotten = [.. s.Forgotten.Append(forgotten).Distinct(StringComparer.OrdinalIgnoreCase)] });
                }

                files.Clear();
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    /// <summary>
    /// Groups made on other computers, in repositories with the <see cref="Topic"/> topic: each <c>group:</c> label no
    /// group here has becomes a group of that name (several groups can share a repository), or joins the group of that
    /// name that has no repository yet. A repository from before labels (none of them) is one group, named in its
    /// description. Groups deleted here (<see cref="ConnectorSettings.Forgotten"/>) aren't brought back.
    /// </summary>
    private async Task DiscoverLockedAsync(string token, CancellationToken cancellationToken)
    {
        var mapped = (await store.ReadAsync(b => b.Groups.Select(g => g.Id).ToList(), cancellationToken).ConfigureAwait(false))
            .Select(id => FilesOf(id).Settings).Where(s => s.Target is not null).Select(s => Key(s.Target!, s.Label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var forgotten = Main.Settings.Forgotten.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page < 50; page++)
        {
            var repos = await SendAsync(token, HttpMethod.Get, $"user/repos?affiliation=owner&per_page=100&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            foreach (var repo in repos.OfType<JsonObject>())
            {
                var fullName = repo["full_name"]?.GetValue<string>();
                // Forgotten whole, or a group here from before labels has all of it.
                if (fullName is null || forgotten.Contains(fullName) || mapped.Contains(fullName) || repo["private"]?.GetValue<bool>() != true
                    || repo["topics"] is not JsonArray topics || !topics.Any(t => t?.GetValue<string>() == Topic))
                {
                    continue;
                }

                var labels = (await Remote(token, fullName, null).LabelNamesAsync(cancellationToken).ConfigureAwait(false)).Where(GitHubRemote.IsGroupLabel).ToList();
                if (labels.Count == 0)
                {
                    // From before labels: the repository is one group's (unless a group here uses it already).
                    if (mapped.Any(m => m.StartsWith(fullName + "#", StringComparison.OrdinalIgnoreCase)) || string.Equals(fullName, Main.Settings.SharedRepo, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var description = repo["description"]?.GetValue<string>() ?? string.Empty;
                    var name = description.StartsWith(Description(string.Empty), StringComparison.Ordinal) && description.Length > Description(string.Empty).Length
                        ? description[Description(string.Empty).Length..].Trim()
                        : repo["name"]?.GetValue<string>() ?? fullName;
                    if (await JoinAsync(name, fullName, null, cancellationToken).ConfigureAwait(false))
                    {
                        mapped.Add(Key(fullName, null));
                    }

                    continue;
                }

                foreach (var label in labels)
                {
                    var key = Key(fullName, label);
                    if (mapped.Contains(key) || forgotten.Contains(key) || label.Length <= GitHubRemote.GroupPrefix.Length)
                    {
                        continue;
                    }

                    if (await JoinAsync(label[GitHubRemote.GroupPrefix.Length..].Trim(), fullName, label, cancellationToken).ConfigureAwait(false))
                    {
                        mapped.Add(key);
                    }
                }
            }

            if (repos.Count < 100)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The group named <paramref name="name"/> (made if there's none) is kept in step with <paramref name="repo"/>
    /// (with <paramref name="label"/>), unless it has a repository already or was stopped here.
    /// </summary>
    private async Task<bool> JoinAsync(string name, string repo, string? label, CancellationToken cancellationToken)
    {
        var found = await store.UpdateAsync(b =>
        {
            if (b.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                return FilesOf(same.Id).Settings is { Target: null, Stopped: false } ? same.Id : (Guid?)null;
            }

            return b.AddGroup(name, null, new Actor(ActorKind.Connector, "GitHub"), time.GetUtcNow()).Id;
        }, cancellationToken).ConfigureAwait(false);
        if (found is not { } group)
        {
            return false;
        }

        FilesOf(group).Update(s => s with { Target = repo, TargetName = name, GroupId = group, Label = label, Direction = ConnectorDirection.Both, Enabled = true });
        return true;
    }

    /// <summary>A group's place: <c>owner/name</c> (the whole repository) or <c>owner/name#group:Label</c>.</summary>
    private static string Key(string repo, string? label) => label is null ? repo : $"{repo}#{label}";

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

    private async Task ConnectLockedAsync(string token, Guid groupId, string? repo, ConnectorDirection direction, bool mayClaim, CancellationToken cancellationToken)
    {
        var name = await store.ReadAsync(b => b.Groups.FirstOrDefault(g => g.Id == groupId)?.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("There is no such group.");
        var target = repo is null
            ? await MakeRepoAsync(token, name, cancellationToken).ConfigureAwait(false)
            : await CheckRepoAsync(token, GitHubRemote.ValidRepo(repo), cancellationToken).ConfigureAwait(false);
        var files = FilesOf(groupId);
        var before = files.Settings;
        var label = string.Equals(before.Target, target, StringComparison.OrdinalIgnoreCase) && before.Label is { } kept
            ? kept
            : await LabelLockedAsync(token, groupId, name, target, mayClaim, cancellationToken).ConfigureAwait(false);
        files.Update(s =>
        {
            if (!string.Equals(s.Target, target, StringComparison.OrdinalIgnoreCase) || !string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase))
            {
                files.SaveLinks([]); // another repository or group label: its links start over (issues carry task ids: nothing is copied)
            }

            return s with { Target = target, TargetName = name, GroupId = groupId, Label = label, Direction = direction, Enabled = true, Stopped = false };
        });
    }

    /// <summary>
    /// The label a group gets in <paramref name="repo"/>: <c>group:&lt;name&gt;</c>, with a number when another group
    /// here has that one there. With <paramref name="mayClaim"/>, a group that's the first in a repository without any
    /// group labels (one made before them, or one picked with its issues) takes the issues there. The label is made in
    /// the repository, so other computers find the group.
    /// </summary>
    private async Task<string> LabelLockedAsync(string token, Guid groupId, string name, string repo, bool mayClaim, CancellationToken cancellationToken)
    {
        var groups = await store.ReadAsync(b => b.Groups.Select(g => g.Id).Where(id => id != groupId).ToList(), cancellationToken).ConfigureAwait(false);
        var others = groups.Select(id => FilesOf(id).Settings).Where(s => string.Equals(s.Target, repo, StringComparison.OrdinalIgnoreCase)).ToList();
        var taken = others.Select(s => s.Label ?? GitHubRemote.GroupLabel(s.TargetName ?? string.Empty)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 1;
        while (taken.Contains(GitHubRemote.GroupLabel(name, n)))
        {
            n++;
        }

        var label = GitHubRemote.GroupLabel(name, n);
        var remote = Remote(token, repo, label);
        if (mayClaim && others.Count == 0 && !(await remote.LabelNamesAsync(cancellationToken).ConfigureAwait(false)).Any(GitHubRemote.IsGroupLabel))
        {
            await remote.ClaimUnlabelledAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await remote.MakeLabelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // Made with its first issue instead.
        }

        return label;
    }

    private GitHubRemote Remote(string token, string repo, string? label) => new(http.CreateClient(HttpClientName), _ => Task.FromResult(token), repo, label);

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

    /// <summary>The topic, so the repository is found from other computers (its own topics stay; a repository that can't be tagged still works).</summary>
    private async Task TagAsync(string token, string repo, CancellationToken cancellationToken)
    {
        try
        {
            var topics = (await SendAsync(token, HttpMethod.Get, $"repos/{repo}", null, cancellationToken).ConfigureAwait(false))?["topics"] as JsonArray ?? [];
            var names = topics.Select(t => t?.GetValue<string>()).OfType<string>().ToList();
            if (!names.Contains(Topic))
            {
                await SendAsync(token, HttpMethod.Put, $"repos/{repo}/topics", new JsonObject { ["names"] = new JsonArray([.. names.Append(Topic).Select(t => (JsonNode)t)]) }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException)
        {
            // Not tagged: it's still this group's repository here.
        }
    }

    /// <summary>How a repository made for a group is recognized again.</summary>
    public static string Description(string group) => $"One Thing: {group}";

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
            if (current.Label is null)
            {
                // Connected before groups had labels: it gets its label now, and keeps the issues it had.
                var name = await store.ReadAsync(b => b.Groups.FirstOrDefault(g => g.Id == groupId)?.Name, cancellationToken).ConfigureAwait(false) ?? current.TargetName ?? string.Empty;
                var label = await LabelLockedAsync(token, groupId, current.TargetName ?? name, repo, mayClaim: true, cancellationToken).ConfigureAwait(false);
                current = files.Update(s => s with { Label = label });
            }

            var remote = Remote(token, repo, current.Label);
            var runner = new ConnectorRunner($"github:{repo}", "GitHub", store, time, options.TimeZone);
            var run = await runner.RunAsync(remote, new ConnectorTarget(groupId, current.Direction, AdoptMarked: MainPlace, GroupOnly: true, TakeMoved: true), files.Links, cancellationToken).ConfigureAwait(false);
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
        request.Headers.UserAgent.ParseAdd("OneThing");
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
public sealed partial class GitHubLoop(GitHubIssues github, IBoardStore store, OneThingServerOptions options, ILogger<GitHubLoop> logger) : BackgroundService
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
