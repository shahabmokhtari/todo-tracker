using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TodoTracker.Core;
using TodoTracker.Core.Vault;
using TodoTracker.Server.Plugins.Sync;

namespace TodoTracker.Server.Tests.Sync;

/// <summary>
/// Tasks in more than one place: a laptop syncs through OneDrive, a Mac through iCloud Drive, and a desktop has both.
/// </summary>
public sealed class MultiPlaceSyncTests : IAsyncLifetime
{
    private readonly string _onedrive = Directory.CreateTempSubdirectory("tt-onedrive-").FullName;
    private readonly string _home = Directory.CreateTempSubdirectory("tt-home-").FullName;
    private readonly string _elsewhere = Directory.CreateTempSubdirectory("tt-elsewhere-").FullName;
    private ServerFixture _laptop = null!;
    private ServerFixture _mac = null!;
    private ServerFixture _desktop = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_home, "iCloudDrive"));
        _laptop = await Start(oneDrive: true, iCloud: false);
        _mac = await Start(oneDrive: false, iCloud: true);
        _desktop = await Start(oneDrive: true, iCloud: true);
    }

    public async ValueTask DisposeAsync()
    {
        await _laptop.DisposeAsync();
        await _mac.DisposeAsync();
        await _desktop.DisposeAsync();
        foreach (var dir in new[] { _onedrive, _home, _elsewhere })
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private Task<ServerFixture> Start(bool oneDrive, bool iCloud) => ServerFixture.StartAsync(services: s => s.AddSingleton(
        new CloudEnvironment(name => oneDrive && name == "OneDriveCommercial" ? _onedrive : null, iCloud ? _home : _elsewhere, Directory.Exists, _ => [], IsWindows: true, IsMac: false)));

    private static async Task<JsonNode> Sync(ServerFixture app)
    {
        using var client = app.Client();
        var response = await client.PostAsync(new Uri("/api/plugins/sync/now", UriKind.Relative), null, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<JsonNode> Use(ServerFixture app, string provider, string mode)
    {
        using var client = app.Client();
        var response = await client.PutAsJsonAsync(new Uri("/api/plugins/sync/places", UriKind.Relative), new { provider, mode }, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
    }

    private static Task<List<string>> Titles(ServerFixture app) =>
        ((VaultBoardStore)app.Store).ReadAsync(b => b.AllItems().Select(i => i.Title).Order(StringComparer.Ordinal).ToList());

    private static JsonNode Place(JsonNode view, string id) => view["places"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public async Task Tasks_in_two_places_are_flagged_then_read_from_both_and_merged()
    {
        await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("From the laptop"), Actor.User, ServerFixture.T0));
        await _mac.Store.UpdateAsync(b => b.AddTask(new NewTask("From the Mac"), Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        await Sync(_mac);

        // The desktop uses OneDrive (first available) and sees that the Mac's tasks are in iCloud Drive too.
        var view = await Sync(_desktop);
        Assert.Contains("From the laptop", await Titles(_desktop));
        Assert.DoesNotContain("From the Mac", await Titles(_desktop));
        Assert.Contains("iCloud Drive", view["warning"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("both", Place(view, "onedrive")["mode"]!.GetValue<string>());
        Assert.Equal("off", Place(view, "icloud")["mode"]!.GetValue<string>());
        Assert.Equal(1, Place(view, "icloud")["otherDevices"]!.GetValue<int>());

        // Read and write both: the desktop merges both places, and each place gets everything.
        Assert.Equal("both", Place(await Use(_desktop, "icloud", "both"), "onedrive")["mode"]!.GetValue<string>());
        view = await Sync(_desktop);
        Assert.Null(view["warning"]);
        Assert.Equal("OneDrive + iCloud Drive", view["provider"]!.GetValue<string>());
        Assert.Equal(["From the Mac", "From the laptop"], await Titles(_desktop));
        await Sync(_laptop);
        await Sync(_mac);
        Assert.Contains("From the Mac", await Titles(_laptop));
        Assert.Contains("From the laptop", await Titles(_mac));
    }

    [Fact]
    public async Task A_place_can_be_only_read_or_only_written()
    {
        await Use(_desktop, "onedrive", "read");
        await Use(_desktop, "icloud", "write");
        await _laptop.Store.UpdateAsync(b => b.AddTask(new NewTask("From the laptop"), Actor.User, ServerFixture.T0));
        await _desktop.Store.UpdateAsync(b => b.AddTask(new NewTask("From the desktop"), Actor.User, ServerFixture.T0));
        await Sync(_laptop);
        await Sync(_desktop);
        await Sync(_laptop);
        await Sync(_mac);

        // Read from OneDrive: the laptop's task is here; nothing is written there.
        Assert.Contains("From the laptop", await Titles(_desktop));
        Assert.DoesNotContain("From the desktop", await Titles(_laptop));

        // Written to iCloud Drive: the Mac gets the desktop's tasks (including what it read from OneDrive).
        Assert.Contains("From the desktop", await Titles(_mac));
        Assert.Contains("From the laptop", await Titles(_mac));

        using var client = _desktop.Client();
        var wrong = await client.PutAsJsonAsync(new Uri("/api/plugins/sync/places", UriKind.Relative), new { provider = "icloud", mode = "sometimes" }, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, wrong.StatusCode);
    }
}
