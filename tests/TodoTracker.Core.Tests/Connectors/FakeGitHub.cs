using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace TodoTracker.Core.Tests.Connectors;

/// <summary>GitHub in memory: the signed-in user, repositories (topics, description) and their issues (only what the
/// connector uses). Labels are the repository's, without case, like GitHub's: an issue given "color:#ff8800" gets an
/// existing "color:#FF8800".</summary>
public sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Dictionary<string, int> _next = new(StringComparer.OrdinalIgnoreCase);

    public FakeGitHub(string repo = "me/todo-work")
    {
        Repo = repo;
        AddRepo(repo, "Todo Tracker: Work", "todo-tracker");
    }

    public string Login { get; init; } = "me";

    /// <summary>The repository <see cref="Issues"/> and <see cref="Add"/> work on.</summary>
    public string Repo { get; set; }

    public Dictionary<string, JsonObject> Repos { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, SortedDictionary<int, JsonObject>> IssuesByRepo { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Issues by number (pull requests too: GitHub lists them with the issues).</summary>
    public SortedDictionary<int, JsonObject> Issues => IssuesByRepo[Repo];

    public List<string> Requests { get; } = [];

    /// <summary>Each repository's labels (as first written).</summary>
    public Dictionary<string, List<string>> LabelsByRepo { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The repository's own spelling of a label (made when it's new).</summary>
    public string Label(string repo, string name)
    {
        var labels = LabelsByRepo[repo];
        var existing = labels.FirstOrDefault(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            labels.Add(name);
        }

        return existing ?? name;
    }

    public JsonObject AddRepo(string fullName, string? description = null, params string[] topics)
    {
        var repo = new JsonObject
        {
            ["full_name"] = fullName,
            ["description"] = description,
            ["has_issues"] = true,
            ["private"] = true,
            ["topics"] = new JsonArray([.. topics.Select(t => (JsonNode)t)]),
        };
        Repos[fullName] = repo;
        IssuesByRepo.TryAdd(fullName, []);
        LabelsByRepo.TryAdd(fullName, []);
        return repo;
    }

    public JsonObject Add(string title, string? body = null, bool closed = false, params string[] labels) => AddTo(Repo, title, body, closed, labels);

    public JsonObject AddTo(string repo, string title, string? body = null, bool closed = false, params string[] labels)
    {
        var number = _next[repo] = _next.GetValueOrDefault(repo) + 1;
        var issue = new JsonObject
        {
            ["number"] = number,
            ["title"] = title,
            ["body"] = body,
            ["state"] = closed ? "closed" : "open",
            ["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)new JsonObject { ["name"] = Label(repo, l) })]),
        };
        IssuesByRepo[repo][number] = issue;
        return issue;
    }

    public IEnumerable<string> LabelsOf(int number) => Issues[number]["labels"]!.AsArray().Select(l => l!["name"]!.GetValue<string>());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery.TrimStart('/'));
        Requests.Add($"{request.Method} {path}");
        Assert.StartsWith("Bearer ", request.Headers.Authorization?.ToString(), StringComparison.Ordinal);
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
        if (path == "user")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["login"] = Login });
        }

        var parts = path.Split('?')[0].Split('/');
        if (parts.Length < 3 || parts[0] != "repos" || !Repos.TryGetValue($"{parts[1]}/{parts[2]}", out var repo))
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });
        }

        var fullName = repo["full_name"]!.GetValue<string>();
        if (parts.Length == 3)
        {
            return Json(HttpStatusCode.OK, repo.DeepClone());
        }

        if (parts[3] == "topics" && request.Method == HttpMethod.Put)
        {
            repo["topics"] = body!["names"]!.DeepClone();
            return Json(HttpStatusCode.OK, new JsonObject { ["names"] = repo["topics"]!.DeepClone() });
        }

        if (parts[3] == "labels")
        {
            if (request.Method == HttpMethod.Delete)
            {
                return LabelsByRepo[fullName].RemoveAll(l => string.Equals(l, parts[4], StringComparison.OrdinalIgnoreCase)) > 0
                    ? new HttpResponseMessage(HttpStatusCode.NoContent)
                    : Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });
            }

            var query = System.Web.HttpUtility.ParseQueryString(path.Contains('?', StringComparison.Ordinal) ? path[path.IndexOf('?', StringComparison.Ordinal)..] : string.Empty);
            var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
            return Json(HttpStatusCode.OK, new JsonArray([.. LabelsByRepo[fullName].Skip((page - 1) * 100).Take(100).Select(l => (JsonNode)new JsonObject { ["name"] = l })]));
        }

        if (parts[3] != "issues")
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });
        }

        var issues = IssuesByRepo[fullName];
        if (parts.Length == 4 && request.Method == HttpMethod.Get)
        {
            var query = System.Web.HttpUtility.ParseQueryString(path.Contains('?', StringComparison.Ordinal) ? path[path.IndexOf('?', StringComparison.Ordinal)..] : string.Empty);
            var page = int.Parse(query["page"] ?? "1", CultureInfo.InvariantCulture);
            var size = Math.Min(100, int.Parse(query["per_page"] ?? "30", CultureInfo.InvariantCulture));
            return Json(HttpStatusCode.OK, new JsonArray([.. issues.Values.Skip((page - 1) * size).Take(size).Select(i => i.DeepClone())]));
        }

        if (parts.Length == 4 && request.Method == HttpMethod.Post)
        {
            var labels = body!["labels"]?.AsArray().Select(l => l!.GetValue<string>()).ToArray() ?? [];
            Assert.All(labels, l => Assert.True(l.Length <= 50, $"label too long: {l}"));
            return Json(HttpStatusCode.Created, AddTo(fullName, body["title"]!.GetValue<string>(), body["body"]?.GetValue<string>(), false, labels).DeepClone());
        }

        if (!issues.TryGetValue(int.Parse(parts[4], CultureInfo.InvariantCulture), out var issue))
        {
            return Json(HttpStatusCode.NotFound, new JsonObject { ["message"] = "Not Found" });
        }

        if (request.Method == HttpMethod.Patch)
        {
            foreach (var (key, value) in body!)
            {
                issue[key] = key == "labels" ? new JsonArray([.. value!.AsArray().Select(l => (JsonNode)new JsonObject { ["name"] = Label(fullName, l!.GetValue<string>()) })]) : value?.DeepClone();
            }
        }

        return Json(HttpStatusCode.OK, issue.DeepClone());
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode node) =>
        new(status) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
}
