using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TodoTracker.Core.Sync;
using TodoTracker.Core.Vault;
using Xunit;

namespace TodoTracker.Core.Tests.Sync;

/// <summary>A GitHub gist API in memory: ETags, 304s, history versions, truncated files.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (Dictionary<string, string> Files, List<string> History, string Description)> _gists = [];

    public int FullReads { get; private set; }

    public int NotModified { get; private set; }

    public string? Token { get; set; } = "token";

    /// <summary>Files bigger than this come back truncated (with a raw_url).</summary>
    public int TruncateAt { get; set; } = int.MaxValue;

    public IReadOnlyDictionary<string, string> Files(string id)
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(_gists[id].Files);
        }
    }

    public IEnumerable<string> Ids
    {
        get
        {
            lock (_lock)
            {
                return [.. _gists.Keys];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        lock (_lock)
        {
            if (request.Headers.Authorization?.Parameter != Token)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
            }

            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "raw.example")
            {
                var (gistId, name) = (path.Split('/')[1], Uri.UnescapeDataString(path.Split('/')[2]));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_gists[gistId].Files[name]) };
            }

            if (request.Method == HttpMethod.Get && path == "/gists")
            {
                var list = new JsonArray([.. _gists.Select(g => (JsonNode)new JsonObject
                {
                    ["id"] = g.Key,
                    ["description"] = g.Value.Description,
                    ["files"] = new JsonObject([.. g.Value.Files.Keys.Select(f => KeyValuePair.Create(f, (JsonNode?)new JsonObject { ["filename"] = f }))]),
                })]);
                return Json(list, null);
            }

            if (request.Method == HttpMethod.Post && path == "/gists")
            {
                var id = $"g{_gists.Count + 1}";
                _gists[id] = (new Dictionary<string, string>(), [], body!["description"]!.GetValue<string>());
                Apply(id, body);
                return Json(Gist(id), ETag(id));
            }

            var gist = path["/gists/".Length..];
            if (!_gists.ContainsKey(gist))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            if (request.Method == HttpMethod.Get)
            {
                if (request.Headers.IfNoneMatch.Any(t => t.Tag == ETag(gist)))
                {
                    NotModified++;
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                FullReads++;
                return Json(Gist(gist), ETag(gist));
            }

            Apply(gist, body!);
            return Json(Gist(gist), ETag(gist));
        }
    }

    private void Apply(string id, JsonNode body)
    {
        foreach (var (name, file) in body["files"]!.AsObject())
        {
            _gists[id].Files[name] = file!["content"]!.GetValue<string>();
        }

        _gists[id].History.Insert(0, $"v{_gists[id].History.Count + 1}");
    }

    private JsonObject Gist(string id)
    {
        var (files, history, description) = _gists[id];
        return new JsonObject
        {
            ["id"] = id,
            ["description"] = description,
            ["files"] = new JsonObject([.. files.Select(f => KeyValuePair.Create(f.Key, (JsonNode?)(f.Value.Length > TruncateAt
                ? new JsonObject { ["content"] = f.Value[..TruncateAt], ["truncated"] = true, ["raw_url"] = $"https://raw.example/{id}/{Uri.EscapeDataString(f.Key)}" }
                : new JsonObject { ["content"] = f.Value, ["truncated"] = false })))]),
            ["history"] = new JsonArray([.. history.Select(v => (JsonNode)new JsonObject { ["version"] = v })]),
        };
    }

    private string ETag(string id) => $"\"{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', _gists[id].History) + string.Concat(_gists[id].Files.Select(f => f.Key + f.Value)))))[..16]}\"";

    private static HttpResponseMessage Json(JsonNode node, string? etag)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }
}

public sealed class GistRemoteTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root = Directory.CreateTempSubdirectory("tt-gist-").FullName;
    private readonly FakeGitHub _github = new();
    private readonly HttpClient _http;
    private readonly List<(VaultBoardStore Store, SyncEngine Engine)> _devices = [];

    public GistRemoteTests() => _http = new HttpClient(_github);

    public void Dispose()
    {
        foreach (var (store, engine) in _devices)
        {
            engine.Dispose();
            store.Dispose();
        }

        _http.Dispose();
        _github.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private (VaultBoardStore Store, SyncEngine Engine) Device(string name)
    {
        var home = Path.Combine(_root, name);
        var store = VaultBoardStore.Open(new VaultOptions(Path.Combine(home, "vault")) { TimeZone = TimeZoneInfo.Utc, LockDirectory = Path.Combine(home, "locks"), Watch = false, EditSettleTime = TimeSpan.Zero });
        var idFile = Path.Combine(home, "gist-id");
        var remote = new GistRemote(_http, _ => Task.FromResult("token"), () => File.Exists(idFile) ? File.ReadAllText(idFile) : null, id => File.WriteAllText(idFile, id));
        var engine = new SyncEngine(store, remote, new SyncState(Path.Combine(home, "sync"), Path.Combine(home, "device"), name), Path.Combine(home, "sync.lock"));
        _devices.Add((store, engine));
        return (store, engine);
    }

    private static Task<List<string>> Titles(VaultBoardStore store) => store.ReadAsync(b => b.AllItems().Select(i => i.Title).Order(StringComparer.Ordinal).ToList());

    [Fact]
    public async Task Two_devices_sync_through_one_private_gist()
    {
        var (laptop, laptopSync) = Device("Laptop");
        var (desktop, desktopSync) = Device("Desktop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("From the laptop"), Actor.User, T0));

        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        await desktop.UpdateAsync(b => b.AddTask(new NewTask("From the desktop"), Actor.User, T0));
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);

        var id = Assert.Single(_github.Ids);
        Assert.Equal(["From the desktop", "From the laptop"], await Titles(laptop));
        Assert.Equal(["From the desktop", "From the laptop"], await Titles(desktop));
        Assert.Contains(GistRemote.Marker, _github.Files(id).Keys);
        Assert.Equal(3, _github.Files(id).Count);
    }

    [Fact]
    public async Task Nothing_new_is_one_conditional_request_answered_304()
    {
        var (laptop, laptopSync) = Device("Laptop");
        var (_, desktopSync) = Device("Desktop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("Hello"), Actor.User, T0));
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        var reads = _github.FullReads;

        var again = await laptopSync.SyncAsync(TestContext.Current.CancellationToken);

        Assert.True(again.Skipped);
        Assert.Equal(reads, _github.FullReads);
        Assert.True(_github.NotModified > 0);
    }

    [Fact]
    public async Task A_write_by_another_device_in_between_is_not_skipped()
    {
        // Our own write changes the ETag; it's only taken as "seen" when nothing else was written in between.
        var (laptop, laptopSync) = Device("Laptop");
        var (desktop, desktopSync) = Device("Desktop");
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);

        await desktop.UpdateAsync(b => b.AddTask(new NewTask("Desktop wrote first"), Actor.User, T0));
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("Laptop wrote after"), Actor.User, T0));
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Desktop wrote first", await Titles(laptop));
    }

    [Fact]
    public async Task Big_device_files_are_read_whole_from_raw_url()
    {
        _github.TruncateAt = 200;
        var (laptop, laptopSync) = Device("Laptop");
        var (desktop, desktopSync) = Device("Desktop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("Long one") { Details = new string('x', 2000) }, Actor.User, T0));

        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Long one", await Titles(desktop));
    }

    [Fact]
    public async Task A_second_computer_finds_the_existing_gist()
    {
        var (laptop, laptopSync) = Device("Laptop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("First"), Actor.User, T0));
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);

        var (desktop, desktopSync) = Device("Desktop");
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Single(_github.Ids);
        Assert.Contains("First", await Titles(desktop));
    }

    [Fact]
    public async Task A_rejected_sign_in_says_how_to_fix_it()
    {
        var (_, laptopSync) = Device("Laptop");
        _github.Token = "other";

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => laptopSync.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Contains("gh auth login", error.Message, StringComparison.Ordinal);
    }
}
