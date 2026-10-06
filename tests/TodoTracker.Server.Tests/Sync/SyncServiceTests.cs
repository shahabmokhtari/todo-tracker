using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;
using TodoTracker.Core.Vault;
using TodoTracker.Server.Plugins.Sync;

namespace TodoTracker.Server.Tests.Sync;

public sealed class CloudDetectionTests
{
    private static CloudEnvironment Windows(Dictionary<string, string?> vars, params string[] dirs) =>
        new(name => vars.GetValueOrDefault(name), @"C:\Users\me", d => dirs.Contains(d, StringComparer.OrdinalIgnoreCase), _ => [], IsWindows: true, IsMac: false);

    [Fact]
    public void OneDrive_for_work_or_school_comes_before_personal()
    {
        var env = Windows(new() { ["OneDrive"] = @"C:\Users\me\OneDrive", ["OneDriveConsumer"] = @"C:\Users\me\OneDrive", ["OneDriveCommercial"] = @"C:\Users\me\OneDrive - Contoso" }, @"C:\Users\me\OneDrive", @"C:\Users\me\OneDrive - Contoso");

        var found = env.OneDrive();

        Assert.Equal([@"C:\Users\me\OneDrive - Contoso", @"C:\Users\me\OneDrive"], found.Select(f => f.Root));
        Assert.Equal("work or school", found[0].Account);
    }

    [Fact]
    public void On_a_mac_onedrive_and_icloud_are_found_in_the_users_library()
    {
        var home = "/Users/me";
        var cloud = Path.Combine(home, "Library", "CloudStorage");
        var icloud = Path.Combine(home, "Library", "Mobile Documents", "com~apple~CloudDocs");
        var dirs = new[] { Path.Combine(cloud, "OneDrive-Personal"), Path.Combine(cloud, "OneDrive-Contoso"), icloud };
        var env = new CloudEnvironment(_ => null, home, dirs.Contains, d => d == cloud ? dirs[..2] : [], IsWindows: false, IsMac: true);

        Assert.Equal([dirs[1], dirs[0]], env.OneDrive().Select(f => f.Root));
        Assert.Equal(icloud, env.ICloud()!.Root);
    }

    [Fact]
    public void A_tasks_folder_already_in_onedrive_is_not_synced_again_through_it()
    {
        var env = Windows(new() { ["OneDriveCommercial"] = @"C:\Users\me\OneDrive - Contoso" }, @"C:\Users\me\OneDrive - Contoso");
        var provider = new OneDriveSyncProvider(env);

        var inside = provider.Check(@"C:\Users\me\OneDrive - Contoso\Documents\Todo Tracker");
        var outside = provider.Check(@"C:\Users\me\Todo Tracker");

        Assert.False(inside.Available);
        Assert.Contains("already in OneDrive", inside.Detail, StringComparison.Ordinal);
        Assert.True(outside.Available);
        Assert.Equal(@"C:\Users\me\OneDrive - Contoso\Apps\TodoTrackerSync", outside.Detail);
    }

    [Fact]
    public void Without_a_cloud_drive_the_providers_say_so()
    {
        var env = Windows([]);

        Assert.False(new OneDriveSyncProvider(env).Check(@"C:\Tasks").Available);
        Assert.False(new ICloudSyncProvider(env).Check(@"C:\Tasks").Available);
    }

    [Fact]
    public void The_gist_needs_a_github_sign_in()
    {
        var dir = Directory.CreateTempSubdirectory("tt-gist-").FullName;
        try
        {
            var options = new TodoTrackerServerOptions { DataDirectory = dir };
            Assert.False(new GistSyncProvider(options) { FindToken = () => null }.Check(dir).Available);
            Assert.True(new GistSyncProvider(options) { FindToken = () => "token" }.Check(dir).Available);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>Two app instances (two "computers") sharing one fake OneDrive folder.</summary>
public sealed class SyncApiTests : IAsyncLifetime
{
    private readonly string _cloud = Directory.CreateTempSubdirectory("tt-onedrive-").FullName;
    private ServerFixture _laptop = null!;
    private ServerFixture _desktop = null!;

    public async ValueTask InitializeAsync()
    {
        _laptop = await Start();
        _desktop = await Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _laptop.DisposeAsync();
        await _desktop.DisposeAsync();
        Directory.Delete(_cloud, recursive: true);
    }

    private Task<ServerFixture> Start() => ServerFixture.StartAsync(services: s => s.AddSingleton(
        new CloudEnvironment(name => name == "OneDriveCommercial" ? _cloud : null, Path.GetTempPath(), Directory.Exists, _ => [], IsWindows: true, IsMac: false)));

    private static async Task<JsonNode> Sync(ServerFixture app)
    {
        using var client = app.Client();
        var response = await client.PostAsync(new Uri("/api/plugins/sync/now", UriKind.Relative), null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<JsonNode> View(ServerFixture app)
    {
        using var client = app.Client();
        return JsonNode.Parse(await client.GetStringAsync(new Uri("/api/plugins/sync", UriKind.Relative), TestContext.Current.CancellationToken))!;
    }

    private static Task<List<string>> Titles(ServerFixture app) =>
        ((VaultBoardStore)app.Store).ReadAsync(b => b.AllItems().Select(i => i.Title).Order(StringComparer.Ordinal).ToList());

    [Fact]
    public async Task OneDrive_is_used_by_default_and_tasks_reach_the_other_computer()
    {
        var view = await View(_laptop);
        Assert.Equal("idle", view["state"]!.GetValue<string>());
        Assert.Equal("OneDrive", view["provider"]!.GetValue<string>());
        Assert.Equal(Path.Combine(_cloud, "Apps", "TodoTrackerSync"), view["where"]!.GetValue<string>());

        await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("Synced through OneDrive"), Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        await Sync(_desktop);

        Assert.Contains("Synced through OneDrive", await Titles(_desktop));
        Assert.True(Directory.EnumerateFiles(Path.Combine(_cloud, "Apps", "TodoTrackerSync", "devices")).Count() == 2);
    }

    [Fact]
    public async Task Sync_can_be_switched_off()
    {
        using var client = _laptop.Client();
        var response = await client.PutAsJsonAsync(new Uri("/api/plugins/sync/provider", UriKind.Relative), new { provider = "off" }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("Stays here"), Actor.User, ServerFixture.T0));
        var view = await Sync(_laptop);

        Assert.Equal("off", view["state"]!.GetValue<string>());
        Assert.False(Directory.Exists(Path.Combine(_cloud, "Apps", "TodoTrackerSync", "devices")));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(new Uri("/api/plugins/sync/provider", UriKind.Relative), new { provider = "dropbox" }, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task A_clash_is_listed_and_either_version_can_be_kept()
    {
        var task = await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("Call mom"), Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        await Sync(_desktop);
        await Sync(_laptop);

        await _laptop.Store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Details = "Ask about Sunday" }, Actor.User, ServerFixture.T0));
        await _desktop.Store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Details = "Ask about the car" }, Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        var view = await Sync(_desktop);

        var conflict = Assert.Single(view["conflicts"]!.AsArray());
        Assert.Contains("Call mom", conflict!["path"]!.GetValue<string>(), StringComparison.Ordinal);
        var details = await ((VaultBoardStore)_desktop.Store).ReadAsync(b => b.Get(task.Id).Details);
        Assert.Contains("Ask about Sunday", details, StringComparison.Ordinal);
        Assert.Contains("Ask about the car", details, StringComparison.Ordinal);

        using var client = _desktop.Client();
        var resolved = await client.PostAsJsonAsync(new Uri("/api/plugins/sync/resolve", UriKind.Relative), new { key = conflict["key"]!.GetValue<string>(), choice = "mine" }, TestContext.Current.CancellationToken);
        resolved.EnsureSuccessStatusCode();

        Assert.Empty(JsonNode.Parse(await resolved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["conflicts"]!.AsArray());
        Assert.Equal("Ask about the car", await ((VaultBoardStore)_desktop.Store).ReadAsync(b => b.Get(task.Id).Details));
    }

    [Fact]
    public async Task The_sync_plugins_are_listed_and_the_gist_starts_off()
    {
        using var client = _laptop.Client();
        var plugins = JsonNode.Parse(await client.GetStringAsync(new Uri("/api/plugins", UriKind.Relative), TestContext.Current.CancellationToken))!.AsArray();

        Assert.True(plugins.Single(p => p!["id"]!.GetValue<string>() == "sync-onedrive")!["enabled"]!.GetValue<bool>());
        Assert.True(plugins.Single(p => p!["id"]!.GetValue<string>() == "sync-icloud")!["enabled"]!.GetValue<bool>());
        Assert.False(plugins.Single(p => p!["id"]!.GetValue<string>() == "sync-gist")!["enabled"]!.GetValue<bool>());
    }
}
