using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Connectors;

/// <summary>
/// A GitHub repository's issues as a task list (one repo per group). An issue is a task: its title, its body (the
/// details), open or closed (done), and labels for everything else: plain labels are tags; prefixed ones carry the
/// rest (<c>due:2026-10-20</c>, <c>priority:high</c>, <c>snooze:2026-10-20T14:30Z</c>, <c>remind:…</c>,
/// <c>stage:doing</c>, <c>label:Deep work</c>, <c>icon:🔥</c>, <c>color:#ff8800</c>, <c>plan:…/45</c>,
/// <c>repeat:every weekday</c>). The task's id is a hidden comment at the end of the body. Nothing is ever deleted: a
/// task deleted here closes its issue as "not planned".
/// </summary>
public sealed partial class GitHubRemote(HttpClient http, Func<CancellationToken, Task<string>> token, string repo) : IConnectorRemote
{
    /// <summary>GitHub's longest label name.</summary>
    public const int MaxLabel = 50;

    private const int PageSize = 100;

    public const string Due = "due:";
    public const string PriorityPrefix = "priority:";

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
    };

    public ConnectorFields Fields => ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Tags | ConnectorFields.Notes | ConnectorFields.Extras;

    public string Repo { get; } = ValidRepo(repo);

    /// <summary>"owner/name", or an exception saying what one looks like.</summary>
    public static string ValidRepo(string repo) =>
        repo is not null && RepoPattern().IsMatch(repo.Trim()) ? repo.Trim() : throw new ArgumentException("A repository looks like owner/name.", nameof(repo));

    /// <summary>What a label can't hold is dropped here too (a tag or an extra longer than a label), so it isn't taken as a change.</summary>
    public SyncedFields Normalize(SyncedFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return Read(fields.Title, fields.Done, Body(fields.Notes, null), LabelsOf(fields), out _);
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<RemoteItem>();
        for (var page = 1; ; page++)
        {
            var batch = await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues?state=all&per_page={PageSize}&page={page}", null, cancellationToken).ConfigureAwait(false) as JsonArray ?? [];
            items.AddRange(batch.OfType<JsonObject>().Where(i => i["pull_request"] is null).Select(ItemOf));
            if (batch.Count < PageSize)
            {
                return items;
            }
        }
    }

    public async Task<RemoteItem?> FindAsync(string id, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues/{Number(id)}", null, cancellationToken, missingIsNull: true).ConfigureAwait(false) is JsonObject issue && issue["pull_request"] is null
            ? ItemOf(issue)
            : null;

    public async Task<string> CreateAsync(Guid localId, SyncedFields fields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var created = await SendAsync(HttpMethod.Post, $"repos/{Repo}/issues", new JsonObject
        {
            ["title"] = fields.Title,
            ["body"] = Body(fields.Notes, localId),
            ["labels"] = new JsonArray([.. LabelsOf(fields).Select(l => (JsonNode)l)]),
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
        if (previous is null || previous.Title != fields.Title)
        {
            change["title"] = fields.Title;
        }

        if (previous is null || !SyncedFields.Same(previous, fields, ConnectorFields.Notes))
        {
            // The body keeps its id comment (read it back: it may be someone else's issue we only just linked).
            var current = await SendAsync(HttpMethod.Get, $"repos/{Repo}/issues/{Number(id)}", null, cancellationToken).ConfigureAwait(false) as JsonObject;
            change["body"] = Body(fields.Notes, MarkerOf(current?["body"]?.GetValue<string>()));
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
            change["labels"] = new JsonArray([.. LabelsOf(fields).Select(l => (JsonNode)l)]);
        }

        if (change.Count > 0)
        {
            await SendAsync(HttpMethod.Patch, $"repos/{Repo}/issues/{Number(id)}", change, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task ArchiveAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, $"repos/{Repo}/issues/{Number(id)}", new JsonObject { ["state"] = "closed", ["state_reason"] = "not_planned" }, cancellationToken);

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

        labels.AddRange(fields.Tags.Select(t => t.Trim()).Where(t => t.Length > 0 && !IsReserved(t)));
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

    /// <summary>The task an issue is (<paramref name="marker"/>: the task id in its body, if any).</summary>
    public static SyncedFields Read(string title, bool closed, string? body, IEnumerable<string> labels, out Guid? marker)
    {
        ArgumentNullException.ThrowIfNull(labels);
        (var notes, marker) = Split(body);
        DateOnly? due = null;
        var priority = Priority.Normal;
        var tags = new List<string>();
        var extras = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            if (label.StartsWith(Due, StringComparison.OrdinalIgnoreCase)
                && DateOnly.TryParseExact(label[Due.Length..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                due = day;
            }
            else if (label.StartsWith(PriorityPrefix, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<Priority>(label[PriorityPrefix.Length..], ignoreCase: true, out var p) && Enum.IsDefined(p))
            {
                priority = p;
            }
            else if (ExtraPrefixes.FirstOrDefault(e => label.StartsWith(e.Value, StringComparison.OrdinalIgnoreCase)) is { Key: { } key, Value: { } prefix })
            {
                if (!extras.TryGetValue(key, out var list))
                {
                    extras[key] = list = [];
                }

                list.Add(label[prefix.Length..].Trim());
            }
            else
            {
                tags.Add(label);
            }
        }

        string Join(string key, List<string> values) => key switch
        {
            ExtraKeys.Remind => string.Join(',', values.Select(v => ConnectorRunner.ParseMoment(v) is { } at ? ConnectorRunner.Moment(at) : null).OfType<string>().Distinct().Order(StringComparer.Ordinal)),
            ExtraKeys.Labels => string.Join('\n', values.Where(v => v.Length > 0).Order(StringComparer.OrdinalIgnoreCase)),
            _ => values[0],
        };

        return new SyncedFields(title, closed, due, priority, tags, notes)
        {
            Extras = extras.Select(e => (e.Key, Value: Join(e.Key, e.Value))).Where(e => e.Value.Length > 0).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>A plain label that would read as one of ours isn't a tag (a tag "due:x" can't round-trip).</summary>
    private static bool IsReserved(string tag) =>
        tag.StartsWith(Due, StringComparison.OrdinalIgnoreCase) || tag.StartsWith(PriorityPrefix, StringComparison.OrdinalIgnoreCase)
        || ExtraPrefixes.Values.Any(p => tag.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>The body: the details, then the task id (hidden when the issue is shown).</summary>
    public static string Body(string? notes, Guid? marker)
    {
        var text = (notes ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        return marker is { } id ? (text.Length > 0 ? text + "\n\n" : string.Empty) + $"<!-- todotracker:id={id:D} -->" : text;
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

    private static RemoteItem ItemOf(JsonObject issue)
    {
        var labels = (issue["labels"] as JsonArray ?? []).Select(l => l is JsonObject o ? o["name"]?.GetValue<string>() : l?.GetValue<string>()).OfType<string>();
        var fields = Read(issue["title"]?.GetValue<string>() ?? string.Empty, issue["state"]?.GetValue<string>() == "closed", issue["body"]?.GetValue<string>(), labels, out var marker);
        return new RemoteItem(issue["number"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture), fields, marker);
    }

    private static string Number(string id) =>
        int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Not an issue number.", nameof(id));

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("TodoTracker");
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

    [GeneratedRegex(@"\n*<!--\s*todotracker:id=([0-9a-fA-F-]{36})\s*-->\s*")]
    private static partial Regex MarkerPattern();
}
