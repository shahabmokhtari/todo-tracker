using System.Net;

namespace TodoTracker.Server.Tests;

public sealed class WebAssetTests : IAsyncLifetime
{
    private ServerFixture _server = null!;

    public async ValueTask InitializeAsync() => _server = await ServerFixture.StartAsync();

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Theory]
    [InlineData("/", "text/html", "Todo Tracker")]
    [InlineData("/report.html", "text/html", "report.js")]
    [InlineData("/js/app.js", "text/javascript", "refresh")]
    [InlineData("/js/format.js", "text/javascript", "relativeTime")]
    [InlineData("/css/app.css", "text/css", "--prio")]
    [InlineData("/js/icons.js", "text/javascript", "export function icon")]
    [InlineData("/img/logo.svg", "image/svg+xml", "linearGradient")]
    public async Task Embedded_web_ui_is_served(string path, string mediaType, string snippet)
    {
        var response = await _server.Client(authenticated: false).GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(snippet, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Web_ui_has_no_inline_scripts_so_csp_can_stay_strict()
    {
        foreach (var page in new[] { "/", "/report.html" })
        {
            var html = await _server.Client(authenticated: false).GetStringAsync(page);

            Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("onclick=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        }
    }
}
