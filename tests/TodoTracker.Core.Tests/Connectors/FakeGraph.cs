using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Core.Tests.Connectors;

/// <summary>A pretend Microsoft Graph with one To Do list.</summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    public const string List = "L1";

    public Dictionary<string, JsonObject> Tasks { get; } = [];

    public List<(HttpMethod Method, string Path, JsonNode? Body)> Requests { get; } = [];

    public JsonObject Add(string id, string title, string status = "notStarted", string importance = "normal", string? due = null, string? body = null, string? marker = null, string contentType = "text")
    {
        var task = new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["status"] = status,
            ["importance"] = importance,
            ["body"] = new JsonObject { ["content"] = body ?? string.Empty, ["contentType"] = contentType },
            ["linkedResources"] = new JsonArray(),
        };
        if (due is not null)
        {
            task["dueDateTime"] = new JsonObject { ["dateTime"] = $"{due}T00:00:00.0000000", ["timeZone"] = "UTC" };
        }

        if (marker is not null)
        {
            task["linkedResources"]!.AsArray().Add(new JsonObject { ["applicationName"] = "Todo Tracker", ["externalId"] = marker, ["displayName"] = "Todo Tracker" });
        }

        Tasks[id] = task;
        return task;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        Requests.Add((request.Method, path + query, body));
        if (request.Headers.Authorization?.Parameter != "graph-token")
        {
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = new JsonObject { ["message"] = "Access token is empty." } });
        }

        var prefix = $"/v1.0/me/todo/lists/{List}/tasks";
        if (request.Method == HttpMethod.Get && path == "/v1.0/me/todo/lists")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["value"] = new JsonArray(new JsonObject { ["id"] = List, ["displayName"] = "Tasks" }, new JsonObject { ["id"] = "L2", ["displayName"] = "Groceries" }) });
        }

        if (request.Method == HttpMethod.Get && path == prefix)
        {
            // Two per page, with a next link that carries the skip.
            var skip = query.Contains("$skip=2", StringComparison.Ordinal) ? 2 : 0;
            var page = Tasks.Values.Skip(skip).Take(2).Select(t => (JsonNode)t.DeepClone()).ToArray();
            var result = new JsonObject { ["value"] = new JsonArray(page) };
            if (skip == 0 && Tasks.Count > 2)
            {
                result["@odata.nextLink"] = $"https://graph.microsoft.com{prefix}?$top=2&$skip=2&$expand=linkedResources";
            }

            return Json(HttpStatusCode.OK, result);
        }

        if (request.Method == HttpMethod.Post && path == prefix)
        {
            var id = $"t{Tasks.Count + 1}";
            var task = (JsonObject)body!.DeepClone();
            task["id"] = id;
            task["linkedResources"] ??= new JsonArray();
            Tasks[id] = task;
            return Json(HttpStatusCode.Created, task);
        }

        if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            var rest = path[(prefix.Length + 1)..].Split('/');
            if (!Tasks.TryGetValue(rest[0], out var task))
            {
                return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["message"] = "The specified object was not found in the store." } });
            }

            if (rest.Length == 2 && rest[1] == "linkedResources" && request.Method == HttpMethod.Post)
            {
                task["linkedResources"]!.AsArray().Add(body!.DeepClone());
                return Json(HttpStatusCode.Created, (JsonObject)body.DeepClone());
            }

            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, (JsonObject)task.DeepClone());
            }

            if (request.Method == HttpMethod.Patch)
            {
                foreach (var (name, value) in body!.AsObject())
                {
                    task[name] = value?.DeepClone();
                }

                return Json(HttpStatusCode.OK, (JsonObject)task.DeepClone());
            }
        }

        return Json(HttpStatusCode.NotFound, new JsonObject { ["error"] = new JsonObject { ["message"] = "Not found" } });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
