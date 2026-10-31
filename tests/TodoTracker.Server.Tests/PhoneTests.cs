using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Time.Testing;
using TodoTracker.Server.Plugins;

namespace TodoTracker.Server.Tests;

/// <summary>
/// The real server on real ports (not the in-memory test server): the loopback port as always, and, when it's on, the
/// phone port with its own certificate.
/// </summary>
internal sealed class PhoneServer : IAsyncDisposable
{
    private PhoneServer(WebApplication app, TodoTrackerServerOptions options, FakeTimeProvider time)
    {
        App = app;
        Options = options;
        Time = time;
    }

    public WebApplication App { get; }

    public TodoTrackerServerOptions Options { get; }

    public FakeTimeProvider Time { get; }

    /// <summary>The certificate the phone port showed (null until a phone client connected).</summary>
    public X509Certificate2? SeenCertificate { get; private set; }

    /// <summary>A data folder with the Phones extra (and "Let phones connect") switched on or not.</summary>
    public static string DataFolder(bool plugin, bool letPhonesConnect)
    {
        var data = Directory.CreateTempSubdirectory("tt-phones-").FullName;
        if (plugin)
        {
            File.WriteAllText(Path.Combine(data, "plugins.json"), "{\"phones\": true}");
        }

        if (letPhonesConnect)
        {
            File.WriteAllText(Path.Combine(data, "phones.json"), "{\"letPhonesConnect\": true}");
        }

        return data;
    }

    public static async Task<PhoneServer> StartAsync(string data, FakeTimeProvider? time = null)
    {
        var options = new TodoTrackerServerOptions
        {
            DataDirectory = data,
            ApiToken = ServerFixture.Token,
            TimeZone = TimeZoneInfo.Utc,
            TickInterval = TimeSpan.FromHours(1),
            EnableBackgroundLoop = false,
            WatchVault = false,
            EnableHistory = false,
            Port = FreePort(IPAddress.Loopback),
            PhonePort = FreePort(IPAddress.Any),
        };
        time ??= new FakeTimeProvider(ServerFixture.T0);
        var builder = TodoTrackerHost.CreateBuilder(options);
        builder.Services.AddSingleton<TimeProvider>(time);
        var app = TodoTrackerHost.Build(builder);
        await app.StartAsync();
        return new PhoneServer(app, options, time);
    }

    private static int FreePort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>On the loopback port, with the API token (as agents and scripts use it).</summary>
    public HttpClient Local(string? bearer = ServerFixture.Token)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Options.Port}") };
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    /// <summary>The owner's session on the loopback port: opened from a link the app made itself.</summary>
    public async Task<HttpClient> Owner()
    {
        var link = new Uri(TodoTrackerHost.CreateLaunchUrl(App.Services));
        using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        using var signIn = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{Options.Port}") };
        using var response = await signIn.GetAsync(new Uri(link.PathAndQuery, UriKind.Relative));
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("tt_session=", StringComparison.Ordinal)).Split(';')[0];
        var client = Local(bearer: null);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");
        return client;
    }

    /// <summary>
    /// On the phone port over HTTPS, as a phone would: the certificate is trusted by its fingerprint (whatever the host
    /// name), and kept to compare with the fingerprint the link gives.
    /// </summary>
    public HttpClient Phone(string? bearer = null)
    {
        var pinned = App.Services.GetRequiredService<PhoneListener>().Fingerprint;
        var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            SeenCertificate = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            return SeenCertificate is not null && PhoneCertificate.Fingerprint(SeenCertificate) == pinned;
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{Options.PhonePort}") };
        if (bearer is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return client;
    }

    /// <summary>
    /// A request sent exactly as written (no client tidying the path), over TLS to the phone port; returns the status.
    /// </summary>
    public async Task<int> Raw(string method, string path, params string[] headers)
    {
        var pinned = App.Services.GetRequiredService<PhoneListener>().Fingerprint;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, Options.PhonePort);
        await using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            certificate is not null && PhoneCertificate.Base64Url(SHA256.HashData(certificate.GetRawCertData())) == pinned);
        await tls.AuthenticateAsClientAsync("todotracker");
        var host = headers.Any(h => h.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) ? string.Empty : $"Host: 192.168.1.20:{Options.PhonePort}\r\n";
        var length = headers.Any(h => h.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) ? string.Empty : "Content-Length: 0\r\n";
        var request = $"{method} {path} HTTP/1.1\r\n{host}{string.Concat(headers.Select(h => h + "\r\n"))}{length}Connection: close\r\n\r\n";
        await tls.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(tls);
        var status = await reader.ReadLineAsync() ?? string.Empty;
        return int.Parse(status.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<JsonNode> Pair()
    {
        using var owner = await Owner();
        return await owner.PostJson("/api/plugins/phones/pair");
    }

    /// <summary>Pairs a phone with that name; returns its token.</summary>
    public async Task<string> PairPhone(string name = "Shahab's iPhone")
    {
        var code = (await Pair())["code"]!.GetValue<string>();
        using var phone = Phone();
        return (await phone.PostJson("/api/phones/claim", new { code, name }))["token"]!.GetValue<string>();
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
        SeenCertificate?.Dispose();
        try
        {
            Directory.Delete(Options.DataDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Phones (the iPhone, iPad and Android apps) reach the tasks on this computer through their own port.</summary>
public sealed class PhoneTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static async Task<bool> PhonePortAnswers(PhoneServer server)
    {
        using var phone = server.Phone();
        try
        {
            using var _ = await phone.GetAsync(new Uri("/health", UriKind.Relative), Cancel);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    [Fact]
    public async Task The_phone_port_stays_closed_until_the_extra_and_let_phones_connect_are_both_on()
    {
        await using (var fresh = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: false, letPhonesConnect: false)))
        {
            using var local = fresh.Local();
            var phones = (await local.GetJson("/api/plugins")).AsArray().Single(p => p!["id"]!.GetValue<string>() == "phones")!;
            Assert.False(phones["enabled"]!.GetValue<bool>());
            Assert.False(await PhonePortAnswers(fresh));
            Assert.False(File.Exists(Path.Combine(fresh.Options.DataDirectory, "phone-certificate.pfx")));
        }

        await using (var pluginOnly = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: false)))
        {
            Assert.False(await PhonePortAnswers(pluginOnly));
        }

        await using (var accessOnly = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: false, letPhonesConnect: true)))
        {
            Assert.False(await PhonePortAnswers(accessOnly));
        }

        await using var both = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        Assert.True(await PhonePortAnswers(both));
    }

    [Fact]
    public async Task Only_you_turn_on_let_phones_connect_and_it_takes_a_restart()
    {
        var data = PhoneServer.DataFolder(plugin: true, letPhonesConnect: false);
        string kept;
        await using (var server = await PhoneServer.StartAsync(data))
        {
            using var agent = server.Local();
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.PutAsJsonAsync("/api/plugins/phones/access", new { on = true }, Cancel)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(new Uri("/api/plugins/phones", UriKind.Relative), Cancel)).StatusCode);

            using var owner = await server.Owner();
            var status = await (await owner.PutAsJsonAsync("/api/plugins/phones/access", new { on = true }, Cancel)).Json();
            Assert.True(status["letPhonesConnect"]!.GetValue<bool>());
            Assert.False(status["listening"]!.GetValue<bool>());
            Assert.True(status["restartRequired"]!.GetValue<bool>());
            Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsync(new Uri("/api/plugins/phones/pair", UriKind.Relative), null, Cancel)).StatusCode);

            // Kept for the restart (each test server cleans up its data folder, so it's carried to the next one).
            kept = await File.ReadAllTextAsync(Path.Combine(data, "phones.json"), Cancel);
        }

        var again = PhoneServer.DataFolder(plugin: true, letPhonesConnect: false);
        await File.WriteAllTextAsync(Path.Combine(again, "phones.json"), kept, Cancel);
        await using var restarted = await PhoneServer.StartAsync(again);
        using var you = await restarted.Owner();
        var after = await you.GetJson("/api/plugins/phones");
        Assert.True(after["listening"]!.GetValue<bool>());
        Assert.False(after["restartRequired"]!.GetValue<bool>());
        Assert.Equal(restarted.Options.PhonePort, after["port"]!.GetValue<int>());
        Assert.True(await PhonePortAnswers(restarted));
    }

    [Fact]
    public async Task A_phone_pairs_with_the_code_from_the_link_once_and_its_changes_say_which_phone()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var pairing = await server.Pair();
        var link = new Uri(pairing["link"]!.GetValue<string>());
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        var code = pairing["code"]!.GetValue<string>();

        Assert.Equal("todotracker", link.Scheme);
        Assert.Equal("pair", link.Host);
        Assert.Equal("1", query["v"]);
        Assert.Equal(code, query["code"]);
        Assert.Equal(pairing["computer"]!.GetValue<string>(), query["name"]);
        Assert.Equal(pairing["fingerprint"]!.GetValue<string>(), query["fp"]);
        Assert.All(query["hosts"]!.Split(',', StringSplitOptions.RemoveEmptyEntries), h => Assert.EndsWith($":{server.Options.PhonePort}", h, StringComparison.Ordinal));
        Assert.StartsWith("<svg", pairing["qrSvg"]!.GetValue<string>().TrimStart(), StringComparison.Ordinal);
        Assert.True(DateTimeOffset.Parse(pairing["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) - ServerFixture.T0 == TimeSpan.FromMinutes(10));

        using var phone = server.Phone();
        var claimed = await phone.PostJson("/api/phones/claim", new { code, name = "Shahab's iPhone" });
        var token = claimed["token"]!.GetValue<string>();
        Assert.StartsWith("ttp_", token, StringComparison.Ordinal);
        Assert.Equal(pairing["computer"]!.GetValue<string>(), claimed["computer"]!.GetValue<string>());

        // The certificate the phone saw is the one the link vouches for.
        var seen = Convert.ToBase64String(SHA256.HashData(server.SeenCertificate!.RawData)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(query["fp"], seen);

        // Single use.
        var reused = await phone.PostAsJsonAsync("/api/phones/claim", new { code, name = "Someone else" }, Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
        Assert.Contains("Settings › Phones", await reused.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);

        // The window that showed the code sees the phone.
        using var owner = await server.Owner();
        var status = await owner.GetJson($"/api/plugins/phones/pair?id={pairing["id"]!.GetValue<string>()}");
        Assert.Equal(("paired", "Shahab's iPhone"), (status["state"]!.GetValue<string>(), status["phone"]!.GetValue<string>()));

        using var paired = server.Phone(token);
        var item = await paired.PostJson("/api/capture", new { text = "Buy milk" });
        var timeline = (await owner.GetJson("/api/timeline")).AsArray();
        var created = timeline.First(t => t!["itemId"]?.GetValue<string>() == item.Id())!;
        Assert.Equal("user", created["actorKind"]!.GetValue<string>());
        Assert.Equal("You (Shahab's iPhone)", created["actor"]!.GetValue<string>());

        // The whole board, with an ETag: unchanged is "304 Not Modified".
        using var export = await paired.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var unchanged = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/export", UriKind.Relative));
        unchanged.Headers.IfNoneMatch.Add(export.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await paired.SendAsync(unchanged, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await paired.GetAsync(new Uri("/health", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public async Task Phone_tokens_work_only_on_the_phone_port_and_nothing_else_works_there()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var token = await server.PairPhone();

        using var onLoopback = server.Local(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await onLoopback.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);

        using var mainToken = server.Phone(ServerFixture.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await mainToken.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);

        // The owner's own session cookie doesn't open the phone port either.
        using var owner = await server.Owner();
        using var withCookie = server.Phone();
        withCookie.DefaultRequestHeaders.Add("Cookie", owner.DefaultRequestHeaders.GetValues("Cookie").Single());
        withCookie.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");
        Assert.Equal(HttpStatusCode.Unauthorized, (await withCookie.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);

        using var nothing = server.Phone();
        Assert.Equal(HttpStatusCode.Unauthorized, (await nothing.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);

        // Any host name: phones reach it by the computer's address on the network.
        using var lan = server.Phone(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/settings", UriKind.Relative));
        request.Headers.Host = $"192.168.1.20:{server.Options.PhonePort}";
        Assert.Equal(HttpStatusCode.OK, (await lan.SendAsync(request, Cancel)).StatusCode);

        // The claim is for the phone port alone.
        using var agent = server.Local();
        Assert.Equal(HttpStatusCode.NotFound, (await agent.PostAsJsonAsync("/api/phones/claim", new { code = "x" }, Cancel)).StatusCode);
    }

    [Theory]
    [InlineData("PUT", "/api/settings/vault")]
    [InlineData("PUT", "/api/settings")]
    [InlineData("GET", "/api/history")]
    [InlineData("POST", "/mcp")]
    [InlineData("GET", "/api/plugins")]
    [InlineData("PUT", "/api/plugins/phones/access")]
    [InlineData("GET", "/api/plugins/phones/devices")]
    [InlineData("GET", "/api/connection")]
    [InlineData("POST", "/api/launch")]
    [InlineData("GET", "/api/dashboard")]
    [InlineData("POST", "/api/hooks")]
    [InlineData("POST", "/api/archive")]
    [InlineData("POST", "/api/items/{id}/attachments")]
    [InlineData("GET", "/api/items/{id}/rich")]
    [InlineData("HEAD", "/api/export")]
    [InlineData("GET", "/")]
    [InlineData("GET", "/index.html")]
    [InlineData("GET", "/api/some-future-route")]
    public async Task A_phone_reaches_only_what_the_phone_apps_do(string method, string path)
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        using var phone = server.Phone(await server.PairPhone());
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal), UriKind.Relative))
        {
            Content = method is "GET" or "HEAD" ? null : JsonContent.Create(new { path = "/tmp/x", on = true }),
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await phone.SendAsync(request, Cancel)).StatusCode);
    }

    [Fact]
    public async Task A_phone_does_what_the_phone_apps_do()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        using var phone = server.Phone(await server.PairPhone());
        var group = await phone.PostJson("/api/groups", new { name = "Home" });
        var item = await phone.PostJson("/api/items", new { title = "Water plants", groupId = group.Id() });
        var other = await phone.PostJson("/api/capture", new { text = "Call mum" });
        var id = item.Id();

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync(new Uri("/api/settings", UriKind.Relative), Cancel)).StatusCode);
        (await phone.PutAsJsonAsync("/api/settings/theme", new { theme = "dark" }, Cancel)).EnsureSuccessStatusCode();
        (await phone.PatchAsJsonAsync($"/api/items/{id}", new { title = "Water the plants" }, Cancel)).EnsureSuccessStatusCode();
        await phone.PostOk($"/api/items/{id}/notes", new { text = "The big one too" });
        await phone.PostOk($"/api/items/{id}/steps", new { titles = new[] { "Fill the can" } });
        var reminder = await phone.PostJson($"/api/items/{id}/reminders", new { at = ServerFixture.T0.AddHours(1) });
        await phone.PostOk($"/api/items/{id}/reminders/{reminder.Id()}/dismiss", new { });
        await phone.PostOk($"/api/items/{id}/schedule", new { rule = "tomorrow" });
        await phone.PostOk($"/api/items/{id}/schedule", new { clear = true });
        await phone.PostOk($"/api/items/{id}/reorder", new { before = (string?)null });
        await phone.PostOk("/api/now/order", new { ids = new[] { other.Id(), id } });
        await phone.PostOk("/api/now/first", new { ids = new[] { id } });
        await phone.PostOk($"/api/items/{id}/move", new { groupId = group.Id() });
        // The Board and Tasks views on an iPad: a card to another column, a task into another and back out after it.
        await phone.PostOk($"/api/items/{id}/stage", new { stage = "doing" });
        await phone.PostOk($"/api/items/{other.Id()}/move", new { parentId = id });
        await phone.PostOk($"/api/items/{other.Id()}/move", new { toTopLevel = true, groupId = group.Id() });
        await phone.PostOk($"/api/items/{other.Id()}/reorder", new { after = id });
        await phone.PostOk($"/api/items/{id}/complete", new { });
        await phone.PostOk($"/api/items/{id}/reopen", new { });
        await phone.PostOk($"/api/items/{id}/complete", new { });
        await phone.PostOk($"/api/items/{id}/archive", new { });
        await phone.PostOk($"/api/items/{id}/unarchive", new { });
        await phone.PostOk($"/api/items/{id}/reopen", new { });
        await phone.PostOk("/api/pomodoro/start", new { itemId = id });
        await phone.PostOk("/api/pomodoro/reset", new { });
        await phone.PostOk("/api/timer/start", new { itemId = id });
        await phone.PostOk("/api/timer/stop", new { });
        await phone.PostOk($"/api/items/{id}/plan", new { start = ServerFixture.T0.AddHours(2), minutes = 45 });
        await phone.PostOk($"/api/items/{id}/plan", new { });
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync(new Uri("/api/energy", UriKind.Relative), Cancel)).StatusCode);
        (await phone.PutAsJsonAsync("/api/energy", new { perDay = 12 }, Cancel)).EnsureSuccessStatusCode();
        (await phone.PatchAsJsonAsync($"/api/groups/{group.Id()}", new { name = "House" }, Cancel)).EnsureSuccessStatusCode();
        (await phone.DeleteAsync(new Uri($"/api/items/{other.Id()}", UriKind.Relative), Cancel)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_code_lasts_ten_minutes_and_wrong_guesses_are_slowed_down()
    {
        var time = new FakeTimeProvider(ServerFixture.T0);
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true), time);
        using var phone = server.Phone();

        var stale = (await server.Pair())["code"]!.GetValue<string>();
        time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/phones/claim", new { code = stale, name = "Late" }, Cancel)).StatusCode);

        // A newer code replaces the one shown before.
        var first = (await server.Pair())["code"]!.GetValue<string>();
        var code = (await server.Pair())["code"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/phones/claim", new { code = first }, Cancel)).StatusCode);

        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/phones/claim", new { code = $"guess-{i}" }, Cancel)).StatusCode);
        }

        // Ten wrong tries from this address in a minute (two above, eight here): this address waits a minute, even with
        // the right code (another address isn't held up: see PhonePairingTests), and the code still works after.
        var limited = await phone.PostAsJsonAsync("/api/phones/claim", new { code }, Cancel);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.StartsWith("ttp_", (await phone.PostJson("/api/phones/claim", new { code, name = "On time" }))["token"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET", "/API/EXPORT", 200)]
    [InlineData("GET", "/api/export/", 200)]
    [InlineData("GET", "/API/HISTORY", 403)]
    [InlineData("GET", "/api/history/", 403)]
    [InlineData("GET", "//api/export", 403)]
    [InlineData("GET", "/api//export", 403)]
    [InlineData("PUT", "/api/settings%2Fvault", 403)]
    [InlineData("PUT", "/api/settings%2ftheme", 403)]
    [InlineData("GET", "/api/items/../history", 403)]
    [InlineData("GET", "/api/export/../history", 403)]
    [InlineData("GET", "/api/export%0A", 403)]
    [InlineData("GET", "/api/settings%0A", 403)]
    public async Task Odd_paths_reach_only_what_they_would_reach_written_plainly(string method, string path, int status)
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var token = await server.PairPhone();

        Assert.Equal(status, await server.Raw(method, path, $"Authorization: Bearer {token}"));
    }

    [Fact]
    public async Task Other_credentials_and_tricks_dont_work_on_the_phone_port()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var token = await server.PairPhone();
        var bearer = $"Authorization: Bearer {token}";

        // Asking for another method doesn't change the one checked.
        Assert.Equal(403, await server.Raw("POST", "/api/history", bearer, "X-HTTP-Method-Override: GET"));
        Assert.Equal(403, await server.Raw("POST", "/api/settings/vault", bearer, "X-HTTP-Method-Override: PUT"));
        Assert.Equal(403, await server.Raw("GET", $"/api/items/{Guid.NewGuid()}", bearer, "X-HTTP-Method-Override: PATCH"));

        // "localhost" as the host name doesn't make it the loopback port: the main token still doesn't work.
        Assert.Equal(401, await server.Raw("GET", "/api/export", $"Authorization: Bearer {ServerFixture.Token}", "Host: localhost"));
        Assert.Equal(200, await server.Raw("GET", "/api/export", bearer, "Host: localhost"));

        // A paired browser extension's token is for the loopback port.
        using var app = server.Local();
        var browserCode = (await app.PostJson("/api/plugins/browser-extension/pair"))["code"]!.GetValue<string>();
        using var extension = server.Local(bearer: null);
        extension.DefaultRequestHeaders.Add("X-TodoTracker-Client", "browser-extension");
        var browserToken = (await extension.PostJson("/api/plugins/browser-extension/claim", new { code = browserCode }))["token"]!.GetValue<string>();
        Assert.StartsWith("ttb_", browserToken, StringComparison.Ordinal);
        Assert.Equal(401, await server.Raw("POST", "/api/capture", $"Authorization: Bearer {browserToken}"));
    }

    [Fact]
    public async Task The_owners_phone_routes_need_the_apps_own_header_with_the_cookie()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        using var owner = await server.Owner();
        using var forged = server.Local(bearer: null);
        forged.DefaultRequestHeaders.Add("Cookie", owner.DefaultRequestHeaders.GetValues("Cookie").Single());

        Assert.Equal(HttpStatusCode.Forbidden, (await forged.PostAsync(new Uri("/api/plugins/phones/pair", UriKind.Relative), null, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.PutAsJsonAsync("/api/plugins/phones/access", new { on = false }, Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await forged.DeleteAsync(new Uri("/api/plugins/phones/devices/x", UriKind.Relative), Cancel)).StatusCode);
        Assert.True((await owner.GetJson("/api/plugins/phones"))["letPhonesConnect"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_right_code_claimed_twice_at_once_pairs_one_phone()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var code = (await server.Pair())["code"]!.GetValue<string>();
        var phones = Enumerable.Range(0, 4).Select(_ => server.Phone()).ToList();

        var answers = await Task.WhenAll(phones.Select((p, i) => p.PostAsJsonAsync("/api/phones/claim", new { code, name = $"Phone {i}" }, Cancel)));

        Assert.Single(answers, a => a.StatusCode == HttpStatusCode.OK);
        Assert.All(answers.Where(a => a.StatusCode != HttpStatusCode.OK), a => Assert.Equal(HttpStatusCode.Forbidden, a.StatusCode));
        using var owner = await server.Owner();
        Assert.Single((await owner.GetJson("/api/plugins/phones/devices")).AsArray());
        phones.ForEach(p => p.Dispose());
    }

    [Fact]
    public async Task A_claim_while_phones_are_off_is_refused()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var code = (await server.Pair())["code"]!.GetValue<string>();
        using var owner = await server.Owner();
        using var phone = server.Phone();

        (await owner.PutAsJsonAsync("/api/plugins/phones/access", new { on = false }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/phones/claim", new { code, name = "x" }, Cancel)).StatusCode);
        (await owner.PutAsJsonAsync("/api/plugins/phones/access", new { on = true }, Cancel)).EnsureSuccessStatusCode();
        (await owner.PutAsJsonAsync("/api/plugins/phones", new { enabled = false }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync("/api/phones/claim", new { code, name = "x" }, Cancel)).StatusCode);

        // Neither used the code up.
        (await owner.PutAsJsonAsync("/api/plugins/phones", new { enabled = true }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync("/api/phones/claim", new { code, name = "x" }, Cancel)).StatusCode);
    }

    [Fact]
    public async Task A_phone_that_couldnt_be_saved_hears_try_again_and_the_code_still_works()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var code = (await server.Pair())["code"]!.GetValue<string>();
        using var phone = server.Phone();

        // Something in the way of saving the list of phones (here: a folder where its temporary file goes).
        var blocker = Path.Combine(server.Options.DataDirectory, "paired-phones.json.tmp");
        Directory.CreateDirectory(blocker);
        var failed = await phone.PostAsJsonAsync("/api/phones/claim", new { code, name = "iPad" }, Cancel);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Contains("try again", await failed.Content.ReadAsStringAsync(Cancel), StringComparison.OrdinalIgnoreCase);

        Directory.Delete(blocker);
        Assert.StartsWith("ttp_", (await phone.PostJson("/api/phones/claim", new { code, name = "iPad" }))["token"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Big_requests_are_refused_and_long_names_are_cut()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var code = (await server.Pair())["code"]!.GetValue<string>();
        using var phone = server.Phone();

        // Only the headers go out: the size said up front is refused before any of the body is read. (Sending the body
        // too races the server closing the connection, which Windows reports as a reset rather than the 413.)
        Assert.Equal(413, await server.Raw("POST", "/api/phones/claim", "Content-Type: application/json", $"Content-Length: {8 * 1024}"));

        var token = (await phone.PostJson("/api/phones/claim", new { code, name = new string('n', 200) }))["token"]!.GetValue<string>();
        using var owner = await server.Owner();
        Assert.Equal(60, (await owner.GetJson("/api/plugins/phones/devices")).AsArray()[0]!["name"]!.GetValue<string>().Length);

        var bearer = $"Authorization: Bearer {token}";
        Assert.Equal(413, await server.Raw("POST", "/api/capture", bearer, "Content-Type: application/json", $"Content-Length: {2 * 1024 * 1024}"));
    }

    [Theory]
    [InlineData("8.8.8.8", 403)]
    [InlineData("2001:4860:4860::8888", 403)]
    [InlineData("::ffff:8.8.8.8", 403)]
    [InlineData("192.168.1.30", 401)]
    [InlineData("::ffff:192.168.1.30", 401)]
    [InlineData("100.100.1.2", 401)]
    public async Task A_request_from_outside_this_network_and_tailscale_is_refused(string remote, int status)
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var listener = server.App.Services.GetRequiredService<PhoneListener>();
        using var scope = server.App.Services.CreateScope();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Connection.LocalPort = listener.Port;
        context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
        context.Request.Method = "GET";
        context.Request.Path = "/api/export";
        context.Response.Body = new MemoryStream();
        var reached = false;

        await Security.Guard(context, _ => { reached = true; return Task.CompletedTask; }, server.Options, new ApiToken(ServerFixture.Token));

        // Outside: refused before anything else; on the network: it gets as far as asking for a phone's token.
        Assert.Equal(status, context.Response.StatusCode);
        Assert.False(reached);
    }

    [Fact]
    public async Task Disconnecting_a_phone_or_turning_phones_off_shuts_it_out_at_once()
    {
        await using var server = await PhoneServer.StartAsync(PhoneServer.DataFolder(plugin: true, letPhonesConnect: true));
        var ipad = await server.PairPhone("iPad");
        var pixel = await server.PairPhone("Pixel");
        using var owner = await server.Owner();

        var devices = (await owner.GetJson("/api/plugins/phones/devices")).AsArray();
        Assert.Equal(["iPad", "Pixel"], devices.Select(d => d!["name"]!.GetValue<string>()));
        Assert.DoesNotContain("ttp_", devices.ToJsonString(), StringComparison.Ordinal);
        using var agent = server.Local();
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync(new Uri("/api/plugins/phones/devices", UriKind.Relative), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.DeleteAsync(new Uri($"/api/plugins/phones/devices/{devices[0]!["id"]!.GetValue<string>()}", UriKind.Relative), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.PostAsync(new Uri("/api/plugins/phones/pair", UriKind.Relative), null, Cancel)).StatusCode);

        (await owner.DeleteAsync(new Uri($"/api/plugins/phones/devices/{devices[0]!["id"]!.GetValue<string>()}", UriKind.Relative), Cancel)).EnsureSuccessStatusCode();
        using var disconnected = server.Phone(ipad);
        using var still = server.Phone(pixel);
        Assert.Equal(HttpStatusCode.Unauthorized, (await disconnected.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await still.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);

        // "Let phones connect" off: no phone gets in, before any restart (the port itself closes at the next start).
        var off = await (await owner.PutAsJsonAsync("/api/plugins/phones/access", new { on = false }, Cancel)).Json();
        Assert.False(off["letPhonesConnect"]!.GetValue<bool>());
        var refused = await still.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("Let phones connect", await refused.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
        (await owner.PutAsJsonAsync("/api/plugins/phones/access", new { on = true }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await still.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);

        // The Phones extra off: the same.
        (await owner.PutAsJsonAsync("/api/plugins/phones", new { enabled = false }, Cancel)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await still.GetAsync(new Uri("/api/export", UriKind.Relative), Cancel)).StatusCode);
    }

    [Fact]
    public void The_link_lists_this_computers_network_and_tailscale_addresses_and_names_it()
    {
        IPAddress[] found =
        [
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("100.101.102.103"),
            IPAddress.Parse("192.168.1.20"),
            IPAddress.Parse("169.254.10.1"),
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("10.0.0.7"),
            IPAddress.Parse("172.20.1.1"),
            IPAddress.Parse("172.32.1.1"),
            IPAddress.Parse("100.128.0.1"),
            IPAddress.Parse("fe80::1"),
            IPAddress.Parse("192.168.1.20"),
        ];

        Assert.Equal(["192.168.1.20", "10.0.0.7", "172.20.1.1", "100.101.102.103"], PhoneLink.Pick(found).Select(a => a.ToString()));
        Assert.Equal(
            "todotracker://pair?v=1&hosts=192.168.1.20:5318,100.101.102.103:5318&code=abc_-123&fp=Zm9v-_&name=Shahab%27s%20Mac%20%26%20more",
            PhoneLink.Build(["192.168.1.20:5318", "100.101.102.103:5318"], "abc_-123", "Zm9v-_", "Shahab's Mac & more"));
    }

    [Fact]
    public void The_qr_code_is_an_svg_of_the_link()
    {
        var svg = PhoneLink.Qr("todotracker://pair?v=1&code=x");

        Assert.StartsWith("<svg", svg.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("viewBox", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", svg, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(svg, PhoneLink.Qr("todotracker://pair?v=1&code=y"));
    }

    [Fact]
    public void The_certificate_is_made_once_and_kept_private()
    {
        var data = Directory.CreateTempSubdirectory("tt-phone-cert-").FullName;
        try
        {
            using var first = PhoneCertificate.LoadOrCreate(data);
            using var again = PhoneCertificate.LoadOrCreate(data);

            Assert.True(first.HasPrivateKey);
            Assert.Equal(PhoneCertificate.Fingerprint(first), PhoneCertificate.Fingerprint(again));
            Assert.Matches("^[A-Za-z0-9_-]{43}$", PhoneCertificate.Fingerprint(first));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(data, PhoneCertificate.FileName)));
            }
        }
        finally
        {
            Directory.Delete(data, recursive: true);
        }
    }
}
