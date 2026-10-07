using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>Time spent on tasks: one timer at a time, focus sessions count, and time can be fixed by hand.</summary>
public class TimeTrackingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    private WorkItem Task(string title, Guid? parent = null) => _board.AddTask(new NewTask(title) { ParentId = parent }, Actor.User, T0);

    [Fact]
    public void A_timer_counts_until_it_is_stopped()
    {
        var item = Task("Write report");

        _board.StartTimer(item.Id, Actor.User, T0, "laptop");
        Assert.Equal(TimeSpan.FromMinutes(10), item.TimeSpent(T0.AddMinutes(10)));
        _board.StopTimer(T0.AddMinutes(25));

        var entry = Assert.Single(item.TimeEntries);
        Assert.Equal(T0, entry.Start);
        Assert.Equal(T0.AddMinutes(25), entry.End);
        Assert.Equal(TimeSource.Manual, entry.Source);
        Assert.Equal("laptop", entry.Device);
        Assert.Null(_board.RunningTimer());
        Assert.Equal(TimeSpan.FromMinutes(25), item.TimeSpent(T0.AddHours(5)));
    }

    [Fact]
    public void Only_one_timer_runs_starting_another_stops_the_first()
    {
        var a = Task("A");
        var b = Task("B");

        _board.StartTimer(a.Id, Actor.User, T0);
        _board.StartTimer(b.Id, Actor.User, T0.AddMinutes(15));

        Assert.Equal(T0.AddMinutes(15), Assert.Single(a.TimeEntries).End);
        Assert.Equal(b, _board.RunningTimer()!.Value.Item);
    }

    [Fact]
    public void Starting_the_timer_that_runs_keeps_it()
    {
        var a = Task("A");
        _board.StartTimer(a.Id, Actor.User, T0);

        _board.StartTimer(a.Id, Actor.User, T0.AddMinutes(5));

        Assert.Single(a.TimeEntries);
    }

    [Fact]
    public void A_parent_counts_the_time_of_its_subtasks()
    {
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);
        _board.AddTime(step.Id, T0, T0.AddMinutes(30), Actor.User, T0);
        _board.AddTime(parent.Id, T0.AddHours(1), T0.AddHours(2), Actor.User, T0);

        Assert.Equal(TimeSpan.FromMinutes(90), parent.TimeSpent(T0.AddHours(3)));
        Assert.Equal(TimeSpan.FromMinutes(60), parent.TimeSpent(T0.AddHours(3), includeSubtasks: false));
    }

    [Fact]
    public void Starting_work_moves_the_card_to_doing()
    {
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);
        Assert.Equal(Stage.Inbox, parent.Stage);

        _board.StartTimer(step.Id, Actor.User, T0);

        Assert.Equal(Stage.Doing, parent.Stage);
    }

    [Fact]
    public void Finishing_a_task_stops_its_timer()
    {
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);
        _board.StartTimer(step.Id, Actor.User, T0);

        _board.Complete(parent.Id, Actor.User, T0.AddMinutes(40));

        Assert.Equal(T0.AddMinutes(40), Assert.Single(step.TimeEntries).End);
        Assert.Null(_board.RunningTimer());
    }

    [Fact]
    public void Done_tasks_can_not_be_timed_but_time_can_be_added_after_the_fact()
    {
        var item = Task("Call bank");
        _board.Complete(item.Id, Actor.User, T0);

        Assert.Throws<InvalidOperationException>(() => _board.StartTimer(item.Id, Actor.User, T0));
        _board.AddTime(item.Id, T0.AddHours(-1), T0, Actor.User, T0);

        Assert.Equal(TimeSpan.FromHours(1), item.TimeSpent(T0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(25 * 60)]
    public void Time_added_by_hand_must_make_sense(int minutes)
    {
        var item = Task("A");

        Assert.Throws<ArgumentException>(() => _board.AddTime(item.Id, T0, T0.AddMinutes(minutes), Actor.User, T0));
    }

    [Fact]
    public void A_time_entry_can_be_corrected_or_removed()
    {
        var item = Task("A");
        var entry = _board.AddTime(item.Id, T0, T0.AddHours(3), Actor.User, T0);

        _board.UpdateTime(item.Id, entry.Id, T0, T0.AddMinutes(45), Actor.User, T0);
        Assert.Equal(TimeSpan.FromMinutes(45), item.TimeSpent(T0.AddHours(5)));

        _board.RemoveTime(item.Id, entry.Id, Actor.User, T0);
        Assert.Empty(item.TimeEntries);
    }

    [Fact]
    public void Deleting_the_task_that_is_timed_stops_the_timer()
    {
        var item = Task("A");
        _board.StartTimer(item.Id, Actor.User, T0);

        _board.Delete(item.Id, Actor.User, T0.AddMinutes(5));

        Assert.Null(_board.RunningTimer());
    }

    [Fact]
    public void A_focus_session_is_timed_and_ends_exactly_when_the_session_does()
    {
        var item = Task("Write report");

        _board.StartFocus(item.Id, Actor.User, T0, "laptop");
        _board.TickPomodoro(T0.AddMinutes(30));

        var entry = Assert.Single(item.TimeEntries);
        Assert.Equal(TimeSource.Focus, entry.Source);
        Assert.Equal(T0.AddMinutes(25), entry.End);
        Assert.Null(_board.RunningTimer());
    }

    [Fact]
    public void Pausing_focus_pauses_the_timer_and_resuming_starts_it_again()
    {
        var item = Task("Write report");
        _board.StartFocus(item.Id, Actor.User, T0);

        _board.PauseFocus(T0.AddMinutes(10));
        _board.ResumeFocus(T0.AddMinutes(20));
        _board.ResetFocus(T0.AddMinutes(30));

        Assert.Equal([TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10)], item.TimeEntries.Select(e => e.End!.Value - e.Start));
        Assert.Null(_board.RunningTimer());
    }

    [Fact]
    public void Skipping_focus_stops_its_timer_but_not_one_started_by_hand_on_another_task()
    {
        var item = Task("Write report");
        var other = Task("Inbox zero");
        _board.StartFocus(item.Id, Actor.User, T0);
        _board.SkipFocus(T0.AddMinutes(5));
        Assert.Null(_board.RunningTimer());

        _board.StartTimer(other.Id, Actor.User, T0.AddMinutes(6));
        _board.TickPomodoro(T0.AddHours(1)); // the break ends

        Assert.Equal(other, _board.RunningTimer()!.Value.Item);
    }

    [Fact]
    public void Skipping_right_after_focus_ended_skips_the_break_that_started_not_a_new_one()
    {
        // The app shows the break the moment the session ends; Skip can arrive before the background tick.
        var item = Task("Write report");
        _board.StartFocus(item.Id, Actor.User, T0);

        _board.SkipFocus(T0.AddMinutes(25).AddSeconds(3));

        Assert.Equal(PomodoroPhase.Idle, _board.Pomodoro.Phase);
        Assert.Equal(1, _board.Pomodoro.CompletedFocusCount);
        Assert.Equal(T0.AddMinutes(25), Assert.Single(item.TimeEntries).End);
    }

    [Fact]
    public void A_timer_left_running_when_the_app_stopped_ends_when_it_was_last_seen()
    {
        var item = Task("A");
        _board.StartTimer(item.Id, Actor.User, T0, "laptop");

        var closed = _board.CloseAbandonedTimers("laptop", lastSeen: T0.AddMinutes(50));

        Assert.Equal(1, closed);
        Assert.Equal(T0.AddMinutes(50), Assert.Single(item.TimeEntries).End);
    }

    [Fact]
    public void Another_devices_timer_is_left_alone()
    {
        var item = Task("A");
        _board.StartTimer(item.Id, Actor.User, T0, "desktop");

        Assert.Equal(0, _board.CloseAbandonedTimers("laptop", lastSeen: T0.AddMinutes(50)));
        Assert.NotNull(_board.RunningTimer());
    }

    [Fact]
    public void A_timer_running_for_more_than_twelve_hours_is_taken_as_forgotten()
    {
        // Review finding: a timer left running on a computer that never came back counted forever.
        var item = Task("A");
        _board.StartTimer(item.Id, Actor.User, T0, "old-laptop");

        Assert.Equal(TaskBoard.ForgottenAfter, item.TimeSpent(T0.AddDays(3)));
        Assert.Equal(1, _board.CloseForgottenTimers(T0.AddDays(3)));
        Assert.Equal(T0 + TaskBoard.ForgottenAfter, Assert.Single(item.TimeEntries).End);
        Assert.Null(_board.RunningTimer());
    }

    [Fact]
    public void Resuming_focus_doesnt_stop_a_timer_started_by_hand_meanwhile()
    {
        var focused = Task("Write report");
        var other = Task("Urgent call");
        _board.StartFocus(focused.Id, Actor.User, T0);
        _board.PauseFocus(T0.AddMinutes(5));
        _board.StartTimer(other.Id, Actor.User, T0.AddMinutes(6));

        _board.ResumeFocus(T0.AddMinutes(10));

        Assert.Equal(other, _board.RunningTimer()!.Value.Item);
    }

    [Fact]
    public void Timing_another_task_by_hand_during_focus_takes_the_session_off_the_first()
    {
        // The session keeps going, but it no longer counts for the task that isn't being worked on.
        var focused = Task("Write report");
        var other = Task("Urgent call");
        _board.StartFocus(focused.Id, Actor.User, T0);

        _board.StartTimer(other.Id, Actor.User, T0.AddMinutes(5));
        _board.TickPomodoro(T0.AddMinutes(30));

        Assert.Null(_board.Pomodoro.ItemId);
        Assert.DoesNotContain(_board.Activity, a => a.Kind == ActivityKind.FocusCompleted && a.ItemId == focused.Id);
        Assert.Equal(TimeSpan.FromMinutes(5), focused.TimeSpent(T0.AddHours(1)));
    }

    [Fact]
    public void Deleting_a_subtask_keeps_its_time_on_the_task()
    {
        // The work happened: reports keep it.
        var parent = Task("Release");
        var step = Task("Roll out", parent.Id);
        _board.AddTime(step.Id, T0, T0.AddHours(1), Actor.User, T0);

        _board.Delete(step.Id, Actor.User, T0.AddHours(2));

        Assert.Equal(TimeSpan.FromHours(1), parent.TimeSpent(T0.AddHours(3)));
    }

    [Fact]
    public void When_two_devices_both_left_a_timer_running_the_newest_one_runs()
    {
        // Sync can bring two running entries together: the newest wins; stopping stops both.
        var a = Task("A");
        var b = Task("B");
        TaskBoard.AddRunningEntryForTest(a, T0, "desktop");
        TaskBoard.AddRunningEntryForTest(b, T0.AddMinutes(10), "laptop");

        Assert.Equal(b, _board.RunningTimer()!.Value.Item);
        _board.StopTimer(T0.AddMinutes(30));

        Assert.All(_board.AllItems().SelectMany(i => i.TimeEntries), e => Assert.NotNull(e.End));
        Assert.Equal(T0.AddMinutes(10), Assert.Single(a.TimeEntries).End);
    }
}
