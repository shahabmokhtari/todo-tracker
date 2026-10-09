using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>"Start next focus": from the break screen, or when the break is over.</summary>
public sealed class NextFocusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    private WorkItem Task(string title) => _board.AddTask(new NewTask(title), Actor.User, T0);

    [Fact]
    public void During_a_break_it_ends_the_break_and_times_the_same_task_again()
    {
        var item = Task("Write report");
        _board.StartFocus(item.Id, Actor.User, T0);
        _board.TickPomodoro(T0.AddMinutes(26)); // the session ended at 25; a short break runs

        var started = _board.StartNextFocus(null, Actor.User, T0.AddMinutes(27));

        Assert.Equal(item, started);
        Assert.Equal(PomodoroPhase.Focus, _board.Pomodoro.Phase);
        Assert.Equal(T0.AddMinutes(27 + 25), _board.Pomodoro.EndsAt);
        Assert.Equal(1, _board.Pomodoro.CompletedFocusCount); // the break isn't a focus session
        Assert.Equal(item, _board.RunningTimer()!.Value.Item);
        Assert.Equal(TimeSource.Focus, _board.RunningTimer()!.Value.Entry.Source);
        Assert.Equal(2, item.TimeEntries.Count);
    }

    [Fact]
    public void Right_when_focus_runs_out_it_counts_that_session_then_starts_the_next()
    {
        // The break screen shows the moment the session ends, before any background tick.
        var item = Task("Write report");
        _board.StartFocus(item.Id, Actor.User, T0);

        _board.StartNextFocus(null, Actor.User, T0.AddMinutes(25).AddSeconds(2));

        Assert.Equal(1, _board.Pomodoro.CompletedFocusCount);
        Assert.Equal(T0.AddMinutes(25), item.TimeEntries[0].End);
        Assert.Equal(PomodoroPhase.Focus, _board.Pomodoro.Phase);
    }

    [Fact]
    public void After_the_break_it_starts_again_on_the_same_task()
    {
        var item = Task("Write report");
        _board.StartFocus(item.Id, Actor.User, T0);
        _board.TickPomodoro(T0.AddMinutes(40)); // focus and the break are both over

        Assert.Equal(PomodoroPhase.Idle, _board.Pomodoro.Phase);
        Assert.Equal(item, _board.NextFocusItem());
        Assert.Equal(item, _board.StartNextFocus(null, Actor.User, T0.AddMinutes(41)));
    }

    [Fact]
    public void A_task_done_meanwhile_gives_way_to_the_one_offered()
    {
        var item = Task("Write report");
        var next = Task("Inbox zero");
        _board.StartFocus(item.Id, Actor.User, T0);
        _board.TickPomodoro(T0.AddMinutes(26));
        _board.Complete(item.Id, Actor.User, T0.AddMinutes(26));

        Assert.Null(_board.NextFocusItem());
        Assert.Equal(next, _board.StartNextFocus(next.Id, Actor.User, T0.AddMinutes(27)));
        Assert.Equal(next.Id, _board.Pomodoro.ItemId);
    }

    [Fact]
    public void A_session_still_running_isnt_restarted()
    {
        // A second click, window or notification mustn't cut the session (and its time) short.
        var item = Task("Write report");
        _board.StartFocus(item.Id, Actor.User, T0);

        Assert.Throws<InvalidOperationException>(() => _board.StartNextFocus(null, Actor.User, T0.AddMinutes(10)));
        Assert.Equal(T0.AddMinutes(25), _board.Pomodoro.EndsAt);
        Assert.Single(item.TimeEntries);
    }

    [Fact]
    public void Without_any_task_it_starts_a_plain_session()
    {
        Assert.Null(_board.StartNextFocus(null, Actor.User, T0));
        Assert.Equal(PomodoroPhase.Focus, _board.Pomodoro.Phase);
        Assert.Null(_board.RunningTimer());
    }
}
