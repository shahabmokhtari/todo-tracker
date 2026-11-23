namespace OneThing.Cli.Tests;

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

    [Fact]
    public async Task The_whole_folders_history_and_a_versions_text_are_shown()
    {
        var id = (await _tt.Json("add", "Draft plan")).Id().ToString();
        await _tt.Ok("edit", id, "--title", "Final plan");

        var board = (await _tt.Json("history"))!["versions"]!.AsArray();
        Assert.True(board.Count >= 2, $"{board.Count} versions");
        var first = (await _tt.Json("history", id)).AsArray()[^1].Str("id");

        var text = await _tt.Ok("history", id, "--text", first);

        Assert.Contains("Draft plan", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Final plan", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folders_left_out_of_history_are_in_the_json_too()
    {
        await _tt.Json("add", "Ship");
        Directory.CreateDirectory(Path.Combine(_tt.VaultDirectory, "Work", "Project", ".git"));
        await File.WriteAllTextAsync(Path.Combine(_tt.VaultDirectory, "Work", "Project", "Plan.md"), "# Plan\n", TestContext.Current.CancellationToken);
        await _tt.Json("add", "Ship again");

        var history = await _tt.Json("history");

        Assert.NotEmpty(history!["versions"]!.AsArray());
        Assert.Equal(["Work/Project"], history["notInHistory"].Strings());
        Assert.Contains("Not in history (a git repository of its own): Work/Project", await _tt.Ok("history"), StringComparison.Ordinal);
    }
}
