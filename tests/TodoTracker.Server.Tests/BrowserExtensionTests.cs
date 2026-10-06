using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace TodoTracker.Server.Tests;

/// <summary>Setting up the browser extension: pairing codes instead of copying a token, and the extension to download.</summary>
public sealed class BrowserExtensionTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync();

    public ValueTask DisposeAsync() => _server.DisposeAsync();

    private HttpClient Extension()
    {
        var client = _server.Client(authenticated: false);
        client.DefaultRequestHeaders.Add("X-TodoTracker-Client", "browser-extension");
        return client;
    }

    private async Task<string> NewCode()
    {
        using var client = _server.Client();
        var response = await client.PostAsync(new Uri("/api/plugins/browser-extension/pair", UriKind.Relative), null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["code"]!.GetValue<string>();
    }

    private static Task<HttpResponseMessage> Claim(HttpClient client, string code) =>
        client.PostAsJsonAsync(new Uri("/api/plugins/browser-extension/claim", UriKind.Relative), new { code }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_pairing_code_is_traded_once_for_the_token()
    {
        var code = await NewCode();
        using var extension = Extension();

        using var first = await Claim(extension, code);
        using var again = await Claim(extension, code);

        Assert.Matches("^[0-9]{6}$", code);
        Assert.Equal(ServerFixture.Token, JsonNode.Parse(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["token"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task Guessing_voids_the_code_and_web_pages_can_not_claim()
    {
        var code = await NewCode();
        using var extension = Extension();
        var wrong = code == "000000" ? "111111" : "000000";
        for (var i = 0; i < 5; i++)
        {
            using var guess = await Claim(extension, wrong);
            Assert.Equal(HttpStatusCode.BadRequest, guess.StatusCode);
        }

        // After 5 wrong tries even the right code no longer works: a new one has to be made.
        using var right = await Claim(extension, code);
        Assert.Equal(HttpStatusCode.BadRequest, right.StatusCode);

        // A page in the browser can't send the custom header without the server's permission.
        using var page = _server.Client(authenticated: false);
        using var fromPage = await Claim(page, await NewCode());
        Assert.Equal(HttpStatusCode.Forbidden, fromPage.StatusCode);
    }

    [Fact]
    public async Task Codes_expire_and_only_signed_in_callers_can_make_one()
    {
        var code = await NewCode();
        _server.Time.Advance(TimeSpan.FromMinutes(6));
        using var extension = Extension();
        using var expired = await Claim(extension, code);
        using var anonymous = _server.Client(authenticated: false);
        using var make = await anonymous.PostAsync(new Uri("/api/plugins/browser-extension/pair", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, make.StatusCode);
    }

    [Theory]
    [InlineData("chromium", "side_panel")]
    [InlineData("safari", "default_popup")]
    public async Task The_extension_downloads_ready_to_load_for_each_browser(string browser, string marker)
    {
        using var client = _server.Client();
        using var response = await client.GetAsync(new Uri($"/api/plugins/browser-extension/download/{browser}", UriKind.Relative), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken);
        using var zip = new ZipArchive(stream);
        var names = zip.Entries.Select(e => e.FullName).ToList();

        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("manifest.json", names);
        Assert.Contains("sidepanel.html", names);
        Assert.Contains("lib/client.js", names);
        Assert.Contains("icons/icon128.png", names);
        Assert.DoesNotContain(names, n => n.StartsWith("tests/", StringComparison.Ordinal) || n.StartsWith("safari/", StringComparison.Ordinal));
        using var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        Assert.Contains(marker, await manifest.ReadToEndAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_browsers_are_refused()
    {
        using var client = _server.Client();
        using var response = await client.GetAsync(new Uri("/api/plugins/browser-extension/download/firefox", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
