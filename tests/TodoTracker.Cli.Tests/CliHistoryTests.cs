namespace TodoTracker.Cli.Tests;

/// <summary>Each CLI change is saved as a version right away (agents work fast; the app may not be running).</summary>
public sealed class CliHistoryTests : IDisposable
{
    private readonly CliHarness _tt = new(history: true);

    public void Dispose() => _tt.Dispose();

    [Fact]
    public async Task Changes_are_versioned_and_can_be_restored()
    {
        var id = (await _tt.Json("add", "Draft plan")).Id().ToString();
        await _tt.Ok("edit", id, "--title", "Final plan");

        var versions = (await _tt.Json("history", id)).AsArray();
        Assert.True(versions.Count >= 2, $"{versions.Count} versions");
        var first = versions[^1].Str("id");

        var restored = await _tt.Json("restore", id, first[..10]);

        Assert.Equal("Draft plan", restored.Str("title"));
        Assert.Contains("Final plan", await _tt.Ok("history", id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restoring_an_unknown_version_fails_cleanly()
    {
        var id = (await _tt.Json("add", "Something")).Id().ToString();

        var result = await _tt.Run("restore", id, "deadbeef");

        Assert.Equal(1, result.ExitCode);
        Assert.NotEmpty(result.Error);
    }
}
