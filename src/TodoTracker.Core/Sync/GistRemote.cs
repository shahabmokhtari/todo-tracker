using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TodoTracker.Core.Sync;

/// <summary>
/// A private GitHub gist as the meeting place: one file per device (<c>device-&lt;id&gt;.json</c>: its snapshot and the
/// contents, base64). Checks for changes with the gist's ETag (<c>If-None-Match</c>; 304 = nothing new), and only ever
/// changes its own file.
/// </summary>
public sealed class GistRemote : ISyncRemote
{
    public const string DescriptionPrefix = "Todo Tracker sync: ";
    public const string Marker = "todo-tracker-sync.md";

    /// <summary>GitHub serves at most 10 MB of a gist file (bigger needs git); each device's file stays below this.</summary>
    public const int MaxBundleBytes = 9 * 1024 * 1024;

    private const string Api = "https://api.github.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly Func<string?> _loadId;
    private readonly Action<string> _saveId;
    private readonly string _description;
    private readonly Dictionary<string, Dictionary<string, string>> _contents = [];

    /// <param name="http">Talks to api.github.com.</param>
    /// <param name="token">A GitHub token with the gist scope.</param>
    /// <param name="loadId">The gist used before (null the first time: it is looked up, or made on the first publish).</param>
    /// <param name="saveId">Remembers the gist.</param>
    /// <param name="library">Which set of tasks (one gist each, so separate tasks folders never mix).</param>
    public GistRemote(HttpClient http, Func<CancellationToken, Task<string>> token, Func<string?> loadId, Action<string> saveId, string library = "Todo Tracker")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _loadId = loadId ?? throw new ArgumentNullException(nameof(loadId));
        _saveId = saveId ?? throw new ArgumentNullException(nameof(saveId));
        _description = DescriptionPrefix + library;
    }

    public string Identity => "gist:" + _description;

    public async Task ForgetAsync(string device, CancellationToken cancellationToken)
    {
        if (await GistIdAsync(cancellationToken).ConfigureAwait(false) is not { } id || device is not { Length: > 1 and <= 64 } || !device.All(char.IsAsciiLetterOrDigit))
        {
            return;
        }

        using var request = await RequestAsync(HttpMethod.Patch, $"/gists/{id}", cancellationToken).ConfigureAwait(false);
        request.Content = Body(new JsonObject { ["files"] = new JsonObject { [$"device-{device}.json"] = null } });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSnapshot?> ReadAsync(string self, string? knownVersion, CancellationToken cancellationToken)
    {
        var id = await GistIdAsync(cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return knownVersion == "none" ? null : new RemoteSnapshot([], "none", SelfMissing: true);
        }

        using var request = await RequestAsync(HttpMethod.Get, $"/gists/{id}", cancellationToken).ConfigureAwait(false);
        if (knownVersion is not null && Split(knownVersion).ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return null;
        }

        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        var gist = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
        var devices = new List<DeviceSnapshot>();
        var partial = false;
        _contents.Clear();
        var files = gist["files"]?.AsObject() ?? [];
        foreach (var (name, file) in files)
        {
            if (!name.StartsWith("device-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) || name == $"device-{self}.json" || file is null)
            {
                continue;
            }

            try
            {
                // Big files are cut short in the gist's JSON: the whole file is at raw_url.
                var text = file["truncated"]?.GetValue<bool>() == true && file["raw_url"]?.GetValue<string>() is { } raw
                    ? await RawAsync(raw, cancellationToken).ConfigureAwait(false)
                    : file["content"]?.GetValue<string>();
                var bundle = text is null ? null : JsonSerializer.Deserialize<Bundle>(text, Json);
                if (bundle?.Snapshot is { Schema: 1, Entries: not null } snapshot && name == $"device-{snapshot.Device}.json")
                {
                    devices.Add(snapshot);
                    _contents[snapshot.Device] = bundle.Contents ?? [];
                }
                else
                {
                    partial = true;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or HttpRequestException)
            {
                // Damaged, too big, or from a newer version: skipped, and looked at again next time.
                partial = true;
            }
        }

        return new RemoteSnapshot(devices, Join(response.Headers.ETag?.ToString(), Revision(gist)), partial, SelfMissing: files[$"device-{self}.json"] is null);
    }

    public Task<byte[]> ReadContentAsync(DeviceSnapshot device, SyncEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(entry);
        if (_contents.TryGetValue(device.Device, out var contents) && contents.TryGetValue(entry.Hash, out var base64))
        {
            try
            {
                return Task.FromResult(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                throw new InvalidDataException($"{entry.Path} from {device.Name} is damaged.");
            }
        }

        throw new FileNotFoundException($"{entry.Path} from {device.Name} isn't in the gist.");
    }

    public async Task<string?> PublishAsync(DeviceSnapshot snapshot, Func<SyncEntry, byte[]> read, string? readVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(read);
        var contents = snapshot.Entries.DistinctBy(e => e.Hash).ToDictionary(e => e.Hash, e => Convert.ToBase64String(read(e)), StringComparer.Ordinal);
        var bundle = JsonSerializer.Serialize(new Bundle(snapshot, contents), Json);
        if (Encoding.UTF8.GetByteCount(bundle) > MaxBundleBytes)
        {
            throw new InvalidOperationException($"Your tasks are too big for a gist ({Encoding.UTF8.GetByteCount(bundle) / (1024 * 1024)} MB with attachments; GitHub serves up to {MaxBundleBytes / (1024 * 1024)} MB per file). Sync through OneDrive or iCloud Drive instead, or move big attachments out of the tasks folder.");
        }

        var files = new JsonObject { [$"device-{snapshot.Device}.json"] = new JsonObject { ["content"] = bundle } };

        var id = await GistIdAsync(cancellationToken).ConfigureAwait(false);
        HttpRequestMessage request;
        if (id is null)
        {
            files[Marker] = new JsonObject { ["content"] = "# Todo Tracker sync\n\nTodo Tracker keeps each device's tasks here to sync them. Don't edit these files.\n" };
            request = await RequestAsync(HttpMethod.Post, "/gists", cancellationToken).ConfigureAwait(false);
            request.Content = Body(new JsonObject { ["description"] = _description, ["public"] = false, ["files"] = files });
        }
        else
        {
            request = await RequestAsync(HttpMethod.Patch, $"/gists/{id}", cancellationToken).ConfigureAwait(false);
            request.Content = Body(new JsonObject { ["files"] = files });
        }

        using (request)
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
            var gist = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
            if (id is null && gist["id"]?.GetValue<string>() is { } created)
            {
                _saveId(created);
            }

            // Our write is the only change since we read the gist when the version before it is the one we read:
            // then the next check may start from here; otherwise it must look again.
            var history = gist["history"]?.AsArray();
            var before = history is { Count: > 1 } ? history[1]?["version"]?.GetValue<string>() : null;
            return readVersion is not null && before is not null && before == Split(readVersion).Revision
                ? Join(response.Headers.ETag?.ToString(), Revision(gist))
                : null;
        }
    }

    private static string Join(string? etag, string? revision) => $"{etag}|{revision}";

    private static (string ETag, string Revision) Split(string version)
    {
        var bar = version.LastIndexOf('|');
        return bar < 0 ? (version, string.Empty) : (version[..bar], version[(bar + 1)..]);
    }

    private static string? Revision(JsonNode gist) => gist["history"]?.AsArray() is { Count: > 0 } history ? history[0]?["version"]?.GetValue<string>() : null;

    private static StringContent Body(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub didn't accept the sign-in (sign in again with gh auth login, with the gist scope).",
            HttpStatusCode.NotFound => "The sync gist wasn't found (was it deleted?).",
            HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests => "GitHub is limiting requests right now; sync tries again later.",
            _ => $"GitHub answered {(int)response.StatusCode}: {detail[..Math.Min(200, detail.Length)]}",
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private async Task<string?> GistIdAsync(CancellationToken cancellationToken)
    {
        if (_loadId() is { Length: > 0 } known)
        {
            return known;
        }

        // A gist made earlier (on another device, or before a reinstall): found by its description and marker file.
        // If two devices made one at the same moment, all use the oldest.
        var found = new List<(string Id, string Created)>();
        for (var page = 1; page <= 5; page++)
        {
            using var request = await RequestAsync(HttpMethod.Get, $"/gists?per_page=100&page={page}", cancellationToken).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
            var gists = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsArray();
            foreach (var gist in gists)
            {
                if (gist?["description"]?.GetValue<string>() == _description && gist["files"]?[Marker] is not null && gist["id"]?.GetValue<string>() is { } id)
                {
                    found.Add((id, gist["created_at"]?.GetValue<string>() ?? string.Empty));
                }
            }

            if (gists.Count < 100)
            {
                break;
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        var oldest = found.OrderBy(f => f.Created, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal).First().Id;
        _saveId(oldest);
        return oldest;
    }

    private async Task<string> RawAsync(string url, CancellationToken cancellationToken)
    {
        // The token only ever goes to GitHub.
        var uri = new Uri(url);
        var github = uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "gist.githubusercontent.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "api.github.com");
        using var request = github ? await RequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false) : new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxBundleBytes * 2L)
        {
            throw new InvalidOperationException("A device's sync file is too big.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, path.StartsWith("https://", StringComparison.Ordinal) ? new Uri(path) : new Uri(Api + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(cancellationToken).ConfigureAwait(false));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("TodoTracker-Sync");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private sealed record Bundle(DeviceSnapshot Snapshot, Dictionary<string, string>? Contents);
}
