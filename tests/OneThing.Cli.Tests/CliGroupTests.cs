namespace OneThing.Cli.Tests;

/// <summary>Groups (the tabs) from the command line: list, add, rename, recolor. Never delete: that's the app's.</summary>
public sealed class CliGroupTests : IDisposable
{
    private readonly CliHarness _tt = new();

    public void Dispose() => _tt.Dispose();

    [Fact]
    public async Task A_group_added_from_the_command_line_takes_tasks()
    {
        var group = await _tt.Json("group", "add", "Garden", "--color", "#22c55e");

        Assert.Equal("Garden", group.Str("name"));
        Assert.Equal("#22c55e", group.Str("color"));
        var item = await _tt.Json("add", "Plant tulips", "-g", "garden");
        Assert.Equal(group.Id(), Guid.Parse(item.Str("groupId")));
        Assert.True(File.Exists(Path.Combine(_tt.VaultDirectory, "Garden", "Plant tulips.md")));
        Assert.Contains("Garden", (await _tt.Json("groups")).AsArray().Select(g => g.Str("name")));
    }

    [Fact]
    public async Task A_group_name_with_spaces_needs_no_quotes()
    {
        var group = await _tt.Json("group", "add", "Side", "projects");

        Assert.Equal("Side projects", group.Str("name"));
    }

    [Fact]
    public async Task Rename_and_color_change_a_group_and_keep_its_tasks()
    {
        var id = (await _tt.Json("group", "add", "Garden")).Id();
        await _tt.Ok("add", "Plant tulips", "-g", "Garden");

        var renamed = await _tt.Json("group", "rename", "garden", "Back", "yard");
        var colored = await _tt.Json("group", "color", "Back yard", "#3b82f6");

        Assert.Equal("Back yard", renamed.Str("name"));
        Assert.Equal("#3b82f6", colored.Str("color"));
        Assert.Equal(id, renamed.Id());
        Assert.Equal(id, Guid.Parse((await _tt.Json("show", "tulips")).Str("groupId")));
        Assert.Null((await _tt.Json("group", "color", "Back yard", "none"))["color"]);
    }

    [Fact]
    public async Task Adding_to_a_missing_group_says_how_to_make_it_and_makes_nothing()
    {
        var before = (await _tt.Json("groups")).AsArray().Count;

        var result = await _tt.Run("add", "Plant tulips", "-g", "Gardn");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("ot group add \"Gardn\"", result.Error, StringComparison.Ordinal);
        Assert.Equal(before, (await _tt.Json("groups")).AsArray().Count);
        Assert.Empty((await _tt.Json("list")).AsArray());
    }

    [Fact]
    public async Task A_second_group_with_the_same_name_is_refused()
    {
        var result = await _tt.Run("group", "add", "work");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Groups_are_never_deleted_from_the_command_line()
    {
        var result = await _tt.Run("group", "delete", "Work");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("in the app", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_adding_a_group_is_attributed()
    {
        await _tt.Ok("group", "add", "Garden", "--as", "claude");

        var timeline = await _tt.Json("timeline");
        Assert.Contains(timeline.AsArray(), e => e.Str("summary").Contains("Garden", StringComparison.Ordinal) && e.Str("actorKind") == "agent" && e.Str("actor").Contains("claude", StringComparison.Ordinal));
    }
}
