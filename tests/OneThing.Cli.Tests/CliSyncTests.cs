using OneThing.Server;

namespace OneThing.Cli.Tests;

/// <summary>ot sync and ot connectors: the app's own sync and connectors, run here (never a real cloud drive in tests).</summary>
public sealed class CliSyncTests : IDisposable
{
    private readonly CliHarness _tt = new();

    public void Dispose() => _tt.Dispose();

    [Fact]
    public async Task Sync_shows_its_status()
    {
        var view = await _tt.Json("sync");

        Assert.NotNull(view["state"]);
        Assert.NotNull(view["library"]);
    }

    [Fact]
    public async Task Sync_now_leaves_it_to_the_app_while_the_app_runs()
    {
        Directory.CreateDirectory(_tt.DataDirectory);
        using var app = InstanceLock.Acquire(_tt.DataDirectory);

        var result = await _tt.Run("sync", "now");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("One Thing is running", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_now_runs_when_the_app_isnt_running()
    {
        var view = await _tt.Json("sync", "now");

        Assert.NotNull(view["state"]);
    }

    [Fact]
    public async Task Connectors_list_what_there_is_and_a_switched_off_one_doesnt_sync()
    {
        var view = await _tt.Json("connectors");
        Assert.Contains(view["connectors"]!.AsArray(), c => c.Str("id") == "notion" && !c!["on"]!.GetValue<bool>());

        var result = await _tt.Run("connectors", "sync", "notion");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("off", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_up_sync_is_left_to_the_app()
    {
        var result = await _tt.Run("sync", "provider", "onedrive");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Settings › Sync", result.Error, StringComparison.Ordinal);
    }
}
