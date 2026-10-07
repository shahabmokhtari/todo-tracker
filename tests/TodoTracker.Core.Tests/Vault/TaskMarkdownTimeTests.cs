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
        board: doing
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
    [InlineData("board: inbox", Stage.Inbox)]
    [InlineData("board: Doing", Stage.Doing)]
    [InlineData("board: next", Stage.Next)]
    [InlineData("board: someday", Stage.Next)]
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

        Assert.DoesNotContain("board:", TaskMarkdown.Render(next, Utc), StringComparison.Ordinal);
        var archived = TaskMarkdown.Render(inbox, Utc);
        Assert.Contains("board: inbox\n", archived, StringComparison.Ordinal);
        Assert.Contains("archived: 2026-01-06T12:00:00.000Z\n", archived, StringComparison.Ordinal);

        var back = TaskMarkdown.Parse(archived, "B", Utc).Root;
        Assert.Equal(Now, back.ArchivedAt);
        Assert.Equal(Stage.Inbox, back.Stage);
    }

    [Fact]
    public void A_timer_stopped_within_the_same_minute_stays_that_short()
    {
        // Review finding: "09:00–09:00" read as past midnight: 24 hours.
        var board = new TaskBoard();
        var item = board.AddTask(new NewTask("A"), Actor.User, Now);
        board.StartTimer(item.Id, Actor.User, Now.AddSeconds(10), "laptop");
        board.StopTimer(Now.AddSeconds(50));

        var text = TaskMarkdown.Render(item, Utc);
        var parsed = TaskMarkdown.Parse(text, "A", Utc);

        Assert.Equal(TimeSpan.FromSeconds(40), parsed.Root.TimeEntries[0].Duration(Now));
        Assert.False(parsed.NeedsWrite);
    }

    [Fact]
    public void An_end_on_the_clock_before_the_start_carries_its_date()
    {
        // DST fall-back, or a correction: the end is shown with its date so it can't be read as the next day.
        var board = new TaskBoard();
        var item = board.AddTask(new NewTask("A"), Actor.User, Now);
        board.AddTime(item.Id, Now, Now.AddMinutes(30), Actor.User, Now);
        var rendered = TaskMarkdown.Render(item, Utc);
        Assert.Contains("12:00–12:30", rendered, StringComparison.Ordinal);

        var typed = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# A\n\n## Time\n\n- 2026-01-05 10:00–2026-01-05 09:30\n";
        var back = TaskMarkdown.Parse(TaskMarkdown.Render(TaskMarkdown.Parse(typed, "A", Utc).Root, Utc), "A", Utc);
        Assert.True(back.Root.TimeEntries[0].Duration(Now) <= TimeSpan.Zero);
    }

    [Theory]
    [InlineData("- 2026-01-05 24:00–25:00")]
    [InlineData("- 2026-01-05 09:75–10:00")]
    [InlineData("- 2026-02-30 09:00–10:00")]
    public void A_mistyped_time_line_stays_as_text_and_the_task_still_loads(string line)
    {
        var text = $"---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# A\n\n## Time\n\n{line}\n";

        var parsed = TaskMarkdown.Parse(text, "A", Utc);

        Assert.Empty(parsed.Root.TimeEntries);
        Assert.Contains(line, TaskMarkdown.Render(parsed.Root, Utc, parsed), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_after_the_time_lines_stays_after_them()
    {
        var text = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# A\n\n## Time\n\n- 2026-01-05 09:00–10:00 %%{\"id\":\"7b0c2f9e-0000-4000-8000-0000000000d1\",\"start\":\"2026-01-05T09:00:00.000Z\",\"end\":\"2026-01-05T10:00:00.000Z\"}%%\n\nTotal this week: 1 h\n";

        var parsed = TaskMarkdown.Parse(text, "A", Utc);
        var rendered = TaskMarkdown.Render(parsed.Root, Utc, parsed);

        Assert.True(rendered.IndexOf("Total this week", StringComparison.Ordinal) > rendered.IndexOf("09:00–10:00", StringComparison.Ordinal));
    }

    [Fact]
    public void Time_lines_out_of_order_load_oldest_first()
    {
        var text = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# A\n\n## Time\n\n- 2026-01-05 11:00–12:00\n- 2026-01-05 09:00–10:00\n";

        var entries = TaskMarkdown.Parse(text, "A", Utc).Root.TimeEntries;

        Assert.True(entries[0].Start < entries[1].Start);
    }

    [Theory]
    [InlineData("archived: true", true)]
    [InlineData("archived: yes", true)]
    [InlineData("archived: false", false)]
    [InlineData("archived: soon", false)]
    [InlineData("archived: 2026-01-06", true)]
    public void An_archived_property_written_by_hand_never_breaks_the_file(string property, bool archived)
    {
        var text = $"---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: done\ncompleted: 2026-01-05T10:00:00.000Z\ncreated: 2026-01-05T09:00:00.000Z\n{property}\n---\n# A\n";

        Assert.Equal(archived, TaskMarkdown.Parse(text, "A", Utc).Root.ArchivedAt is not null);
    }

    [Fact]
    public void A_task_reopened_by_hand_is_not_archived()
    {
        var text = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\narchived: 2026-01-06T00:00:00.000Z\n---\n# A\n";

        var parsed = TaskMarkdown.Parse(text, "A", Utc);

        Assert.Null(parsed.Root.ArchivedAt);
        Assert.True(parsed.NeedsWrite);
    }

    [Fact]
    public void A_stage_property_of_the_persons_own_is_left_alone()
    {
        // Review finding: "stage: draft" (their own property) was taken over. The board column is "board:".
        var text = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\nstage: draft\n---\n# A\n";
        var parsed = TaskMarkdown.Parse(text, "A", Utc);
        parsed.Root.Stage = Stage.Doing;

        var rendered = TaskMarkdown.Render(parsed.Root, Utc, parsed);

        Assert.Contains("stage: draft\n", rendered, StringComparison.Ordinal);
        Assert.Contains("board: doing\n", rendered, StringComparison.Ordinal);
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
