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
    private bool _looked;

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

        using var response = await SendAsync(HttpMethod.Patch, $"/gists/{id}", new JsonObject { ["files"] = new JsonObject { [$"device-{device}.json"] = null } }, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteSnapshot?> ReadAsync(string self, string? knownVersion, CancellationToken cancellationToken)
    {
        var id = await GistIdAsync(cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            return knownVersion == "none" ? null : new RemoteSnapshot([], "none", SelfMissing: true);
        }

        var tag = knownVersion is not null && Split(knownVersion).ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var parsed) ? parsed : null;
        using var response = await SendAsync(HttpMethod.Get, $"/gists/{id}", null, cancellationToken, tag).ConfigureAwait(false);
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
        if (id is null)
        {
            files[Marker] = new JsonObject { ["content"] = "# Todo Tracker sync\n\nTodo Tracker keeps each device's tasks here to sync them. Don't edit these files.\n" };
        }

        var body = id is null ? new JsonObject { ["description"] = _description, ["public"] = false, ["files"] = files } : new JsonObject { ["files"] = files };
        {
            using var response = await SendAsync(id is null ? HttpMethod.Post : HttpMethod.Patch, id is null ? "/gists" : $"/gists/{id}", body, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
            var gist = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
            if (id is null && gist["id"]?.GetValue<string>() is { } created)
            {
                _saveId(created);

                // Another device may have made one at the same moment: look again next time.
                _looked = false;
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
        var known = _loadId() is { Length: > 0 } saved ? saved : null;
        if (known is not null && _looked)
        {
            return known;
        }

        // A gist made earlier (on another device, or before a reinstall): found by its description and marker file.
        // Looked for once per start (and after making one): if two devices each made one at the same moment, every
        // device moves to the oldest, so they meet again.
        var found = new List<(string Id, string Created)>();
        for (var page = 1; page <= 5; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/gists?per_page=100&page={page}", null, cancellationToken).ConfigureAwait(false);
            await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
            var gists = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsArray();
            foreach (var gist in gists)
            {
                if (string.Equals(gist?["description"]?.GetValue<string>(), _description, StringComparison.OrdinalIgnoreCase) && gist!["files"]?[Marker] is not null && gist["id"]?.GetValue<string>() is { } id)
                {
                    found.Add((id, gist["created_at"]?.GetValue<string>() ?? string.Empty));
                }
            }

            if (gists.Count < 100)
            {
                break;
            }
        }

        _looked = true;
        if (found.Count == 0)
        {
            return known;
        }

        var oldest = found.OrderBy(f => f.Created, StringComparer.Ordinal).ThenBy(f => f.Id, StringComparer.Ordinal).First().Id;
        if (oldest != known)
        {
            _saveId(oldest);
        }

        return oldest;
    }

    private async Task<string> RawAsync(string url, CancellationToken cancellationToken)
    {
        // The token only ever goes to GitHub's API and gist hosts (SendAsync checks every hop).
        using var response = await SendAsync(HttpMethod.Get, url, null, cancellationToken, completion: HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaxBundleBytes * 2L)
        {
            throw new InvalidOperationException("A device's sync file is too big.");
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The only hosts the token is ever sent to (over https).</summary>
    internal static readonly string[] TokenHosts = ["api.github.com", "gist.githubusercontent.com"];

    private const int MaxRedirects = 5;

    private static bool IsTokenHost(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && TokenHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sends a request to GitHub and follows redirects by hand (at most 5, https only): the token goes only to
    /// <see cref="TokenHosts"/>, checked again at every hop, so a redirect can never carry it anywhere else. (The
    /// HttpClient given should not follow redirects itself; .NET would drop the token on its own redirects anyway.)
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken, EntityTagHeaderValue? ifNoneMatch = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var uri = path.StartsWith("https://", StringComparison.Ordinal) ? new Uri(path) : new Uri(Api + path);
        var content = body?.ToJsonString();
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, uri);
            if (IsTokenHost(uri))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(cancellationToken).ConfigureAwait(false));
            }

            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.UserAgent.ParseAdd("TodoTracker-Sync");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (ifNoneMatch is not null)
            {
                request.Headers.IfNoneMatch.Add(ifNoneMatch);
            }

            if (content is not null)
            {
                request.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }

            var response = await _http.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            {
                return response;
            }

            var status = response.StatusCode;
            response.Dispose();
            var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (hop >= MaxRedirects)
            {
                throw new HttpRequestException("GitHub redirected too many times.", null, status);
            }

            if (next.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException($"GitHub redirected to a page that isn't secure ({next.Host}).", null, status);
            }

            if (status == HttpStatusCode.SeeOther || (status is HttpStatusCode.Moved or HttpStatusCode.Found && method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
                content = null;
            }

            uri = next;
        }
    }

    private sealed record Bundle(DeviceSnapshot Snapshot, Dictionary<string, string>? Contents);
}
