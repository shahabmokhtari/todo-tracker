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
public sealed partial class MicrosoftToDoRemote(HttpClient http, Func<CancellationToken, Task<string>> token, string listId) : IConnectorRemote
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
        var body = Write(fields, fields.Done ? "completed" : "notStarted");
        body["linkedResources"] = new JsonArray(Link(localId));
        var task = await SendAsync(http, token, HttpMethod.Post, Tasks, body, cancellationToken).ConfigureAwait(false);
        return task!["id"]!.GetValue<string>();
    }

    public async Task UpdateAsync(string id, SyncedFields fields, CancellationToken cancellationToken)
    {
        // "In progress" or "waiting" stay as they are unless the task is finished or reopened.
        var current = await SendAsync(http, token, HttpMethod.Get, $"{Tasks}/{Uri.EscapeDataString(id)}", null, cancellationToken).ConfigureAwait(false);
        var status = current?["status"]?.GetValue<string>() ?? "notStarted";
        var next = fields.Done ? "completed" : status == "completed" ? "notStarted" : status;
        await SendAsync(http, token, HttpMethod.Patch, $"{Tasks}/{Uri.EscapeDataString(id)}", Write(fields, next), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>To Do has no archive: the task is finished (and can be reopened there).</summary>
    public Task ArchiveAsync(string id, CancellationToken cancellationToken) =>
        SendAsync(http, token, HttpMethod.Patch, $"{Tasks}/{Uri.EscapeDataString(id)}", new JsonObject { ["status"] = "completed" }, cancellationToken);

    public Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken) =>
        SendAsync(http, token, HttpMethod.Post, $"{Tasks}/{Uri.EscapeDataString(id)}/linkedResources", Link(localId), cancellationToken);

    private static JsonObject Link(Guid localId) => new() { ["applicationName"] = AppName, ["displayName"] = AppName, ["externalId"] = localId.ToString() };

    private static JsonObject Write(SyncedFields f, string status) => new()
    {
        ["title"] = f.Title,
        ["status"] = status,
        ["importance"] = f.Priority switch { Priority.Low => "low", Priority.High or Priority.Critical => "high", _ => "normal" },
        ["dueDateTime"] = f.Due is { } d ? new JsonObject { ["dateTime"] = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00", ["timeZone"] = "UTC" } : null,
        ["body"] = new JsonObject { ["content"] = f.Notes ?? string.Empty, ["contentType"] = "text" },
    };

    private static RemoteItem Read(JsonObject task)
    {
        var dueText = task["dueDateTime"]?["dateTime"]?.GetValue<string>();
        DateOnly? due = dueText is { Length: >= 10 } && DateOnly.TryParseExact(dueText[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
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
