using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Connectors;

public sealed record NotionDatabase(string Id, string Title);

/// <summary>
/// The Notion API with an internal integration's token: JSON in and out, Notion's own error messages, its rate limit
/// (about three requests a second) and its "slow down" answers (429, retried after the time it asks for).
/// </summary>
public sealed class NotionApi(HttpClient http, string token) : IDisposable
{
    public const string Version = "2022-06-28";
    private const int MaxRetries = 4;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _last = DateTime.MinValue;

    public void Dispose() => _gate.Dispose();

    /// <summary>The time between requests (Notion allows about three a second).</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMilliseconds(350);

    /// <summary>Databases the integration was given access to (in Notion: ••• › Connections › the integration).</summary>
    public async Task<IReadOnlyList<NotionDatabase>> DatabasesAsync(CancellationToken cancellationToken)
    {
        var list = new List<NotionDatabase>();
        string? cursor = null;
        do
        {
            var body = new JsonObject { ["filter"] = new JsonObject { ["property"] = "object", ["value"] = "database" }, ["page_size"] = 100 };
            if (cursor is not null)
            {
                body["start_cursor"] = cursor;
            }

            var page = await SendAsync(HttpMethod.Post, "v1/search", body, cancellationToken).ConfigureAwait(false);
            foreach (var db in page!["results"]!.AsArray().OfType<JsonObject>())
            {
                list.Add(new NotionDatabase(db["id"]!.GetValue<string>(), PlainText(db["title"]) is { Length: > 0 } t ? t : "Untitled"));
            }

            cursor = page["has_more"]?.GetValue<bool>() == true ? page["next_cursor"]?.GetValue<string>() : null;
        }
        while (cursor is not null);
        return list;
    }

    /// <summary>A request; null for 404 when <paramref name="missingIsNull"/>.</summary>
    public async Task<JsonObject?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            await PaceAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Notion-Version", Version);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetries)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1 << attempt);
                await Task.Delay(wait < TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.NotFound && missingIsNull)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                var message = TryParse(text)?["message"]?.GetValue<string>() ?? response.ReasonPhrase;
                throw new HttpRequestException($"Notion: {message}", null, response.StatusCode);
            }

            return TryParse(text) ?? [];
        }
    }

    internal static string PlainText(JsonNode? richText) =>
        string.Concat(richText?.AsArray().Select(r => r?["plain_text"]?.GetValue<string>() ?? r?["text"]?["content"]?.GetValue<string>() ?? string.Empty) ?? []);

    private static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private async Task PaceAsync(CancellationToken cancellationToken)
    {
        if (MinInterval <= TimeSpan.Zero)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _last + MinInterval - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _last = DateTime.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// One Notion database as an outside list. Its columns are matched by type and name: the title; a checkbox named like
/// Done (else the first checkbox), or else a status column (done = its "Complete" group); a date named like Due (else
/// the first date); a select named Priority; a multi-select named Tags. A "Todo Tracker ID" text column is added to
/// hold each page's task id (so nothing is ever copied twice).
/// </summary>
public sealed partial class NotionRemote : IConnectorRemote, IDisposable
{
    public const string MarkerColumn = "Todo Tracker ID";

    private readonly NotionApi _api;
    private readonly string _database;
    private readonly Columns _columns;

    private NotionRemote(NotionApi api, string database, Columns columns)
    {
        _api = api;
        _database = database;
        _columns = columns;
        Fields = ConnectorFields.Title
            | (columns.Done is not null ? ConnectorFields.Done : 0)
            | (columns.Due is not null ? ConnectorFields.Due : 0)
            | (columns.Priority is not null ? ConnectorFields.Priority : 0)
            | (columns.Tags is not null ? ConnectorFields.Tags : 0);
    }

    public ConnectorFields Fields { get; }

    /// <summary>The connection it was made with goes with it.</summary>
    public void Dispose() => _api.Dispose();

    /// <summary>Which columns were found, said plainly (for the settings panel).</summary>
    public string Describe() =>
        $"Title: {_columns.Title}; done: {_columns.Done ?? "–"}; due: {_columns.Due ?? "–"}; priority: {_columns.Priority ?? "–"}; tags: {_columns.Tags ?? "–"}";

    public static async Task<NotionRemote> ConnectAsync(NotionApi api, string database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        var db = await api.SendAsync(HttpMethod.Get, $"v1/databases/{Uri.EscapeDataString(database)}", null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Notion database not found.");
        var properties = db["properties"]!.AsObject();
        if (!properties.Any(p => p.Key == MarkerColumn && Type(p.Value) == "rich_text"))
        {
            await api.SendAsync(
                HttpMethod.Patch,
                $"v1/databases/{Uri.EscapeDataString(database)}",
                new JsonObject { ["properties"] = new JsonObject { [MarkerColumn] = new JsonObject { ["rich_text"] = new JsonObject() } } },
                cancellationToken).ConfigureAwait(false);
        }

        return new NotionRemote(api, database, Columns.Read(properties));
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<RemoteItem>();
        string? cursor = null;
        do
        {
            var body = new JsonObject { ["page_size"] = 100 };
            if (cursor is not null)
            {
                body["start_cursor"] = cursor;
            }

            var page = await _api.SendAsync(HttpMethod.Post, $"v1/databases/{Uri.EscapeDataString(_database)}/query", body, cancellationToken).ConfigureAwait(false);
            items.AddRange(page!["results"]!.AsArray().OfType<JsonObject>().Where(IsLive).Select(Read));
            cursor = page["has_more"]?.GetValue<bool>() == true ? page["next_cursor"]?.GetValue<string>() : null;
        }
        while (cursor is not null);
        return items;
    }

    public async Task<RemoteItem?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var page = await _api.SendAsync(HttpMethod.Get, $"v1/pages/{Uri.EscapeDataString(id)}", null, cancellationToken, missingIsNull: true).ConfigureAwait(false);
        return page is not null && IsLive(page) ? Read(page) : null;
    }

    public async Task<string> CreateAsync(Guid localId, SyncedFields fields, CancellationToken cancellationToken)
    {
        var properties = Write(fields);
        properties[MarkerColumn] = Marker(localId);
        var page = await _api.SendAsync(HttpMethod.Post, "v1/pages", new JsonObject { ["parent"] = new JsonObject { ["database_id"] = _database }, ["properties"] = properties }, cancellationToken).ConfigureAwait(false);
        return page!["id"]!.GetValue<string>();
    }

    public Task UpdateAsync(string id, SyncedFields fields, CancellationToken cancellationToken) =>
        _api.SendAsync(HttpMethod.Patch, $"v1/pages/{Uri.EscapeDataString(id)}", new JsonObject { ["properties"] = Write(fields) }, cancellationToken);

    /// <summary>Archived pages are in Notion's trash (restorable for 30 days).</summary>
    public Task ArchiveAsync(string id, CancellationToken cancellationToken) =>
        _api.SendAsync(HttpMethod.Patch, $"v1/pages/{Uri.EscapeDataString(id)}", new JsonObject { ["archived"] = true }, cancellationToken);

    public Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken) =>
        _api.SendAsync(HttpMethod.Patch, $"v1/pages/{Uri.EscapeDataString(id)}", new JsonObject { ["properties"] = new JsonObject { [MarkerColumn] = Marker(localId) } }, cancellationToken);

    private static string? Type(JsonNode? property) => property?["type"]?.GetValue<string>();

    private static bool IsLive(JsonObject page) => page["archived"]?.GetValue<bool>() != true && page["in_trash"]?.GetValue<bool>() != true;

    private static JsonObject Marker(Guid id) => new() { ["rich_text"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = id.ToString() } }) };

    private static Priority PriorityOf(string? name)
    {
        var n = (name ?? string.Empty).ToLowerInvariant();
        if (n.Contains("crit", StringComparison.Ordinal) || n.Contains("urgent", StringComparison.Ordinal) || n.Contains("p0", StringComparison.Ordinal))
        {
            return Priority.Critical;
        }

        if (n.Contains("high", StringComparison.Ordinal) || n.Contains("p1", StringComparison.Ordinal))
        {
            return Priority.High;
        }

        return n.Contains("low", StringComparison.Ordinal) ? Priority.Low : Priority.Normal;
    }

    private RemoteItem Read(JsonObject page)
    {
        var p = page["properties"]!.AsObject();
        var done = _columns.Done switch
        {
            null => false,
            _ when _columns.DoneIsStatus => p[_columns.Done]?["status"]?["name"]?.GetValue<string>() is { } s && _columns.CompleteOptions.Contains(s),
            _ => p[_columns.Done]?["checkbox"]?.GetValue<bool>() == true,
        };
        var dueText = _columns.Due is null ? null : p[_columns.Due]?["date"]?["start"]?.GetValue<string>();
        DateOnly? due = dueText is { Length: >= 10 } && DateOnly.TryParseExact(dueText[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        var priority = _columns.Priority is null ? Priority.Normal : PriorityOf(p[_columns.Priority]?["select"]?["name"]?.GetValue<string>());
        IReadOnlyList<string> tags = _columns.Tags is null ? [] : [.. p[_columns.Tags]?["multi_select"]?.AsArray().Select(t => t?["name"]?.GetValue<string>() ?? string.Empty).Where(t => t.Length > 0) ?? []];
        var marker = Guid.TryParse(NotionApi.PlainText(p[MarkerColumn]?["rich_text"]).Trim(), out var m) ? m : (Guid?)null;
        return new RemoteItem(page["id"]!.GetValue<string>(), new SyncedFields(NotionApi.PlainText(p[_columns.Title]?["title"]), done, due, priority, tags, null), marker);
    }

    private JsonObject Write(SyncedFields f)
    {
        var properties = new JsonObject
        {
            [_columns.Title] = new JsonObject { ["title"] = new JsonArray(new JsonObject { ["text"] = new JsonObject { ["content"] = f.Title } }) },
        };
        if (_columns.Done is { } done)
        {
            properties[done] = _columns.DoneIsStatus
                ? new JsonObject { ["status"] = new JsonObject { ["name"] = f.Done ? _columns.DoneOption : _columns.OpenOption } }
                : new JsonObject { ["checkbox"] = f.Done };
        }

        if (_columns.Due is { } due)
        {
            properties[due] = new JsonObject { ["date"] = f.Due is { } d ? new JsonObject { ["start"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) } : null };
        }

        if (_columns.Priority is { } priority)
        {
            // An existing option that means the same is reused; otherwise Notion adds the option.
            var name = _columns.PriorityOptions.FirstOrDefault(o => PriorityOf(o) == f.Priority && (f.Priority != Priority.Normal || o.Contains("normal", StringComparison.OrdinalIgnoreCase) || o.Contains("medium", StringComparison.OrdinalIgnoreCase)))
                ?? f.Priority.ToString();
            properties[priority] = new JsonObject { ["select"] = new JsonObject { ["name"] = name } };
        }

        if (_columns.Tags is { } tags)
        {
            properties[tags] = new JsonObject { ["multi_select"] = new JsonArray([.. f.Tags.Select(t => (JsonNode)new JsonObject { ["name"] = t.Replace(",", " ", StringComparison.Ordinal) })]) };
        }

        return properties;
    }

    private sealed record Columns(string Title, string? Done, bool DoneIsStatus, IReadOnlySet<string> CompleteOptions, string? DoneOption, string? OpenOption, string? Due, string? Priority, IReadOnlyList<string> PriorityOptions, string? Tags)
    {
        public static Columns Read(JsonObject properties)
        {
            string? Find(string type, Regex? name = null, bool fallback = true)
            {
                var ofType = properties.Where(p => Type(p.Value) == type).Select(p => p.Key).ToList();
                return ofType.FirstOrDefault(k => name?.IsMatch(k) == true) ?? (fallback ? ofType.FirstOrDefault() : null);
            }

            var title = Find("title") ?? throw new InvalidOperationException("This Notion database has no title column.");
            var checkbox = Find("checkbox", DoneName());
            string? status = checkbox is null ? Find("status", StatusName()) : null;
            var complete = new HashSet<string>(StringComparer.Ordinal);
            string? doneOption = null;
            string? openOption = null;
            if (status is not null && properties[status]?["status"] is JsonObject s)
            {
                var names = s["options"]?.AsArray().OfType<JsonObject>().ToDictionary(o => o["id"]!.GetValue<string>(), o => o["name"]!.GetValue<string>()) ?? [];
                string[] Group(string name) => [.. s["groups"]?.AsArray().OfType<JsonObject>()
                    .Where(g => string.Equals(g["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(g => g["option_ids"]!.AsArray().Select(i => names.GetValueOrDefault(i!.GetValue<string>())))
                    .OfType<string>() ?? []];
                complete.UnionWith(Group("Complete"));
                doneOption = complete.FirstOrDefault();
                openOption = Group("To-do").FirstOrDefault() ?? names.Values.FirstOrDefault(n => !complete.Contains(n));
                if (doneOption is null || openOption is null)
                {
                    status = null; // a status column without a done/open meaning isn't synced
                }
            }

            var priority = Find("select", PriorityName(), fallback: false);
            IReadOnlyList<string> priorityOptions = priority is null ? [] : [.. properties[priority]?["select"]?["options"]?.AsArray().Select(o => o?["name"]?.GetValue<string>()).OfType<string>() ?? []];
            return new Columns(title, checkbox ?? status, status is not null, complete, doneOption, openOption, Find("date", DueName()), priority, priorityOptions, Find("multi_select", TagsName(), fallback: false));
        }
    }

    [GeneratedRegex("^(done|completed?|finished|complete)$", RegexOptions.IgnoreCase)]
    private static partial Regex DoneName();

    [GeneratedRegex("status", RegexOptions.IgnoreCase)]
    private static partial Regex StatusName();

    [GeneratedRegex("^(due|due date|deadline|date|when)$", RegexOptions.IgnoreCase)]
    private static partial Regex DueName();

    [GeneratedRegex("priority|importance", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityName();

    [GeneratedRegex("^(tags?|labels?)$", RegexOptions.IgnoreCase)]
    private static partial Regex TagsName();
}
