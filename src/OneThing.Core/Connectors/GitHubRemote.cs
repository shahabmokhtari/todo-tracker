using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OneThing.Core.Connectors;

/// <summary>
/// A GitHub repository's issues as a task list (a repository per group, or several groups in one, each with its label). An issue is a task: its title, its body (the
/// details), open or closed (done), and labels for everything else: plain labels are tags; prefixed ones carry the
/// rest (<c>due:2026-10-20</c>, <c>priority:high</c>, <c>snooze:2026-10-20T14:30Z</c>, <c>remind:…</c>,
/// <c>stage:doing</c>, <c>label:Deep work</c>, <c>icon:🔥</c>, <c>color:#ff8800</c>, <c>plan:…/45</c>,
/// <c>repeat:every weekday</c>). The task's id is a hidden comment at the end of the body. Nothing is ever deleted: a
/// task deleted here closes its issue as "not planned".
/// <para>
/// <paramref name="label"/>: the group's label (<c>group:Work</c>), so several groups can share one repository: every
/// issue the group makes gets it, and the group sees only the issues that have it. <c>group:</c> labels are never tags.
/// </para>
/// </summary>
public sealed partial class GitHubRemote(HttpClient http, Func<CancellationToken, Task<string>> token, string repo, string? label = null) : IConnectorRemote
{
    /// <summary>GitHub's longest label name.</summary>
    public const int MaxLabel = 50;

    /// <summary>GitHub's longest issue title.</summary>
    public const int MaxTitle = 256;

    /// <summary>The longest details a task here has (a longer body comes in shortened).</summary>
    public const int MaxNotes = 10_000;

    private const int PageSize = 100;

    public const string Due = "due:";
    public const string PriorityPrefix = "priority:";

    /// <summary>The prefix of a group's label (<c>group:Work</c>): which group an issue in a shared repository is in.</summary>
    public const string GroupPrefix = "group:";

    /// <summary>The label prefix of each extra (see <see cref="ExtraKeys"/>); reminders get one label each.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExtraPrefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ExtraKeys.Snooze] = "snooze:",
        [ExtraKeys.Remind] = "remind:",
        [ExtraKeys.Stage] = "stage:",
        [ExtraKeys.Labels] = "label:",
        [ExtraKeys.Icon] = "icon:",
        [ExtraKeys.Color] = "color:",
        [ExtraKeys.Plan] = "plan:",
        [ExtraKeys.Repeat] = "repeat:",
        [ExtraKeys.Energy] = "energy:",
    };

    public ConnectorFields Fields => ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Tags | ConnectorFields.Notes | ConnectorFields.Extras;

    public string Repo { get; } = ValidRepo(repo);

    /// <summary>The group's label (null: every issue in the repository is the group's).</summary>
    public string? Label { get; } = string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    /// <summary>
    /// The label of a group named <paramref name="group"/> (<c>group:Work</c>; <paramref name="n"/> above 1 adds a
    /// number, for two groups whose names end up the same): no commas (GitHub lists labels with them), and no longer
    /// than a label can be.
    /// </summary>
    public static string GroupLabel(string group, int n = 1)
    {
        var name = string.Join(' ', (group ?? string.Empty).Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0)
        {
            name = "tasks";
        }

        var suffix = n > 1 ? " " + n.ToString(CultureInfo.InvariantCulture) : string.Empty;
        var room = MaxLabel - GroupPrefix.Length - suffix.Length;
        return GroupPrefix + (name.Length <= room ? name : name[..room].TrimEnd()) + suffix;
    }

    /// <summary>Whether <paramref name="name"/> says which group an issue is in.</summary>
    public static bool IsGroupLabel(string name) => name is not null && name.StartsWith(GroupPrefix, StringComparison.OrdinalIgnoreCase);

    private string Scope => Label is null ? string.Empty : "&labels=" + Uri.EscapeDataString(Label);

    private bool InScope(JsonObject issue) => Label is null || LabelNames(issue).Contains(Label, StringComparer.OrdinalIgnoreCase);

    /// <summary>"owner/name", or an exception saying what one looks like.</summary>
    public static string ValidRepo(string repo) =>
        repo is not null && RepoPattern().IsMatch(repo.Trim()) ? repo.Trim() : throw new ArgumentException("A repository looks like owner/name.", nameof(repo));

    /// <summary>What a label can't hold is dropped here too (a tag or an extra longer than a label), so it isn't taken as a change.</summary>
    public SyncedFields Normalize(SyncedFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Read(TitleOf(fields.Title), fields.Done, Body(fields.Notes, null), LabelsOf(fields), out _);
    }

    private static string TitleOf(string title) => title.Length <= MaxTitle ? title : title[..MaxTitle];

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<RemoteItem>();
        for (var page = 1; ; page++)
        {
            var batch = await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues?state=all&per_page={PageSize}&page={page}{Scope}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            var issues = batch.OfType<JsonObject>().Where(i => i["pull_request"] is null && InScope(i)).ToList();
            await KnowViewerAsync(issues, cancellationToken).ConfigureAwait(false);
            items.AddRange(issues.Select(ItemOf));
            if (batch.Count < PageSize)
            {
                return items;
            }
        }
    }

    /// <summary>The issue, or null when it's gone, or in another group now (its group label changed on GitHub).</summary>
    public async Task<RemoteItem?> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues/{Number(id)}", null, cancellationToken, missingIsNull: true).ConfigureAwait(false) is not JsonObject issue || issue["pull_request"] is not null || !InScope(issue))
        {
            return null;
        }

        await KnowViewerAsync([issue], cancellationToken).ConfigureAwait(false);
        return ItemOf(issue);
    }

    public async Task<string> CreateAsync(Guid localId, SyncedFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var created = await SendAsync(HttpMethod.Post, $"repos/{Repo}/issues", new JsonObject
        {
            ["title"] = TitleOf(fields.Title),
            ["body"] = Body(fields.Notes, localId),
            ["labels"] = new JsonArray([.. (Label is null ? LabelsOf(fields) : LabelsOf(fields).Prepend(Label)).Select(l => (JsonNode)l)]),
        }, cancellationToken).ConfigureAwait(false) as JsonObject ?? throw new InvalidOperationException("GitHub didn't say which issue it made.");
        var id = created["number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture);
        if (fields.Done)
        {
            await SendAsync(HttpMethod.Patch, $"repos/{Repo}/issues/{id}", new JsonObject { ["state"] = "closed", ["state_reason"] = "completed" }, cancellationToken).ConfigureAwait(false);
        }

        return id;
    }

    public async Task UpdateAsync(string id, SyncedFields fields, SyncedFields? previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var change = new JsonObject();
        if (previous is null || previous.Title != TitleOf(fields.Title))
        {
            change["title"] = TitleOf(fields.Title);
        }

        // Read back first: the body keeps its id comment, and labels this app doesn't own stay on the issue.
        JsonObject? current = null;
        async Task<JsonObject?> Current() => current ??= await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues/{Number(id)}", null, cancellationToken).ConfigureAwait(false) as JsonObject;
        if (previous is null || !SyncedFields.Same(previous, fields, ConnectorFields.Notes))
        {
            change["body"] = Body(fields.Notes, MarkerOf((await Current().ConfigureAwait(false))?["body"]?.GetValue<string>()));
        }

        if (previous is null || previous.Done != fields.Done)
        {
            change["state"] = fields.Done ? "closed" : "open";
            if (fields.Done)
            {
                change["state_reason"] = "completed";
            }
        }

        var labelFields = ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Tags | ConnectorFields.Extras;
        if (previous is null || !SyncedFields.Same(Normalize(previous), Normalize(fields), labelFields))
        {
            var foreign = LabelNames(await Current().ConfigureAwait(false)).Where(l => Kind(l) == LabelKind.Foreign).ToList();
            // The group's label stays (put back if it went), unless the issue is in another group now.
            if (Label is not null && !foreign.Any(IsGroupLabel))
            {
                foreign.Add(Label);
            }

            change["labels"] = new JsonArray([.. foreign.Concat(LabelsOf(fields)).Distinct(StringComparer.OrdinalIgnoreCase).Select(l => (JsonNode)l)]);
        }

        if (change.Count > 0)
        {
            await SendAsync(HttpMethod.Patch, $"repos/{Repo}/issues/{Number(id)}", change, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ArchiveAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"repos/{Repo}/issues/{Number(id)}", new JsonObject { ["state"] = "closed", ["state_reason"] = "not_planned" }, cancellationToken);

    /// <summary>
    /// Gives the group's label to every issue in the repository that isn't in a group yet (made before groups had
    /// labels, or on GitHub without one): a group that had the repository to itself keeps its issues.
    /// </summary>
    public async Task ClaimUnlabelledAsync(CancellationToken cancellationToken)
    {
        if (Label is null)
        {
            return;
        }

        var unlabelled = new List<string>();
        for (var page = 1; ; page++)
        {
            var batch = await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues?state=all&per_page={PageSize}&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            unlabelled.AddRange(batch.OfType<JsonObject>().Where(i => i["pull_request"] is null && !LabelNames(i).Any(IsGroupLabel)).Select(i => i["number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture)));
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        foreach (var number in unlabelled)
        {
            await SendAsync(HttpMethod.Post, $"repos/{Repo}/issues/{number}/labels", new JsonObject { ["labels"] = new JsonArray(Label) }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Makes the group's label in the repository (so other computers find the group before it has an issue).</summary>
    public async Task MakeLabelAsync(CancellationToken cancellationToken)
    {
        if (Label is null || (await LabelNamesAsync(cancellationToken).ConfigureAwait(false)).Contains(Label, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        await SendAsync(HttpMethod.Post, $"repos/{Repo}/labels", new JsonObject { ["name"] = Label, ["color"] = "ededed", ["description"] = "A group in One Thing" }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every label the repository has.</summary>
    public async Task<IReadOnlyList<string>> LabelNamesAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        for (var page = 1; ; page++)
        {
            var labels = await SendAsync(HttpMethod.Get, $"repos/{Repo}/labels?per_page={PageSize}&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            names.AddRange(labels.OfType<JsonObject>().Select(l => l["name"]?.GetValue<string>()).OfType<string>());
            if (labels.Count < PageSize)
            {
                return names;
            }
        }
    }

    /// <summary>
    /// Deletes the repository's time labels no issue has any more (<c>snooze:</c>, <c>remind:</c>, <c>plan:</c>,
    /// <c>due:</c>): each moment makes a label, and the label list would only grow. Every issue counts, other groups'
    /// too (they share the repository); group labels are never deleted.
    /// </summary>
    public async Task PruneLabelsAsync(CancellationToken cancellationToken)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; ; page++)
        {
            var batch = await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues?state=all&per_page={PageSize}&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            used.UnionWith(batch.OfType<JsonObject>().SelectMany(LabelNames));
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        string[] timed = [Due, ExtraPrefixes[ExtraKeys.Snooze], ExtraPrefixes[ExtraKeys.Remind], ExtraPrefixes[ExtraKeys.Plan]];
        for (var page = 1; ; page++)
        {
            var labels = await SendAsync(HttpMethod.Get, $"repos/{Repo}/labels?per_page={PageSize}&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            foreach (var name in labels.OfType<JsonObject>().Select(l => l["name"]?.GetValue<string>()).OfType<string>())
            {
                if (!used.Contains(name) && !IsGroupLabel(name) && timed.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    await SendAsync(HttpMethod.Delete, $"repos/{Repo}/labels/{Uri.EscapeDataString(name)}", null, cancellationToken, missingIsNull: true).ConfigureAwait(false);
                }
            }

            if (labels.Count < PageSize)
            {
                return;
            }
        }
    }

    public async Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken)
    {
        var issue = await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues/{Number(id)}", null, cancellationToken).ConfigureAwait(false) as JsonObject;
        var (notes, _) = Split(issue?["body"]?.GetValue<string>());
        await SendAsync(HttpMethod.Patch, $"repos/{Repo}/issues/{Number(id)}", new JsonObject { ["body"] = Body(notes, localId) }, cancellationToken).ConfigureAwait(false);
    }

    // ---- The mapping (static: tested on its own) -------------------------------------------------

    /// <summary>The labels an issue gets for <paramref name="fields"/> (none longer than GitHub allows).</summary>
    public static IReadOnlyList<string> LabelsOf(SyncedFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var labels = new List<string>();
        if (fields.Due is { } due)
        {
            labels.Add(Due + due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }

        if (fields.Priority != Priority.Normal)
        {
            labels.Add(PriorityPrefix + fields.Priority.ToString().ToLowerInvariant());
        }

        // Only a tag that reads back as a tag goes out as one ("due:x" would read as a due date).
        labels.AddRange(fields.Tags.Select(t => t.Trim()).Where(t => t.Length > 0 && Parse(t) is (null, _)));
        foreach (var (key, prefix) in ExtraPrefixes)
        {
            var value = fields.Extra(key);
            if (value.Length == 0)
            {
                continue;
            }

            // Several reminders and labels: one label each.
            var values = key switch
            {
                ExtraKeys.Remind => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                ExtraKeys.Labels => value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                _ => [value],
            };
            labels.AddRange(values.Select(v => prefix + v));
        }

        return [.. labels.Where(l => l.Length <= MaxLabel).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>What a label is to this app.</summary>
    private enum LabelKind
    {
        /// <summary>A tag here.</summary>
        Tag,

        /// <summary>One of ours (due date, priority, an extra) with a value this app takes.</summary>
        Ours,

        /// <summary>Not for this app (a label a tag can't be, "priority:p1", "stage:review"): left on the issue as it is.</summary>
        Foreign,
    }

    private static LabelKind Kind(string label) => Parse(label) is { } parsed ? (parsed.Key is null ? LabelKind.Tag : LabelKind.Ours) : LabelKind.Foreign;

    /// <summary>A label read: (null, tag), (key, value) for one of ours (<see cref="Due"/> and "priority" too), or null
    /// when it's not for this app. Values come back the way this app writes them (lowercase colors, rules as it says them).</summary>
    private static (string? Key, string Value)? Parse(string label)
    {
        if (IsGroupLabel(label))
        {
            return null; // which group the issue is in, never a tag
        }

        if (label.StartsWith(Due, StringComparison.OrdinalIgnoreCase))
        {
            return DateOnly.TryParseExact(label[Due.Length..].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? (Due, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null;
        }

        if (label.StartsWith(PriorityPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Enum.TryParse<Priority>(label[PriorityPrefix.Length..].Trim(), ignoreCase: true, out var p) && Enum.IsDefined(p) && !int.TryParse(label[PriorityPrefix.Length..], out _)
                ? (PriorityPrefix, p.ToString()) : null;
        }

        foreach (var (key, prefix) in ExtraPrefixes)
        {
            if (!label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = label[prefix.Length..].Trim();
            string? clean = key switch
            {
                ExtraKeys.Snooze or ExtraKeys.Remind => ConnectorRunner.ParseMoment(value) is { } at ? ConnectorRunner.Moment(at) : null,
                ExtraKeys.Plan => value.Split('/') is [var start, var minutes] && ConnectorRunner.ParseMoment(start) is { } from
                    && int.TryParse(minutes, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is >= TaskBoard.MinPlanMinutes and <= TaskBoard.MaxPlanMinutes
                    ? $"{ConnectorRunner.Moment(from)}/{m}" : null,
                ExtraKeys.Stage => value.ToLowerInvariant() is "inbox" or "next" or "doing" ? value.ToLowerInvariant() : null,
                ExtraKeys.Labels => ValidLabel(value) ? value : null,
                ExtraKeys.Icon => TaskBoard.Lenient(TaskBoard.CleanIcon, value),
                ExtraKeys.Color => TaskBoard.Lenient(TaskBoard.CleanColor, value),
                ExtraKeys.Repeat => Recurrence.TryParse(value, out var rule, out _) ? rule.ToString() : null,
                ExtraKeys.Energy => value is "1" or "2" or "3" ? value : null,
                _ => null,
            };
            return clean is null ? null : (key, clean);
        }

        var tag = TaskBoard.CleanTag(label);
        return TaskBoard.IsValidTag(tag) ? (null, tag) : null;
    }

    private static bool ValidLabel(string name)
    {
        try
        {
            return TaskBoard.ValidateLabelNames([name]).Count == 1;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The task an issue is (<paramref name="marker"/>: the task id in its body, if any). Labels that aren't for
    /// this app are left out (and stay on the issue). Of two labels for one value, the later time (snooze, plan) or the
    /// last one wins.</summary>
    public static SyncedFields Read(string title, bool closed, string? body, IEnumerable<string> labels, out Guid? marker)
    {
        ArgumentNullException.ThrowIfNull(labels);
        (var notes, marker) = Split(body);
        if (notes is { Length: > MaxNotes })
        {
            notes = notes[..MaxNotes];
        }

        DateOnly? due = null;
        var priority = Priority.Normal;
        var tags = new List<string>();
        var extras = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            switch (Parse(label))
            {
                case (null, var tag):
                    tags.Add(tag);
                    break;
                case (Due, var day):
                    due = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    break;
                case (PriorityPrefix, var p):
                    priority = Enum.Parse<Priority>(p);
                    break;
                case ({ } key, var value):
                    if (!extras.TryGetValue(key, out var list))
                    {
                        extras[key] = list = [];
                    }

                    list.Add(value);
                    break;
            }
        }

        string Join(string key, List<string> values) => key switch
        {
            ExtraKeys.Remind => string.Join(',', values.Distinct().Order(StringComparer.Ordinal)),
            ExtraKeys.Labels => string.Join('\n', values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
            ExtraKeys.Snooze or ExtraKeys.Plan => values.Max(StringComparer.Ordinal)!,
            _ => values[^1],
        };

        return new SyncedFields(title, closed, due, priority, tags, notes)
        {
            Extras = extras.ToDictionary(e => e.Key, e => Join(e.Key, e.Value), StringComparer.Ordinal),
        };
    }

    /// <summary>The body: the details, then the task id (hidden when the issue is shown).</summary>
    public static string Body(string? notes, Guid? marker)
    {
        var text = (notes ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return marker is { } id ? (text.Length > 0 ? text + "\n\n" : string.Empty) + $"<!-- onething:id={id:D} -->" : text;
    }

    private static (string? Notes, Guid? Marker) Split(string? body)
    {
        var text = (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var match = MarkerPattern().Match(text);
        var marker = match.Success && Guid.TryParse(match.Groups[1].Value, out var id) ? id : (Guid?)null;
        var notes = MarkerPattern().Replace(text, string.Empty).TrimEnd();
        return (notes.Length == 0 ? null : notes, marker);
    }

    private static Guid? MarkerOf(string? body) => Split(body).Marker;

    private static IEnumerable<string> LabelNames(JsonObject? issue) =>
        (issue?["labels"] as JsonArray ?? []).Select(l => l is JsonObject o ? o["name"]?.GetValue<string>() : l?.GetValue<string>()).OfType<string>();

    private RemoteItem ItemOf(JsonObject issue)
    {
        var closed = issue["state"]?.GetValue<string>() == "closed";
        var fields = Read(issue["title"]?.GetValue<string>() ?? string.Empty, closed, issue["body"]?.GetValue<string>(), LabelNames(issue), out var marker);
        return new RemoteItem(issue["number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture), fields, marker, Dropped: closed && issue["state_reason"]?.GetValue<string>() == "not_planned")
        {
            // Anyone can open an issue in a public repository, marker and all: only the token's own issues are believed.
            MarkerTrusted = marker is null || AuthorOf(issue) is not { } author || _viewer is null || string.Equals(author, _viewer, StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string? AuthorOf(JsonObject issue) => issue["user"]?["login"]?.GetValue<string>();

    // The signed-in account (null until needed, or when GitHub doesn't say).
    private string? _viewer;
    private bool _viewerAsked;

    /// <summary>Looks up who the token belongs to, once, when an issue carries a marker and says who opened it.</summary>
    private async Task KnowViewerAsync(IEnumerable<JsonObject> issues, CancellationToken cancellationToken)
    {
        if (_viewerAsked || !issues.Any(i => AuthorOf(i) is not null && MarkerOf(i["body"]?.GetValue<string>()) is not null))
        {
            return;
        }

        _viewerAsked = true;
        _viewer = (await SendAsync(HttpMethod.Get, "user", null, cancellationToken).ConfigureAwait(false))?["login"]?.GetValue<string>();
    }

    private static string Number(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Not an issue number.", nameof(id));

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("OneThing");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (missingIsNull && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                message = JsonNode.Parse(text)?["message"]?.GetValue<string>();
            }
            catch (System.Text.Json.JsonException)
            {
                // not JSON: the status says it
            }

            throw new HttpRequestException($"GitHub: {message ?? response.ReasonPhrase} ({(int)response.StatusCode})", null, response.StatusCode);
        }

        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"\n*<!--\s*onething:id=([0-9a-fA-F-]{36})\s*-->\s*")]
    private static partial Regex MarkerPattern();
}
