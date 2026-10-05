using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

public sealed class VaultApiTests : IAsyncLifetime
{
    private ServerFixture _server = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await ServerFixture.StartAsync();
        _client = _server.Client();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private string VaultFile(params string[] parts) => Path.Combine([_server.VaultDirectory, .. parts]);

    [Fact]
    public async Task Notes_autosave_in_place_as_they_are_typed()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });
        var note = await _client.PostJson($"/api/items/{item.Id()}/notes", new { text = "deployed" });

        var updated = await (await _client.PatchAsJsonAsync($"/api/items/{item.Id()}/notes/{note.Id()}", new { text = "deployed ring 0" })).Json();
        var timeline = await _client.GetJson($"/api/items/{item.Id()}/timeline");

        Assert.Equal((note.Id(), "deployed ring 0"), (updated.Id(), updated["text"]!.GetValue<string>()));
        Assert.Contains("deployed ring 0", await File.ReadAllTextAsync(VaultFile("Work", "Ship.md")), StringComparison.Ordinal);
        Assert.Equal("deployed ring 0", timeline.AsArray().Single(t => t!["kind"]!.GetValue<string>() == "noteAdded")!["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tasks_are_stored_as_markdown_in_the_vault()
    {
        var item = await _client.PostJson("/api/items", new { title = "Durable", tags = new[] { "release" }, labels = new[] { "Deep work" } });

        var text = await File.ReadAllTextAsync(VaultFile("Work", "Durable.md"));
        Assert.Contains("# Durable", text, StringComparison.Ordinal);
        Assert.Contains("  - release", text, StringComparison.Ordinal);
        Assert.Equal("Work/Durable.md", item["file"]!.GetValue<string>());
        Assert.StartsWith("obsidian://open?path=", item["obsidianUrl"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Vault_info_lists_the_folder_problems_and_the_format_guide()
    {
        await File.WriteAllTextAsync(VaultFile("Work", "Broken.md"), "---\nid: [\n---\n# Broken\n");

        var vault = await _client.GetJson("/api/vault");

        Assert.Equal(_server.VaultDirectory, vault["path"]!.GetValue<string>());
        Assert.Equal("Work/Broken.md", vault["problems"]![0]!["path"]!.GetValue<string>());
        Assert.Contains("Todo Tracker vault", vault["guide"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("Work/Broken.md", (await _client.GetJson("/api/dashboard"))["problems"]![0]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Edits_made_in_obsidian_show_up_in_the_api()
    {
        await File.WriteAllTextAsync(VaultFile("Personal", "Groceries.md"), "# Groceries\n\n- [ ] Milk #dairy\n");

        var dashboard = await _client.GetJson("/api/dashboard");

        Assert.Contains("Milk", dashboard["now"]!.Titles());
        var milk = dashboard["now"]!.AsArray().Single(c => c!["title"]!.GetValue<string>() == "Milk")!;
        Assert.Equal("dairy", milk["tags"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Tags_and_labels_can_be_edited_and_appear_on_cards_with_colors()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });

        var patched = await (await _client.PatchAsJsonAsync($"/api/items/{item.Id()}", new { tags = new[] { "#infra", "release" }, labels = new[] { "Quick win" } })).Json();
        var card = (await _client.GetJson("/api/dashboard"))["now"]![0]!;

        Assert.Equal(["infra", "release"], patched["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal("Quick win", card["labels"]![0]!["name"]!.GetValue<string>());
        Assert.Matches("^#[0-9a-f]{6}$", card["labels"]![0]!["color"]!.GetValue<string>());
        Assert.Equal("Quick win", (await _client.GetJson("/api/labels"))[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Search_and_dashboard_filter_share_one_query_syntax()
    {
        await _client.PostJson("/api/items", new { title = "Upgrade cluster", tags = new[] { "infra/k8s" } });
        await _client.PostJson("/api/items", new { title = "Buy milk" });

        var found = await _client.GetJson("/api/search?q=%23infra");
        var filtered = await _client.GetJson("/api/dashboard?q=milk");

        Assert.Equal(["Upgrade cluster"], found.Titles());
        Assert.Equal(["Buy milk"], filtered["now"]!.Titles());
        Assert.Equal("milk", filtered["query"]!.GetValue<string>());
    }

    [Fact]
    public async Task Labels_can_be_created_renamed_recolored_and_deleted()
    {
        await _client.PostJson("/api/labels", new { name = "Later", color = "#64748b" });
        var item = await _client.PostJson("/api/items", new { title = "Ship", labels = new[] { "later" } });

        await (await _client.PatchAsJsonAsync("/api/labels/Later", new { name = "Someday", color = "#0ea5e9" })).Json();
        Assert.Equal("Someday", (await _client.GetJson($"/api/items/{item.Id()}"))["labels"]![0]!["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/labels/Someday")).StatusCode);
        Assert.Empty((await _client.GetJson($"/api/items/{item.Id()}"))["labels"]!.AsArray());
    }

    [Fact]
    public async Task Tasks_move_within_the_hierarchy()
    {
        var project = await _client.PostJson("/api/items", new { title = "Project" });
        var idea = await _client.PostJson("/api/items", new { title = "Idea" });

        var moved = await _client.PostJson($"/api/items/{idea.Id()}/move", new { parentId = project.Id(), index = 0 });
        Assert.Equal(project.Id(), moved["parentId"]!.GetValue<string>());

        var promoted = await _client.PostJson($"/api/items/{idea.Id()}/move", new { toTopLevel = true });
        Assert.Null(promoted["parentId"]);
        Assert.True(File.Exists(VaultFile("Work", "Idea.md")));
    }

    [Fact]
    public async Task Attachments_upload_download_and_delete()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent("hello"u8.ToArray());
        bytes.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(bytes, "file", "notes.txt");

        var uploaded = await (await _client.PostAsync($"/api/items/{item.Id()}/attachments", form)).Json();
        var url = uploaded["url"]!.GetValue<string>();
        using var download = await _client.GetAsync(url);

        Assert.Equal(5, uploaded["size"]!.GetValue<long>());
        Assert.Equal("hello", await download.Content.ReadAsStringAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("notes.txt", (await _client.GetJson($"/api/items/{item.Id()}"))["attachments"]![0]!["fileName"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Images_open_inline_but_html_and_svg_always_download()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });

        async Task<HttpResponseMessage> UploadAndGet(string name, string type)
        {
            using var form = new MultipartFormDataContent();
            var content = new ByteArrayContent("<svg onload=alert(1)>"u8.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue(type);
            form.Add(content, "file", name);
            var uploaded = await (await _client.PostAsync($"/api/items/{item.Id()}/attachments", form)).Json();
            return await _client.GetAsync(uploaded["url"]!.GetValue<string>());
        }

        using var png = await UploadAndGet("shot.png", "image/png");
        using var svg = await UploadAndGet("logo.svg", "image/svg+xml");
        using var html = await UploadAndGet("page.html", "text/html");

        Assert.Equal("inline", png.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("image/png", png.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", svg.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("application/octet-stream", svg.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", html.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Cookie_sessions_need_the_client_header_to_upload()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });
        var browser = _server.Client(authenticated: false);
        browser.DefaultRequestHeaders.Add("Cookie", $"{Security.CookieName}={ServerFixture.Token}");
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "x.bin" } };

        Assert.Equal(HttpStatusCode.Forbidden, (await browser.PostAsync($"/api/items/{item.Id()}/attachments", form)).StatusCode);
    }

    [Fact]
    public async Task Rich_html_is_saved_beside_the_markdown_and_served_sandboxed()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });

        await (await _client.PutAsJsonAsync($"/api/items/{item.Id()}/rich", new { html = "<h1>Plan</h1><script>alert(1)</script>" })).Json();
        using var rich = await _client.GetAsync($"/api/items/{item.Id()}/rich");

        Assert.Equal("text/html", rich.Content.Headers.ContentType!.MediaType);
        var csp = rich.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("sandbox", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", csp, StringComparison.Ordinal);
        Assert.False(rich.Headers.Contains("X-Frame-Options") && rich.Headers.GetValues("X-Frame-Options").Single() == "DENY");
        Assert.Contains("<h1>Plan</h1>", await rich.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True((await _client.GetJson($"/api/items/{item.Id()}"))["hasRich"]!.GetValue<bool>());
        Assert.True(File.Exists(VaultFile("Work", "Ship.html")));
    }

    [Fact]
    public async Task Rich_html_is_only_for_top_level_tasks()
    {
        var item = await _client.PostJson("/api/items", new { title = "Ship" });
        var step = await _client.PostJson("/api/items", new { title = "Step", parentId = item.Id() });

        var response = await _client.PutAsJsonAsync($"/api/items/{step.Id()}/rich", new { html = "<p>x</p>" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/items/{item.Id()}/rich")).StatusCode);
    }

    [Fact]
    public async Task Obsidian_vaults_on_this_machine_are_listed()
    {
        var vaults = await _client.GetJson("/api/obsidian/vaults");

        Assert.NotNull(vaults.AsArray());
    }

    [Fact]
    public async Task A_second_server_on_the_same_data_folder_is_refused()
    {
        await Assert.ThrowsAsync<StoreLockedException>(() => ServerFixture.StartAsync(o => o.DataDirectory = _server.DataDirectory));
    }
}
