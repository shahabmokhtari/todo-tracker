using TodoTracker.Core;

namespace TodoTracker.Server.Tests;

public sealed class VaultPathTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("tt-paths-").FullName;

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Theory]
    [InlineData("${user_config.tasks_folder}")]
    [InlineData("  ")]
    [InlineData("")]
    public void Unset_or_unexpanded_placeholders_mean_the_apps_folder(string value)
    {
        // Claude Desktop leaves "${user_config.x}" as-is when an optional setting is empty.
        var options = new TodoTrackerServerOptions { DataDirectory = _data, VaultPath = value };

        Assert.Equal(Path.Combine(_data, "vault"), options.ResolveVaultPath(chosenInApp: null));
        Assert.False(options.IsVaultOverridden);
    }

    [Fact]
    public void An_explicit_folder_wins()
    {
        var options = new TodoTrackerServerOptions { DataDirectory = _data, VaultPath = Path.Combine(_data, "elsewhere") };

        Assert.Equal(Path.Combine(_data, "elsewhere"), options.ResolveVaultPath(chosenInApp: null));
        Assert.True(options.IsVaultOverridden);
    }

    [Fact]
    public async Task An_old_board_is_only_migrated_into_the_apps_own_folder()
    {
        var board = new TaskBoard();
        board.AddTask(new NewTask("From the old app"), Actor.User, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(Path.Combine(_data, "board.json"), BoardSerializer.Serialize(board), TestContext.Current.CancellationToken);
        var locks = Path.Combine(_data, "locks");

        var other = new TodoTrackerServerOptions { DataDirectory = _data, VaultPath = Path.Combine(_data, "other"), WatchVault = false, LockDirectory = locks };
        using (var store = TodoTrackerHost.OpenVault(other, new SettingsStore(other), TimeProvider.System))
        {
            Assert.Empty(await store.ReadAsync(b => b.Items.ToList()));
        }

        Assert.True(File.Exists(Path.Combine(_data, "board.json")));
        var own = new TodoTrackerServerOptions { DataDirectory = _data, WatchVault = false, LockDirectory = locks };
        using (var store = TodoTrackerHost.OpenVault(own, new SettingsStore(own), TimeProvider.System))
        {
            Assert.Equal(["From the old app"], await store.ReadAsync(b => b.Items.Select(i => i.Title).ToList()));
        }
    }
}
