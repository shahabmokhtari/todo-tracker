using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Tests.Vault;

/// <summary>Board stage, archive, and time spent, as they read in a task file (and in Obsidian).</summary>
public class TaskMarkdownTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TaskMarkdownContext Utc = new(TimeZoneInfo.Utc, Now, "Work");

    private const string WithTime = """
        ---
        id: 7b0c2f9e-0000-4000-8000-000000000001
        status: open
        created: 2026-01-05T09:00:00.000Z
        stage: doing
        ---
        # Ship release

        ## Subtasks

        - [ ] Roll out A ^t0000002 %%{"id":"7b0c2f9e-0000-4000-8000-000000000002","created":"2026-01-05T09:00:00.000Z"}%%

        ## Time

        - 2026-01-05 09:00–09:25 · 25 min · focus · [[#^t0000002|Roll out A]] %%{"id":"7b0c2f9e-0000-4000-8000-0000000000d1","start":"2026-01-05T09:00:00.000Z","end":"2026-01-05T09:25:00.000Z","device":"laptop"}%%
        - 2026-01-05 13:00–2026-01-06 01:30 · 12 h 30 min %%{"id":"7b0c2f9e-0000-4000-8000-0000000000d2","start":"2026-01-05T13:00:00.000Z","end":"2026-01-06T01:30:00.000Z"}%%
        - 2026-01-06 11:00 → running %%{"id":"7b0c2f9e-0000-4000-8000-0000000000d3","start":"2026-01-06T11:00:00.000Z","device":"desktop"}%%

        """;

    [Fact]
    public void Time_spent_reads_and_writes_back_exactly()
    {
        var text = WithTime.ReplaceLineEndings("\n");
        var parsed = TaskMarkdown.Parse(text, "Ship release", Utc);
        var root = parsed.Root;
        var step = root.Children[0];

        Assert.Equal(Stage.Doing, root.Stage);
        var focus = Assert.Single(step.TimeEntries);
        Assert.Equal(TimeSource.Focus, focus.Source);
        Assert.Equal("laptop", focus.Device);
        Assert.Equal(TimeSpan.FromMinutes(25), focus.Duration(Now));
        Assert.Equal(2, root.TimeEntries.Count);
        Assert.True(root.TimeEntries[1].IsRunning);
        Assert.Equal("desktop", root.TimeEntries[1].Device);
        Assert.False(parsed.NeedsWrite);

        Assert.Equal(text, TaskMarkdown.Render(root, Utc, parsed));
    }

    [Theory]
    [InlineData("- 2026-01-05 09:00–10:30", 90)]
    [InlineData("- 2026-01-05 09:00 - 10:30", 90)]
    [InlineData("- 2026-01-05 9:00 to 9:45 · standup", 45)]
    [InlineData("- 2026-01-05 22:00–2026-01-06 00:15", 135)]
    public void Time_typed_by_hand_counts(string line, int minutes)
    {
        var text = $"---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# A\n\n## Time\n\n{line}\n";

        var parsed = TaskMarkdown.Parse(text, "A", Utc);

        Assert.Equal(TimeSpan.FromMinutes(minutes), Assert.Single(parsed.Root.TimeEntries).Duration(Now));
        Assert.Equal(TimeSource.Manual, parsed.Root.TimeEntries[0].Source);
        Assert.True(parsed.NeedsWrite);
    }

    [Fact]
    public void An_end_changed_in_Obsidian_wins_over_the_hidden_exact_time()
    {
        var text = WithTime.ReplaceLineEndings("\n").Replace("09:00–09:25 · 25 min", "09:00–09:40 · 25 min", StringComparison.Ordinal);

        var parsed = TaskMarkdown.Parse(text, "Ship release", Utc);

        Assert.Equal(TimeSpan.FromMinutes(40), parsed.Root.Children[0].TimeEntries[0].Duration(Now));
        Assert.True(parsed.NeedsWrite);
    }

    [Fact]
    public void Lines_that_are_not_time_stay_as_they_were()
    {
        var text = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# A\n\n## Time\n\nEstimate: 3 hours.\n\n- 2026-01-05 09:00–10:00 %%{\"id\":\"7b0c2f9e-0000-4000-8000-0000000000d1\",\"start\":\"2026-01-05T09:00:00.000Z\",\"end\":\"2026-01-05T10:00:00.000Z\"}%%\n";

        var parsed = TaskMarkdown.Parse(text, "A", Utc);
        var rendered = TaskMarkdown.Render(parsed.Root, Utc, parsed);

        Assert.Single(parsed.Root.TimeEntries);
        Assert.Contains("Estimate: 3 hours.", rendered, StringComparison.Ordinal);
        Assert.Contains("- 2026-01-05 09:00–10:00 · 1 h 00 min %%", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stage: inbox", Stage.Inbox)]
    [InlineData("stage: Doing", Stage.Doing)]
    [InlineData("stage: next", Stage.Next)]
    [InlineData("stage: someday", Stage.Next)]
    [InlineData("", Stage.Next)]
    public void The_board_column_is_a_property_and_tasks_without_one_are_next(string property, Stage stage)
    {
        var text = $"---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n{property}\n---\n# A\n";

        Assert.Equal(stage, TaskMarkdown.Parse(text, "A", Utc).Root.Stage);
    }

    [Fact]
    public void Next_is_not_written_but_other_columns_and_the_archive_are()
    {
        var board = new TaskBoard();
        var next = board.AddTask(new NewTask("A") { Stage = Stage.Next }, Actor.User, Now);
        var inbox = board.AddTask(new NewTask("B"), Actor.User, Now);
        board.Complete(inbox.Id, Actor.User, Now);
        board.Archive(inbox.Id, Actor.User, Now);

        Assert.DoesNotContain("stage:", TaskMarkdown.Render(next, Utc), StringComparison.Ordinal);
        var archived = TaskMarkdown.Render(inbox, Utc);
        Assert.Contains("stage: inbox\n", archived, StringComparison.Ordinal);
        Assert.Contains("archived: 2026-01-06T12:00:00.000Z\n", archived, StringComparison.Ordinal);

        var back = TaskMarkdown.Parse(archived, "B", Utc).Root;
        Assert.Equal(Now, back.ArchivedAt);
        Assert.Equal(Stage.Inbox, back.Stage);
    }

    [Fact]
    public void A_timer_started_in_the_app_is_written_as_running()
    {
        var board = new TaskBoard();
        var item = board.AddTask(new NewTask("A"), Actor.User, Now);
        board.StartTimer(item.Id, Actor.User, Now, "laptop");

        var text = TaskMarkdown.Render(item, Utc);

        Assert.Contains("## Time\n\n- 2026-01-06 12:00 → running %%{", text, StringComparison.Ordinal);
        Assert.Contains("\"device\":\"laptop\"", text, StringComparison.Ordinal);
        Assert.True(TaskMarkdown.Parse(text, "A", Utc).Root.TimeEntries[0].IsRunning);
    }
}
