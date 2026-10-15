using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Tests;

public sealed class PlanTests
{
    private static readonly DateTimeOffset Morning = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero); // a Monday
    private static readonly TaskMarkdownContext Context = new(TimeZoneInfo.Utc, Morning, "Work");
    private readonly TaskBoard _board = new() { TimeZone = TimeZoneInfo.Utc };

    [Fact]
    public void A_task_gets_a_block_of_time_and_is_the_thing_to_do_while_it_is_on()
    {
        var first = _board.AddTask(new NewTask("Answer mail") { Priority = Priority.High }, Actor.User, Morning);
        var report = _board.AddTask(new NewTask("Write the report") { Priority = Priority.Low }, Actor.User, Morning);

        _board.Plan(report.Id, Morning.AddHours(1), 90, Actor.User, Morning);

        Assert.Equal((Morning.AddHours(1), 90), (report.PlannedAt, report.PlannedMinutes));
        Assert.Equal(first.Id, Agenda.Build(_board, Morning.AddMinutes(30)).Focus!.Item.Id);
        Assert.Equal(report.Id, Agenda.Build(_board, Morning.AddMinutes(61)).Focus!.Item.Id);
        Assert.Equal(first.Id, Agenda.Build(_board, Morning.AddMinutes(150)).Focus!.Item.Id);
    }

    [Fact]
    public void Blocks_are_five_minutes_to_twelve_hours_keep_their_length_and_come_off_the_plan()
    {
        var task = _board.AddTask(new NewTask("Deep work"), Actor.User, Morning);

        Assert.Throws<ArgumentOutOfRangeException>(() => _board.Plan(task.Id, Morning, 2, Actor.User, Morning));
        Assert.Throws<ArgumentOutOfRangeException>(() => _board.Plan(task.Id, Morning, 13 * 60, Actor.User, Morning));
        _board.Plan(task.Id, Morning, 120, Actor.User, Morning);
        _board.Plan(task.Id, Morning.AddDays(1), null, Actor.User, Morning);
        Assert.Equal(120, task.PlannedMinutes);

        _board.Unplan(task.Id, Actor.User, Morning);
        Assert.Equal((null, null), (task.PlannedAt, task.PlannedMinutes));

        _board.Complete(task.Id, Actor.User, Morning);
        Assert.Throws<InvalidOperationException>(() => _board.Plan(task.Id, Morning, 30, Actor.User, Morning));
    }

    [Fact]
    public void A_repeating_task_s_block_moves_to_its_next_time()
    {
        var standup = _board.AddTask(new NewTask("Standup notes") { Repeat = "every weekday", Deadline = Morning.AddHours(8) }, Actor.User, Morning);
        _board.Plan(standup.Id, Morning.AddHours(1), 15, Actor.User, Morning);

        _board.Complete(standup.Id, Actor.User, Morning.AddHours(1).AddMinutes(10));

        Assert.Equal(Morning.AddDays(1).AddHours(1), standup.PlannedAt);
        Assert.Equal(15, standup.PlannedMinutes);
    }

    [Fact]
    public void The_plan_survives_the_files()
    {
        var root = _board.AddTask(new NewTask("Release"), Actor.User, Morning);
        var step = _board.AddTask(new NewTask("Notes") { ParentId = root.Id }, Actor.User, Morning);
        _board.Plan(root.Id, Morning.AddHours(2), 45, Actor.User, Morning);
        _board.Plan(step.Id, Morning.AddHours(3), null, Actor.User, Morning);

        var text = TaskMarkdown.Render(root, Context);
        var md = TaskMarkdown.Parse(text, "Release", Context).Root;
        var json = BoardSerializer.Deserialize(BoardSerializer.Serialize(_board)).Get(root.Id);

        Assert.Contains("planned: 2026-01-05T11:00", text, StringComparison.Ordinal);
        Assert.Contains("planned-length: 45m", text, StringComparison.Ordinal);
        Assert.Equal((Morning.AddHours(2), 45), (md.PlannedAt, md.PlannedMinutes));
        Assert.Equal(Morning.AddHours(3), md.Children.Single().PlannedAt);
        Assert.Equal((Morning.AddHours(2), 45), (json.PlannedAt, json.PlannedMinutes));
    }
}
