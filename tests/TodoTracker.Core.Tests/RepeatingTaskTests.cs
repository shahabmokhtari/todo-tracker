using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public sealed class RepeatingTaskTests
{
    private static readonly DateTimeOffset Morning = new(2026, 1, 5, 10, 0, 0, TimeSpan.Zero); // a Monday
    private readonly TaskBoard _board = new() { TimeZone = TimeZoneInfo.Utc };

    private WorkItem Daily(string title = "Stand-up notes") =>
        _board.AddTask(new NewTask(title) { Repeat = "Every Day", Deadline = Morning.AddHours(7) }, Actor.User, Morning.AddDays(-1));

    [Fact]
    public void Done_moves_it_on_and_ends_this_time_of_it()
    {
        var item = Daily();
        var step = _board.AddTask(new NewTask("Write them") { ParentId = item.Id }, Actor.User, Morning);
        _board.Complete(step.Id, Actor.User, Morning);
        _board.SetStage(item.Id, Stage.Doing, Actor.User, Morning);
        _board.StartTimer(item.Id, Actor.User, Morning);
        var waiter = _board.AddTask(new NewTask("Send them"), Actor.User, Morning);
        _board.WaitFor(waiter.Id, item.Id, Actor.User, Morning);

        _board.Complete(item.Id, Actor.User, Morning.AddMinutes(30));

        Assert.False(item.IsDone);
        Assert.Equal("every day", item.Repeat?.ToString());
        Assert.Equal(Morning.AddDays(1).AddHours(7), item.Deadline);
        Assert.Equal(new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero), item.NextActionAt);
        Assert.False(step.IsDone);
        Assert.Equal(Stage.Next, item.Stage);
        Assert.Null(_board.RunningTimer(Morning.AddMinutes(31)));
        Assert.Null(waiter.AfterId);
        Assert.Contains("repeats: next Tue 6 Jan", _board.Activity.Last(a => a.ItemId == item.Id).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void It_counts_as_done_today_and_not_done_after_all_puts_it_back()
    {
        var item = Daily();
        _board.Complete(item.Id, Actor.User, Morning);
        Assert.Equal([item], Agenda.DoneToday(_board, Morning.AddHours(1), TimeZoneInfo.Utc));
        Assert.Equal(Morning, Agenda.DoneAt(_board, item));

        _board.Reopen(item.Id, Actor.User, Morning.AddHours(1));

        Assert.Empty(Agenda.DoneToday(_board, Morning.AddHours(2), TimeZoneInfo.Utc));
        Assert.Equal(Morning.AddHours(7), item.Deadline);
        Assert.Null(item.NextActionAt);
        Assert.Equal(ActivityKind.Reopened, _board.Activity[^1].Kind);

        // Reopening again does nothing more.
        _board.Reopen(item.Id, Actor.User, Morning.AddHours(2));
        Assert.Equal(Morning.AddHours(7), item.Deadline);
    }

    [Fact]
    public void A_counted_rule_gets_its_time_back_when_undone()
    {
        var item = _board.AddTask(new NewTask("Physio exercises") { Repeat = "every day for 3 times" }, Actor.User, Morning);
        _board.Complete(item.Id, Actor.User, Morning);
        Assert.Equal("every day for 2 times", item.Repeat?.ToString());

        _board.Reopen(item.Id, Actor.User, Morning);
        Assert.Equal("every day for 3 times", item.Repeat?.ToString());
    }

    [Fact]
    public void A_rule_is_checked_set_and_cleared()
    {
        var item = _board.AddTask(new NewTask("Taxes"), Actor.User, Morning);

        var problem = Assert.Throws<ArgumentException>(() => _board.Update(item.Id, new TaskChanges { Repeat = "every blue moon" }, Actor.User, Morning));
        Assert.Contains("every weekday", problem.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => _board.AddTask(new NewTask("Bad") { Repeat = "sometimes" }, Actor.User, Morning));

        _board.Update(item.Id, new TaskChanges { Repeat = "yearly" }, Actor.User, Morning);
        Assert.Equal("every year", item.Repeat?.ToString());

        _board.Update(item.Id, new TaskChanges { ClearRepeat = true }, Actor.User, Morning);
        Assert.Null(item.Repeat);
        _board.Complete(item.Id, Actor.User, Morning);
        Assert.True(item.IsDone);
    }

    [Fact]
    public void Not_done_after_all_puts_back_the_deadline_and_wait_it_had()
    {
        // Late (due Friday, done Monday), waiting until 8:30 that day.
        var item = _board.AddTask(new NewTask("Timesheet") { Repeat = "every day", Deadline = Morning.AddDays(-3).AddHours(7) }, Actor.User, Morning.AddDays(-5));
        _board.ScheduleNextAction(item.Id, Morning.AddDays(-3).AddHours(-1.5), Actor.User, Morning.AddDays(-5));
        _board.Complete(item.Id, Actor.User, Morning);
        Assert.Equal(Morning.AddDays(1).AddHours(7), item.Deadline);

        _board.Reopen(item.Id, Actor.User, Morning.AddMinutes(1));

        Assert.Equal(Morning.AddDays(-3).AddHours(7), item.Deadline);
        Assert.Equal(Morning.AddDays(-3).AddHours(-1.5), item.NextActionAt);
        Assert.Null(item.RepeatUndo);
    }

    [Fact]
    public void A_repeating_checklist_moves_on_when_its_last_step_is_done()
    {
        var review = _board.AddTask(new NewTask("Weekly review") { Repeat = "every week", Deadline = Morning.AddHours(7) }, Actor.User, Morning);
        var steps = _board.AddSteps(review.Id, ["Inbox zero", "Plan the week"], null, Actor.User, Morning);

        _board.Complete(steps[0].Id, Actor.User, Morning);
        _board.Complete(steps[1].Id, Actor.User, Morning.AddMinutes(5));

        Assert.False(review.IsDone);
        Assert.Equal(Morning.AddDays(7).AddHours(7), review.Deadline);
        Assert.All(steps, s => Assert.False(s.IsDone));
    }

    [Fact]
    public void A_repeating_step_of_an_in_order_task_is_done_for_good_so_the_next_step_can_start()
    {
        var rollout = _board.AddTask(new NewTask("Rollout"), Actor.User, Morning);
        var steps = _board.AddSteps(rollout.Id, ["Ring 0", "Ring 1"], null, Actor.User, Morning);
        _board.Update(steps[0].Id, new TaskChanges { Repeat = "every day" }, Actor.User, Morning);

        _board.Complete(steps[0].Id, Actor.User, Morning);
        _board.Complete(steps[1].Id, Actor.User, Morning);

        Assert.True(rollout.IsDone);
    }

    [Fact]
    public void Completing_its_parent_finishes_a_repeating_subtask_with_it()
    {
        var project = _board.AddTask(new NewTask("Project"), Actor.User, Morning);
        var check = _board.AddTask(new NewTask("Weekly check") { ParentId = project.Id, Repeat = "every week" }, Actor.User, Morning);

        _board.Complete(project.Id, Actor.User, Morning);

        Assert.True(check.IsDone);
    }

    [Fact]
    public void The_rule_survives_the_board_file()
    {
        var item = _board.AddTask(new NewTask("Rent") { Repeat = "every month on the last day" }, Actor.User, Morning);

        var reloaded = BoardSerializer.Deserialize(BoardSerializer.Serialize(_board));

        Assert.Equal("every month on the last day", reloaded.Get(item.Id).Repeat?.ToString());
    }
}
