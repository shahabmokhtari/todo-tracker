using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;
using TodoTracker.Core.Sync;
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
        var root = Path.Combine(Path.GetTempPath(), "me", "OneDrive - Contoso");
        var env = new CloudEnvironment(name => name == "OneDriveCommercial" ? root : null, Path.GetTempPath(), d => d == root, _ => [], IsWindows: true, IsMac: false);
        var provider = new OneDriveSyncProvider(env);

        var inside = provider.Check(Path.Combine(root, "Documents", "Todo Tracker"));
        var outside = provider.Check(Path.Combine(Path.GetTempPath(), "me", "Todo Tracker"));

        Assert.False(inside.Available);
        Assert.Contains("already in OneDrive", inside.Detail, StringComparison.Ordinal);
        Assert.True(outside.Available);
        Assert.Equal(Path.Combine(root, "Apps", "TodoTrackerSync"), outside.Detail);
    }

    [Fact]
    public void Without_a_cloud_drive_the_providers_say_so()
    {
        var env = Windows([]);

        Assert.False(new OneDriveSyncProvider(env).Check(@"C:\Tasks").Available);
        Assert.False(new ICloudSyncProvider(env).Check(@"C:\Tasks").Available);
    }

    [Fact]
    public async Task The_gist_needs_a_github_sign_in_which_is_looked_up_in_the_background()
    {
        var dir = Directory.CreateTempSubdirectory("tt-gist-").FullName;
        try
        {
            var options = new TodoTrackerServerOptions { DataDirectory = dir };
            using var slow = new ManualResetEventSlim();
            var signedIn = new GistSyncProvider(options) { FindToken = () => { slow.Wait(TimeSpan.FromSeconds(10)); return "token"; } };

            // Asking gh never blocks the caller (the sidebar menu asks from the UI thread).
            Assert.Contains("Checking", signedIn.Check(dir).Detail, StringComparison.Ordinal);
            slow.Set();
            Assert.True(await Eventually(() => signedIn.Check(dir).Available));

            var signedOut = new GistSyncProvider(options) { FindToken = () => null };
            Assert.True(await Eventually(() => signedOut.Check(dir).Detail.Contains("gh auth login", StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }

        return condition();
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
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_cloud, "Apps", "TodoTrackerSync", "vault", "devices")).Count());
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
        Assert.False(Directory.Exists(Path.Combine(_cloud, "Apps", "TodoTrackerSync", "vault", "devices")));
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
        var key = conflict["key"]!.GetValue<string>();

        // Review finding: choosing a side before the other computer had the merged task was undone by its merge.
        Assert.False(conflict["ready"]!.GetValue<bool>());
        var early = await client.PostAsJsonAsync(new Uri("/api/plugins/sync/resolve", UriKind.Relative), new { key, choice = "mine" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, early.StatusCode);
        await Sync(_laptop);
        Assert.True((await Sync(_desktop))["conflicts"]![0]!["ready"]!.GetValue<bool>());

        var resolved = await client.PostAsJsonAsync(new Uri("/api/plugins/sync/resolve", UriKind.Relative), new { key, choice = "mine" }, TestContext.Current.CancellationToken);
        resolved.EnsureSuccessStatusCode();

        Assert.Empty(JsonNode.Parse(await resolved.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["conflicts"]!.AsArray());
        Assert.Equal("Ask about the car", await ((VaultBoardStore)_desktop.Store).ReadAsync(b => b.Get(task.Id).Details));

        // And it stays chosen on both computers.
        await Sync(_desktop);
        await Sync(_laptop);
        await Sync(_desktop);
        Assert.Equal("Ask about the car", await ((VaultBoardStore)_desktop.Store).ReadAsync(b => b.Get(task.Id).Details));
        Assert.Equal("Ask about the car", await ((VaultBoardStore)_laptop.Store).ReadAsync(b => b.Get(task.Id).Details));
    }

    [Fact]
    public async Task Keeping_theirs_or_both_and_an_unknown_merge_tool()
    {
        var task = await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("Plan trip"), Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        await Sync(_desktop);
        await Sync(_laptop);
        await _laptop.Store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Details = "By train" }, Actor.User, ServerFixture.T0));
        await _desktop.Store.UpdateAsync(b => b.Update(task.Id, new TaskChanges { Details = "By plane" }, Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        var key = (await Sync(_desktop))["conflicts"]![0]!["key"]!.GetValue<string>();
        await Sync(_laptop);
        await Sync(_desktop);
        using var client = _desktop.Client();

        var compare = await client.PostAsJsonAsync(new Uri("/api/plugins/sync/compare", UriKind.Relative), new { key, tool = "no-such-tool" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, compare.StatusCode);

        (await client.PostAsJsonAsync(new Uri("/api/plugins/sync/resolve", UriKind.Relative), new { key, choice = "theirs" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        Assert.Equal("By train", await ((VaultBoardStore)_desktop.Store).ReadAsync(b => b.Get(task.Id).Details));
        var again = await client.PostAsJsonAsync(new Uri("/api/plugins/sync/resolve", UriKind.Relative), new { key, choice = "merged" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, again.StatusCode);
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

    [Fact]
    public async Task A_different_library_name_keeps_tasks_apart_and_forgotten_computers_are_gone()
    {
        using var desktop = _desktop.Client();
        (await desktop.PutAsJsonAsync(new Uri("/api/plugins/sync/library", UriKind.Relative), new { library = "Side project" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("Work only"), Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        await Sync(_desktop);

        Assert.DoesNotContain("Work only", await Titles(_desktop));
        Assert.Equal("Side project", (await View(_desktop))["library"]!.GetValue<string>());

        (await desktop.PutAsJsonAsync(new Uri("/api/plugins/sync/library", UriKind.Relative), new { library = "vault" }, TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await Sync(_desktop);
        var laptopSeen = Assert.Single((await View(_desktop))["devices"]!.AsArray());
        Assert.Contains("Work only", await Titles(_desktop));

        var forgotten = await desktop.PostAsJsonAsync(new Uri("/api/plugins/sync/forget", UriKind.Relative), new { device = laptopSeen!["device"]!.GetValue<string>() }, TestContext.Current.CancellationToken);
        forgotten.EnsureSuccessStatusCode();
        Assert.Empty(JsonNode.Parse(await forgotten.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!["devices"]!.AsArray());
    }
}

/// <summary>Sync must never take the app down, whatever a provider does.</summary>
public sealed class SyncRobustnessTests
{
    private sealed class Throwing(Exception error) : ISyncProvider, ISyncRemote
    {
        public string Id => "throwing";

        public string Name => "Throwing";

        public int Order => 1;

        public bool Automatic => true;

        public string Identity => "throwing";

        public SyncAvailability Check(string vaultRoot) => new(true, "nowhere");

        public ISyncRemote CreateRemote(string vaultRoot, string library) => this;

        public Task<Core.Sync.RemoteSnapshot?> ReadAsync(string self, string? knownVersion, CancellationToken cancellationToken) => throw error;

        public Task<byte[]> ReadContentAsync(Core.Sync.DeviceSnapshot device, Core.Sync.SyncEntry entry, CancellationToken cancellationToken) => throw error;

        public Task<string?> PublishAsync(Core.Sync.DeviceSnapshot snapshot, Func<Core.Sync.SyncEntry, byte[]> read, string? readVersion, CancellationToken cancellationToken) => throw error;

        public Task ForgetAsync(string device, CancellationToken cancellationToken) => throw error;
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("argument")]
    [InlineData("json")]
    public async Task A_failing_sync_is_reported_not_thrown(string kind)
    {
        // Review finding: an HttpClient timeout (TaskCanceledException) escaped and stopped the whole app.
        Exception error = kind switch
        {
            "timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout."),
            "argument" => new ArgumentException("An item with the same key has already been added."),
            _ => new System.Text.Json.JsonException("bad"),
        };
        await using var app = await ServerFixture.StartAsync(services: s => s.AddSingleton<ISyncProvider>(new Throwing(error)));
        var sync = app.App.Services.GetRequiredService<SyncService>();

        var view = await sync.SyncNowAsync(TestContext.Current.CancellationToken);

        Assert.Equal("error", view.State);
        Assert.NotNull(view.Problem);
    }

    [Fact]
    public async Task The_background_loop_syncs_without_being_asked()
    {
        var cloud = Directory.CreateTempSubdirectory("tt-onedrive-").FullName;
        try
        {
            await using var app = await ServerFixture.StartAsync(
                o => o.EnableBackgroundLoop = true,
                s => s.AddSingleton(new CloudEnvironment(name => name == "OneDriveCommercial" ? cloud : null, Path.GetTempPath(), Directory.Exists, _ => [], IsWindows: true, IsMac: false)));
            var devices = Path.Combine(cloud, "Apps", "TodoTrackerSync", "vault", "devices");

            for (var i = 0; i < 100 && !(Directory.Exists(devices) && Directory.EnumerateFiles(devices).Any()); i++)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            Assert.Single(Directory.EnumerateFiles(devices));
        }
        finally
        {
            Directory.Delete(cloud, recursive: true);
        }
    }

    [Fact]
    public void Merge_tools_get_file_names_as_plain_arguments()
    {
        // Review finding: VS Code went through cmd.exe, which broke on spaces and ran "&" in file names.
        var start = MergeTools.StartInfo(new MergeTool("vscode", "Visual Studio Code", @"C:\Program Files\Microsoft VS Code\Code.exe"), @"C:\data\other.md", @"C:\Tasks\R&D a&echo INJECTED&.md");

        Assert.Equal(@"C:\Program Files\Microsoft VS Code\Code.exe", start.FileName);
        Assert.Equal(["--diff", @"C:\data\other.md", @"C:\Tasks\R&D a&echo INJECTED&.md"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
    }
}
