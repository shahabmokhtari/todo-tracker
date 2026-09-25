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
    public async Task Launch_link_sets_strict_http_only_cookie_and_redirects()
    {
        var client = _server.App.GetTestClientWithoutRedirects();

        var response = await client.GetAsync($"/auth?token={ServerFixture.Token}");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Launch_link_with_wrong_token_is_rejected()
    {
        var response = await _server.App.GetTestClientWithoutRedirects().GetAsync("/auth?token=bad");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

    private async Task<string> LoginCookie()
    {
        var response = await _server.App.GetTestClientWithoutRedirects().GetAsync($"/auth?token={ServerFixture.Token}");
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
