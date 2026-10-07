using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TodoTracker.Core.Connectors;

public sealed record ToDoList(string Id, string Name);

/// <summary>
/// One Microsoft To Do list (through Microsoft Graph) as an outside list: title, done, due date, importance and the
/// note. Each task carries its Todo Tracker id as a linked resource (so nothing is ever copied twice). To Do has no
/// tags and no "critical": tags stay here, critical shows as high there.
/// </summary>
public sealed partial class MicrosoftToDoRemote(HttpClient http, Func<CancellationToken, Task<string>> token, string listId, TimeZoneInfo zone) : IConnectorRemote
{
    public const string AppName = "Todo Tracker";

    public ConnectorFields Fields => ConnectorFields.Title | ConnectorFields.Done | ConnectorFields.Due | ConnectorFields.Priority | ConnectorFields.Notes;

    private string Tasks => $"v1.0/me/todo/lists/{Uri.EscapeDataString(listId)}/tasks";

    public SyncedFields Normalize(SyncedFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return fields with { Priority = fields.Priority == Priority.Critical ? Priority.High : fields.Priority };
    }

    /// <summary>The person's To Do lists.</summary>
    public static async Task<IReadOnlyList<ToDoList>> ListsAsync(HttpClient http, Func<CancellationToken, Task<string>> token, CancellationToken cancellationToken)
    {
        var lists = new List<ToDoList>();
        string? next = "v1.0/me/todo/lists";
        while (next is not null)
        {
            var page = await SendAsync(http, token, HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            lists.AddRange(page!["value"]!.AsArray().OfType<JsonObject>().Select(l => new ToDoList(l["id"]!.GetValue<string>(), l["displayName"]?.GetValue<string>() ?? "Tasks")));
            next = page["@odata.nextLink"]?.GetValue<string>();
        }

        return lists;
    }

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken cancellationToken)
    {
        var items = new List<RemoteItem>();
        string? next = $"{Tasks}?$top=100&$expand=linkedResources";
        while (next is not null)
        {
            var page = await SendAsync(http, token, HttpMethod.Get, next, null, cancellationToken).ConfigureAwait(false);
            items.AddRange(page!["value"]!.AsArray().OfType<JsonObject>().Select(Read));
            next = page["@odata.nextLink"]?.GetValue<string>();
        }

        return items;
    }

    public async Task<RemoteItem?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var task = await SendAsync(http, token, HttpMethod.Get, $"{Tasks}/{Uri.EscapeDataString(id)}?$expand=linkedResources", null, cancellationToken, missingIsNull: true).ConfigureAwait(false);
        return task is null ? null : Read(task);
    }

    public async Task<string> CreateAsync(Guid localId, SyncedFields fields, CancellationToken cancellationToken)
    {
        var body = Write(fields, null);
        body["linkedResources"] = new JsonArray(Link(localId));
        var task = await SendAsync(http, token, HttpMethod.Post, Tasks, body, cancellationToken).ConfigureAwait(false);
        return task!["id"]!.GetValue<string>();
    }

    public async Task UpdateAsync(string id, SyncedFields fields, SyncedFields? previous, CancellationToken cancellationToken)
    {
        var body = Write(fields, previous);
        if (body.Count > 0)
        {
            await SendAsync(http, token, HttpMethod.Patch, $"{Tasks}/{Uri.EscapeDataString(id)}", body, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>To Do has no archive: the task is finished (and can be reopened there).</summary>
    public Task ArchiveAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(http, token, HttpMethod.Patch, $"{Tasks}/{Uri.EscapeDataString(id)}", new JsonObject { ["status"] = "completed" }, cancellationToken);

    public Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken) =>
        SendAsync(http, token, HttpMethod.Post, $"{Tasks}/{Uri.EscapeDataString(id)}/linkedResources", Link(localId), cancellationToken);

    private static JsonObject Link(Guid localId) => new() { ["applicationName"] = AppName, ["displayName"] = AppName, ["externalId"] = localId.ToString() };

    /// <summary>All fields for a new task, else only those that changed ("In progress" stays unless it's finished or
    /// reopened; a note with formatting stays unless the note changed).</summary>
    private JsonObject Write(SyncedFields f, SyncedFields? previous)
    {
        bool Changed(ConnectorFields field) => previous is null || !SyncedFields.Same(Normalize(f), Normalize(previous), field);
        var body = new JsonObject();
        if (Changed(ConnectorFields.Title))
        {
            body["title"] = f.Title;
        }

        if (Changed(ConnectorFields.Done))
        {
            body["status"] = f.Done ? "completed" : "notStarted";
        }

        if (Changed(ConnectorFields.Priority))
        {
            body["importance"] = f.Priority switch { Priority.Low => "low", Priority.High or Priority.Critical => "high", _ => "normal" };
        }

        if (Changed(ConnectorFields.Due))
        {
            // Midday UTC is the same day in every time zone people live in.
            body["dueDateTime"] = f.Due is { } d ? new JsonObject { ["dateTime"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T12:00:00", ["timeZone"] = "UTC" } : null;
        }

        if (Changed(ConnectorFields.Notes))
        {
            body["body"] = new JsonObject { ["content"] = f.Notes ?? string.Empty, ["contentType"] = "text" };
        }

        return body;
    }

    /// <summary>The day of a due date, in the person's time zone (To Do keeps a due date as local midnight, in UTC).</summary>
    private DateOnly? DueOf(JsonNode? due)
    {
        var text = due?["dateTime"]?.GetValue<string>();
        if (text is null || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
        {
            return null;
        }

        var utc = string.Equals(due?["timeZone"]?.GetValue<string>(), "UTC", StringComparison.OrdinalIgnoreCase);
        return DateOnly.FromDateTime(utc ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone) : at);
    }

    private RemoteItem Read(JsonObject task)
    {
        var due = DueOf(task["dueDateTime"]);
        var priority = task["importance"]?.GetValue<string>() switch { "high" => Priority.High, "low" => Priority.Low, _ => Priority.Normal };
        var content = task["body"]?["content"]?.GetValue<string>() ?? string.Empty;
        var notes = string.Equals(task["body"]?["contentType"]?.GetValue<string>(), "html", StringComparison.OrdinalIgnoreCase) ? PlainText(content) : content.Trim();
        var marker = task["linkedResources"]?.AsArray().OfType<JsonObject>()
            .Where(r => r["applicationName"]?.GetValue<string>() == AppName)
            .Select(r => Guid.TryParse(r["externalId"]?.GetValue<string>(), out var g) ? g : (Guid?)null)
            .LastOrDefault(g => g is not null);
        return new RemoteItem(
            task["id"]!.GetValue<string>(),
            new SyncedFields(task["title"]?.GetValue<string>() ?? string.Empty, task["status"]?.GetValue<string>() == "completed", due, priority, [], notes.Length == 0 ? null : notes),
            marker);
    }

    private static string PlainText(string html)
    {
        var text = LineBreaks().Replace(html, "\n");
        text = WebUtility.HtmlDecode(Tags().Replace(text, string.Empty));
        return ManyLines().Replace(text.Replace("\r", string.Empty, StringComparison.Ordinal), "\n\n").Trim();
    }

    private static async Task<JsonObject?> SendAsync(HttpClient http, Func<CancellationToken, Task<string>> token, HttpMethod method, string path, JsonObject? body, CancellationToken cancellationToken, bool missingIsNull = false)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellationToken).ConfigureAwait(false));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound && missingIsNull)
        {
            return null;
        }

        JsonObject? json = null;
        try
        {
            json = text.Length == 0 ? [] : JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON: the status says what happened
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Microsoft To Do: {json?["error"]?["message"]?.GetValue<string>() ?? response.ReasonPhrase}", null, response.StatusCode);
        }

        return json ?? [];
    }

    [GeneratedRegex(@"<\s*(br|/p|/div|/li)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyLines();
}
