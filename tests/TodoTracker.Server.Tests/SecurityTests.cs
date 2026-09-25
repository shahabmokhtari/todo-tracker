using System.Net;
using System.Net.Http.Json;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

public sealed class SecurityTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await _server.Client(authenticated: false).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/items")]
    [InlineData("/api/export")]
    public async Task Api_requires_authentication(string url)
    {
        var response = await _server.Client(authenticated: false).GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_bearer_token_is_rejected()
    {
        var client = _server.Client(authenticated: false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Mcp_requires_authentication()
    {
        var response = await _server.Client(authenticated: false).PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Non_loopback_host_header_is_rejected_to_stop_dns_rebinding()
    {
        var client = _server.Client();
        client.DefaultRequestHeaders.Host = "evil.example:5317";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Launch_code_sets_strict_http_only_cookie_and_redirects()
    {
        var launch = await LaunchPath("/");

        var response = await _server.App.GetTestClientWithoutRedirects().GetAsync(launch);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Launch_codes_are_single_use_and_never_contain_the_api_token()
    {
        // Review finding: the long-lived API token was put in URLs and therefore in (synced) browser history.
        var launch = await LaunchPath("/");
        var client = _server.App.GetTestClientWithoutRedirects();

        Assert.DoesNotContain(ServerFixture.Token, launch, StringComparison.Ordinal);
        Assert.True((await client.GetAsync(launch)).Headers.Contains("Set-Cookie"));
        AssertGrantsNoSession(await client.GetAsync(launch));
    }

    [Fact]
    public async Task Launch_codes_expire()
    {
        var launch = await LaunchPath("/");
        _server.Time.Advance(TimeSpan.FromMinutes(3));

        AssertGrantsNoSession(await _server.App.GetTestClientWithoutRedirects().GetAsync(launch));
    }

    [Fact]
    public async Task Auth_no_longer_accepts_the_raw_token()
    {
        AssertGrantsNoSession(await _server.App.GetTestClientWithoutRedirects().GetAsync($"/auth?token={ServerFixture.Token}"));
    }

    [Fact]
    public async Task Launch_endpoint_requires_authentication()
    {
        var response = await _server.Client(authenticated: false).PostAsJsonAsync("/api/launch", new { @return = "/" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/?item=abc", "/?item=abc")]
    [InlineData("/report.html?id=1", "/report.html?id=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("/\t/evil.example", "/")]
    [InlineData("/\n/evil.example", "/")]
    public async Task Launch_link_only_returns_to_local_paths(string returnTo, string expected)
    {
        var response = await _server.App.GetTestClientWithoutRedirects().GetAsync(await LaunchPath(returnTo));

        Assert.Equal(expected, response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Launch_link_with_unknown_code_grants_nothing_but_lands_on_the_page()
    {
        // Review follow-up: a stale link (double click, slow browser start) should show the page, not a bare 401.
        AssertGrantsNoSession(await _server.App.GetTestClientWithoutRedirects().GetAsync("/auth?code=bad"));
    }

    private static void AssertGrantsNoSession(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Cookie_session_can_read_but_mutations_need_the_client_header()
    {
        var cookie = await LoginCookie();
        var client = _server.Client(authenticated: false);
        client.DefaultRequestHeaders.Add("Cookie", cookie);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/items", new { title = "x" })).StatusCode);

        client.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/items", new { title = "x" })).StatusCode);
    }

    [Fact]
    public async Task Login_endpoint_accepts_pasted_token()
    {
        var client = _server.Client(authenticated: false);
        client.DefaultRequestHeaders.Add("X-TodoTracker-Client", "web");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/login", new { token = "bad" })).StatusCode);
        var ok = await client.PostAsJsonAsync("/api/login", new { token = ServerFixture.Token });

        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Contains(ok.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tt_session=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Pages_send_strict_security_headers()
    {
        var response = await _server.Client(authenticated: false).GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = string.Join(';', response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Token_is_generated_and_persisted_when_not_configured()
    {
        await using var server = await ServerFixture.StartAsync(o => o.ApiToken = null);

        var tokenFile = Path.Combine(server.DataDirectory, "api-token");
        Assert.True(File.Exists(tokenFile));
        var token = (await File.ReadAllTextAsync(tokenFile)).Trim();
        Assert.True(token.Length >= 40);
        var client = server.Client(authenticated: false);
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/dashboard")).StatusCode);
    }

    private async Task<string> LaunchPath(string returnTo)
    {
        var body = await _server.Client().PostJson("/api/launch", new { @return = returnTo });
        var url = new Uri(body["url"]!.GetValue<string>());
        return url.PathAndQuery;
    }

    private async Task<string> LoginCookie()
    {
        var response = await _server.App.GetTestClientWithoutRedirects().GetAsync(await LaunchPath("/"));
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }
}

internal static class TestClientExtensions
{
    public static HttpClient GetTestClientWithoutRedirects(this Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var server = (Microsoft.AspNetCore.TestHost.TestServer)app.Services.GetService(typeof(Microsoft.AspNetCore.Hosting.Server.IServer))!;
        return new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };
    }
}
