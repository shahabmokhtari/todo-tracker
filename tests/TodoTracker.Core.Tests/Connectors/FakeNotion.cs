using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Core.Connectors;

namespace TodoTracker.Core.Tests.Connectors;

/// <summary>A pretend Notion: one database, its pages, and the requests made.</summary>
internal sealed class FakeNotion : HttpMessageHandler
{
    public const string Db = "db1";

    public JsonObject Database { get; set; } = Schema(withStatus: false);

    public Dictionary<string, JsonObject> Pages { get; } = [];

    public List<(HttpMethod Method, string Path, JsonNode? Body)> Requests { get; } = [];

    public int TooManyRequests { get; set; }

    public static JsonObject Schema(bool withStatus) => new()
    {
        ["object"] = "database",
        ["id"] = Db,
        ["title"] = new JsonArray(new JsonObject { ["plain_text"] = "Tasks" }),
        ["properties"] = withStatus
            ? new JsonObject
            {
                ["Name"] = new JsonObject { ["type"] = "title" },
                ["Status"] = new JsonObject
                {
                    ["type"] = "status",
                    ["status"] = new JsonObject
                    {
                        ["options"] = new JsonArray(new JsonObject { ["id"] = "o1", ["name"] = "Not started" }, new JsonObject { ["id"] = "o2", ["name"] = "In progress" }, new JsonObject { ["id"] = "o3", ["name"] = "Done" }),
                        ["groups"] = new JsonArray(
                            new JsonObject { ["name"] = "To-do", ["option_ids"] = new JsonArray("o1") },
                            new JsonObject { ["name"] = "In progress", ["option_ids"] = new JsonArray("o2") },
                            new JsonObject { ["name"] = "Complete", ["option_ids"] = new JsonArray("o3") }),
                    },
                },
            }
            : new JsonObject
            {
                ["Task"] = new JsonObject { ["type"] = "title" },
                ["Done"] = new JsonObject { ["type"] = "checkbox" },
                ["Due date"] = new JsonObject { ["type"] = "date" },
                ["Priority"] = new JsonObject { ["type"] = "select" },
                ["Tags"] = new JsonObject { ["type"] = "multi_select" },
                ["Created"] = new JsonObject { ["type"] = "created_time" },
            },
    };

    public JsonObject AddPage(string id, JsonObject properties, bool archived = false)
    {
        var page = new JsonObject { ["object"] = "page", ["id"] = id, ["archived"] = archived, ["in_trash"] = false, ["properties"] = properties };
        Pages[id] = page;
        return page;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add((request.Method, path, body));
        if (request.Headers.Authorization?.Parameter != "secret_token" || !request.Headers.Contains("Notion-Version"))
        {
            return Json(HttpStatusCode.Unauthorized, new JsonObject { ["object"] = "error", ["message"] = "API token is invalid." });
        }

        if (TooManyRequests > 0)
        {
            TooManyRequests--;
            var busy = Json((HttpStatusCode)429, new JsonObject { ["message"] = "Rate limited" });
            busy.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return busy;
        }

        switch (request.Method.Method, path)
        {
            case ("POST", "/v1/search"):
                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = new JsonArray(Database.DeepClone()), ["has_more"] = false });
            case ("GET", "/v1/databases/db1"):
                return Json(HttpStatusCode.OK, (JsonObject)Database.DeepClone());
            case ("PATCH", "/v1/databases/db1"):
                foreach (var (name, value) in body!["properties"]!.AsObject())
                {
                    Database["properties"]![name] = new JsonObject { ["type"] = value!.AsObject().First().Key };
                }

                return Json(HttpStatusCode.OK, (JsonObject)Database.DeepClone());
            case ("POST", "/v1/databases/db1/query"):
            {
                // Two pages of results (page size 2 here) to exercise the cursor.
                var all = Pages.Values.Where(p => !p["archived"]!.GetValue<bool>()).ToList();
                var start = body?["start_cursor"]?.GetValue<string>() is { } cursor ? int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) : 0;
                var slice = all.Skip(start).Take(2).Select(p => (JsonNode)p.DeepClone()).ToArray();
                var more = start + 2 < all.Count;
                return Json(HttpStatusCode.OK, new JsonObject { ["results"] = new JsonArray(slice), ["has_more"] = more, ["next_cursor"] = more ? (start + 2).ToString(System.Globalization.CultureInfo.InvariantCulture) : null });
            }

            case ("POST", "/v1/pages"):
            {
                var id = $"p{Pages.Count + 1}";
                var page = AddPage(id, (JsonObject)body!["properties"]!.DeepClone());
                return Json(HttpStatusCode.OK, (JsonObject)page.DeepClone());
            }
        }

        if (path.StartsWith("/v1/pages/", StringComparison.Ordinal) && Pages.TryGetValue(path["/v1/pages/".Length..], out var existing))
        {
            if (request.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, (JsonObject)existing.DeepClone());
            }

            if (body!["archived"] is { } archived)
            {
                existing["archived"] = archived.GetValue<bool>();
            }

            foreach (var (name, value) in body["properties"]?.AsObject() ?? [])
            {
                existing["properties"]![name] = value!.DeepClone();
            }

            return Json(HttpStatusCode.OK, (JsonObject)existing.DeepClone());
        }

        return Json(HttpStatusCode.NotFound, new JsonObject { ["object"] = "error", ["message"] = "Could not find page." });
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonObject body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
