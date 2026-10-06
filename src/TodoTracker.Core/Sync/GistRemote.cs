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
    public const string Description = "Todo Tracker sync";
    public const string Marker = "todo-tracker-sync.md";
    private const string Api = "https://api.github.com";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;
    private readonly Func<string?> _loadId;
    private readonly Action<string> _saveId;
    private readonly Dictionary<string, Dictionary<string, string>> _contents = [];

    /// <param name="http">Talks to api.github.com.</param>
    /// <param name="token">A GitHub token with the gist scope.</param>
    /// <param name="loadId">The gist used before (null the first time: it is looked up, or made on the first publish).</param>
    /// <param name="saveId">Remembers the gist.</param>
    public GistRemote(HttpClient http, Func<CancellationToken, Task<string>> token, Func<string?> loadId, Action<string> saveId)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _loadId = loadId ?? throw new ArgumentNullException(nameof(loadId));
        _saveId = saveId ?? throw new ArgumentNullException(nameof(saveId));
    }

    public async Task<RemoteSnapshot?> ReadAsync(string self, string? knownVersion, CancellationToken cancellationToken)
    {
        var id = await GistIdAsync(cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return knownVersion == "none" ? null : new RemoteSnapshot([], "none");
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
        _contents.Clear();
        foreach (var (name, file) in gist["files"]?.AsObject() ?? [])
        {
            if (!name.StartsWith("device-", StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.Ordinal) || name == $"device-{self}.json" || file is null)
            {
                continue;
            }

            // Big files are cut short in the gist's JSON: the whole file is at raw_url.
            var text = file["truncated"]?.GetValue<bool>() == true && file["raw_url"]?.GetValue<string>() is { } raw
                ? await RawAsync(raw, cancellationToken).ConfigureAwait(false)
                : file["content"]?.GetValue<string>();
            if (text is null)
            {
                continue;
            }

            try
            {
                var bundle = JsonSerializer.Deserialize<Bundle>(text, Json);
                if (bundle?.Snapshot is { Schema: 1, Entries: not null } snapshot && name == $"device-{snapshot.Device}.json")
                {
                    devices.Add(snapshot);
                    _contents[snapshot.Device] = bundle.Contents ?? [];
                }
            }
            catch (JsonException)
            {
                // From a newer version, or damaged: skipped.
            }
        }

        return new RemoteSnapshot(devices, Join(response.Headers.ETag?.ToString(), Revision(gist)));
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
        var files = new JsonObject { [$"device-{snapshot.Device}.json"] = new JsonObject { ["content"] = bundle } };

        var id = await GistIdAsync(cancellationToken).ConfigureAwait(false);
        HttpRequestMessage request;
        if (id is null)
        {
            files[Marker] = new JsonObject { ["content"] = "# Todo Tracker sync\n\nTodo Tracker keeps each device's tasks here to sync them. Don't edit these files.\n" };
            request = await RequestAsync(HttpMethod.Post, "/gists", cancellationToken).ConfigureAwait(false);
            request.Content = Body(new JsonObject { ["description"] = Description, ["public"] = false, ["files"] = files });
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
        for (var page = 1; page <= 5; page++)
        {
            using var request = await RequestAsync(HttpMethod.Get, $"/gists?per_page=100&page={page}", cancellationToken).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
            var gists = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsArray();
            foreach (var gist in gists)
            {
                if (gist?["description"]?.GetValue<string>() == Description && gist["files"]?[Marker] is not null && gist["id"]?.GetValue<string>() is { } id)
                {
                    _saveId(id);
                    return id;
                }
            }

            if (gists.Count < 100)
            {
                break;
            }
        }

        return null;
    }

    private async Task<string> RawAsync(string url, CancellationToken cancellationToken)
    {
        using var request = await RequestAsync(HttpMethod.Get, url, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
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
