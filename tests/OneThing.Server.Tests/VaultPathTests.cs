using OneThing.Core;

namespace OneThing.Server.Tests;

public sealed class VaultPathTests : IDisposable
{
    private readonly string _data = Directory.CreateTempSubdirectory("onething-paths-").FullName;

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Theory]
    [InlineData("${user_config.tasks_folder}")]
    [InlineData("  ")]
    [InlineData("")]
    public void Unset_or_unexpanded_placeholders_mean_the_apps_folder(string value)
    {
        // Claude Desktop leaves "${user_config.x}" as-is when an optional setting is empty.
        var options = new OneThingServerOptions { DataDirectory = _data, VaultPath = value };

        Assert.Equal(Path.Combine(_data, "vault"), options.ResolveVaultPath(chosenInApp: null));
        Assert.False(options.IsVaultOverridden);
    }

    [Fact]
    public void An_explicit_folder_wins()
    {
        var options = new OneThingServerOptions { DataDirectory = _data, VaultPath = Path.Combine(_data, "elsewhere") };

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

        var other = new OneThingServerOptions { DataDirectory = _data, VaultPath = Path.Combine(_data, "other"), WatchVault = false, LockDirectory = locks };
        using (var store = OneThingHost.OpenVault(other, new SettingsStore(other), TimeProvider.System))
        {
            Assert.Empty(await store.ReadAsync(b => b.Items.ToList()));
        }

        Assert.True(File.Exists(Path.Combine(_data, "board.json")));
        var own = new OneThingServerOptions { DataDirectory = _data, WatchVault = false, LockDirectory = locks };
        using (var store = OneThingHost.OpenVault(own, new SettingsStore(own), TimeProvider.System))
        {
            Assert.Equal(["From the old app"], await store.ReadAsync(b => b.Items.Select(i => i.Title).ToList()));
        }
    }

    [Fact]
    public void A_data_folder_of_its_own_keeps_the_lock_and_history_of_the_tasks_inside_it()
    {
        var options = new OneThingServerOptions { DataDirectory = _data };

        Assert.Equal(Path.Combine(_data, "locks"), options.LockDirectoryFor(Path.Combine(_data, "vault")));
        Assert.Equal(Path.Combine(_data, "locks"), options.LockDirectoryFor(Path.Combine(_data, "github-tasks")));
    }

    [Fact]
    public void Tasks_outside_the_data_folder_keep_the_shared_lock_so_the_app_and_ot_take_turns()
    {
        var options = new OneThingServerOptions { DataDirectory = _data };

        Assert.Null(options.LockDirectoryFor(Path.Combine(Path.GetTempPath(), "someone-elses-tasks")));
        Assert.Null(options.LockDirectoryFor(_data + "-sibling"));
        Assert.Null(new OneThingServerOptions { DataDirectory = OneThingServerOptions.PlatformDataDirectory() }
            .LockDirectoryFor(Path.Combine(OneThingServerOptions.PlatformDataDirectory(), "vault")));
    }

    [Fact]
    public void A_lock_folder_named_outright_wins()
    {
        var options = new OneThingServerOptions { DataDirectory = _data, LockDirectory = Path.Combine(_data, "mine") };

        Assert.Equal(Path.Combine(_data, "mine"), options.LockDirectoryFor(Path.Combine(Path.GetTempPath(), "anywhere")));
    }

    [Fact]
    public void The_store_of_a_data_folder_of_its_own_keeps_its_history_there()
    {
        var options = new OneThingServerOptions { DataDirectory = _data, WatchVault = false };
        using var store = OneThingHost.OpenVault(options, new SettingsStore(options), TimeProvider.System);

        Assert.StartsWith(Path.Combine(_data, "locks", "history") + Path.DirectorySeparatorChar, store.HistoryPath, StringComparison.Ordinal);
    }
}
