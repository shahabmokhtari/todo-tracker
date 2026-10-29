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

    /// <summary>Changes a file behind the devices' backs (a damaged or half-written one).</summary>
    public void Replace(string id, string name, string content)
    {
        lock (_lock)
        {
            _gists[id].Files[name] = content;
            _gists[id].History.Insert(0, $"v{_gists[id].History.Count + 1}");
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
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host == "raw.example")
            {
                // Not a GitHub host: the token must not be sent there.
                if (request.Headers.Authorization is not null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("token leaked") };
                }

                var (gistId, name) = (path.Split('/')[1], Uri.UnescapeDataString(path.Split('/')[2]));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_gists[gistId].Files[name]) };
            }

            if (request.Headers.Authorization?.Parameter != Token)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") };
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
            if (file is null)
            {
                _gists[id].Files.Remove(name);
            }
            else
            {
                _gists[id].Files[name] = file["content"]!.GetValue<string>();
            }
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

    private (VaultBoardStore Store, SyncEngine Engine) Device(string name, string library = "Todo Tracker")
    {
        var home = Path.Combine(_root, name);
        var store = VaultBoardStore.Open(new VaultOptions(Path.Combine(home, "vault")) { TimeZone = TimeZoneInfo.Utc, LockDirectory = Path.Combine(home, "locks"), Watch = false, EditSettleTime = TimeSpan.Zero });
        var idFile = Path.Combine(home, "gist-id");
        var remote = new GistRemote(_http, _ => Task.FromResult("token"), () => File.Exists(idFile) ? File.ReadAllText(idFile) : null, id => File.WriteAllText(idFile, id), library);
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
    public async Task A_vault_too_big_for_a_gist_says_so()
    {
        // Review finding: big vaults were published anyway and silently couldn't be read back.
        var (laptop, laptopSync) = Device("Laptop");
        var task = await laptop.UpdateAsync(b => b.AddTask(new NewTask("Scans"), Actor.User, T0));
        var big = new byte[GistRemote.MaxBundleBytes];
        Random.Shared.NextBytes(big);
        await laptop.AddAttachmentAsync(task.Id, "scan.bin", new MemoryStream(big), Actor.User, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => laptopSync.SyncAsync(TestContext.Current.CancellationToken));

        Assert.Contains("too big for a gist", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_that_can_not_be_read_is_tried_again()
    {
        var (laptop, laptopSync) = Device("Laptop");
        var (desktop, desktopSync) = Device("Desktop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("Hello"), Actor.User, T0));
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        var id = Assert.Single(_github.Ids);
        var good = _github.Files(id).Single(f => f.Key.StartsWith("device-", StringComparison.Ordinal));
        _github.Replace(id, good.Key, "{ not json");

        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Hello", await Titles(desktop));

        _github.Replace(id, good.Key, good.Value);
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Hello", await Titles(desktop));
    }

    [Fact]
    public async Task Each_library_has_its_own_gist()
    {
        var (laptop, laptopSync) = Device("Laptop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("Work things"), Actor.User, T0));
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);

        var (other, otherSync) = Device("Laptop2", library: "Side project");
        await otherSync.SyncAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("Work things", await Titles(other));
        Assert.Equal(2, _github.Ids.Count());
    }

    [Fact]
    public async Task Two_gists_made_at_once_are_joined_on_the_next_start()
    {
        // Review finding: a device that made the newer gist kept using it, so the two never met.
        var (laptop, laptopSync) = Device("Laptop");
        await laptop.UpdateAsync(b => b.AddTask(new NewTask("On the first gist"), Actor.User, T0));
        await laptopSync.SyncAsync(TestContext.Current.CancellationToken);
        using var http = new HttpClient(_github);
        using var create = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/gists")
        {
            Content = new StringContent("""{"description":"Todo Tracker sync: Todo Tracker","public":false,"files":{"todo-tracker-sync.md":{"content":"x"}}}"""),
        };
        create.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "token");
        var second = JsonNode.Parse(await (await http.SendAsync(create, TestContext.Current.CancellationToken)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["id"]!.GetValue<string>();
        Directory.CreateDirectory(Path.Combine(_root, "Desktop"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Desktop", "gist-id"), second, TestContext.Current.CancellationToken);

        var (desktop, desktopSync) = Device("Desktop");
        await desktopSync.SyncAsync(TestContext.Current.CancellationToken);

        Assert.Contains("On the first gist", await Titles(desktop));
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

    /// <summary>Answers with a function of the request, and remembers what was sent where (with or without the token).</summary>
    private sealed class Recording(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Token)> Sent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task The_token_never_follows_a_redirect_away_from_GitHub()
    {
        using var handler = new Recording(r => r.RequestUri!.AbsolutePath switch
        {
            "/gists" => Ok("[]"),
            "/gists/g1" => Redirect("https://evil.example/steal"),
            _ => Ok("{\"files\":{}}"),
        });
        using var http = new HttpClient(handler);
        var remote = new GistRemote(http, _ => Task.FromResult("secret"), () => "g1", _ => { });

        await remote.ReadAsync("me", null, TestContext.Current.CancellationToken);

        Assert.Contains(handler.Sent, s => s.Uri.Host == "evil.example" && s.Token is null);
        Assert.All(handler.Sent.Where(s => s.Uri.Host == "api.github.com"), s => Assert.Equal("secret", s.Token));
    }

    [Fact]
    public async Task A_redirect_that_isnt_https_or_never_ends_is_refused()
    {
        using var insecure = new Recording(r => r.RequestUri!.AbsolutePath == "/gists" ? Ok("[]") : Redirect("http://api.github.com/gists/g2"));
        using var http1 = new HttpClient(insecure);
        await Assert.ThrowsAsync<HttpRequestException>(() => new GistRemote(http1, _ => Task.FromResult("secret"), () => "g1", _ => { }).ReadAsync("me", null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(insecure.Sent, s => s.Uri.Scheme == "http");

        using var loop = new Recording(r => r.RequestUri!.AbsolutePath == "/gists" ? Ok("[]") : Redirect("https://api.github.com/gists/g1"));
        using var http2 = new HttpClient(loop);
        await Assert.ThrowsAsync<HttpRequestException>(() => new GistRemote(http2, _ => Task.FromResult("secret"), () => "g1", _ => { }).ReadAsync("me", null, TestContext.Current.CancellationToken));
        Assert.True(loop.Sent.Count(s => s.Uri.AbsolutePath == "/gists/g1") <= 6);
    }

    [Fact]
    public async Task A_big_file_is_fetched_with_the_token_only_from_GitHubs_gist_host()
    {
        var bundle = "{\"snapshot\":{\"device\":\"other1\",\"name\":\"Other\",\"at\":\"2026-01-05T09:00:00+00:00\",\"entries\":[],\"schema\":1},\"contents\":{}}";
        foreach (var (host, token) in new[] { ("gist.githubusercontent.com", "secret"), ("objects.githubusercontent.com", (string?)null) })
        {
            using var handler = new Recording(r => r.RequestUri!.AbsolutePath switch
            {
                "/gists" => Ok("[]"),
                "/gists/g1" => Ok("{\"files\":{\"device-other1.json\":{\"truncated\":true,\"raw_url\":\"https://" + host + "/raw/device-other1.json\"}}}"),
                _ => Ok(bundle),
            });
            using var http = new HttpClient(handler);
            var remote = new GistRemote(http, _ => Task.FromResult("secret"), () => "g1", _ => { });

            var read = await remote.ReadAsync("me", null, TestContext.Current.CancellationToken);

            Assert.Equal("other1", Assert.Single(read!.Devices).Device);
            Assert.Equal(token, handler.Sent.Single(s => s.Uri.Host == host).Token);
        }
    }
}
