using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class PomodoroTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Idle_timer_shows_full_focus_duration()
    {
        var timer = new PomodoroTimer();

        Assert.Equal(PomodoroPhase.Idle, timer.Phase);
        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(25), timer.Remaining(T0));
    }

    [Fact]
    public void Focus_counts_down_and_clamps_at_zero()
    {
        var timer = new PomodoroTimer();
        var item = Guid.NewGuid();

        timer.StartFocus(T0, item);

        Assert.Equal(PomodoroPhase.Focus, timer.Phase);
        Assert.Equal(item, timer.ItemId);
        Assert.Equal(T0.AddMinutes(25), timer.EndsAt);
        Assert.Equal(TimeSpan.FromMinutes(15), timer.Remaining(T0.AddMinutes(10)));
        Assert.Equal(TimeSpan.Zero, timer.Remaining(T0.AddHours(1)));
    }

    [Fact]
    public void Pause_freezes_and_resume_continues()
    {
        var timer = new PomodoroTimer();
        timer.StartFocus(T0);

        timer.Pause(T0.AddMinutes(5));
        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.FromMinutes(20), timer.Remaining(T0.AddMinutes(50)));

        timer.Resume(T0.AddMinutes(60));
        Assert.True(timer.IsRunning);
        Assert.Equal(T0.AddMinutes(80), timer.EndsAt);
    }

    [Fact]
    public void Tick_moves_completed_focus_into_short_break_and_reports_event()
    {
        var timer = new PomodoroTimer();
        var item = Guid.NewGuid();
        timer.StartFocus(T0, item);

        Assert.Null(timer.Tick(T0.AddMinutes(24)));
        var evt = timer.Tick(T0.AddMinutes(25));

        Assert.Equal(new PomodoroEvent(PomodoroEventKind.FocusCompleted, item, T0.AddMinutes(25)), evt);
        Assert.Equal(PomodoroPhase.ShortBreak, timer.Phase);
        Assert.Equal(1, timer.CompletedFocusCount);
        Assert.Equal(T0.AddMinutes(30), timer.EndsAt);
    }

    [Fact]
    public void Every_fourth_focus_earns_a_long_break()
    {
        var timer = new PomodoroTimer();
        var now = T0;
        for (var i = 0; i < 4; i++)
        {
            timer.StartFocus(now);
            now = now.AddMinutes(25);
            timer.Tick(now);
            if (i < 3)
            {
                Assert.Equal(PomodoroPhase.ShortBreak, timer.Phase);
                now = now.AddMinutes(5);
                Assert.Equal(PomodoroEventKind.BreakCompleted, timer.Tick(now)?.Kind);
            }
        }

        Assert.Equal(PomodoroPhase.LongBreak, timer.Phase);
        Assert.Equal(now.AddMinutes(15), timer.EndsAt);
    }

    [Fact]
    public void Finished_break_returns_to_idle_waiting_for_user()
    {
        var timer = new PomodoroTimer();
        timer.StartFocus(T0);
        timer.Tick(T0.AddMinutes(25));

        var evt = timer.Tick(T0.AddMinutes(31));

        Assert.Equal(PomodoroEventKind.BreakCompleted, evt?.Kind);
        Assert.Equal(PomodoroPhase.Idle, timer.Phase);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void Skip_focus_goes_to_break_without_counting()
    {
        var timer = new PomodoroTimer();
        timer.StartFocus(T0);

        timer.Skip(T0.AddMinutes(3));

        Assert.Equal(PomodoroPhase.ShortBreak, timer.Phase);
        Assert.Equal(0, timer.CompletedFocusCount);
        Assert.Equal(T0.AddMinutes(8), timer.EndsAt);

        timer.Skip(T0.AddMinutes(4));
        Assert.Equal(PomodoroPhase.Idle, timer.Phase);
    }

    [Fact]
    public void Reset_returns_to_idle_and_clears_cycle()
    {
        var timer = new PomodoroTimer();
        timer.StartFocus(T0, Guid.NewGuid());
        timer.Tick(T0.AddMinutes(25));

        timer.Reset();

        Assert.Equal(PomodoroPhase.Idle, timer.Phase);
        Assert.Null(timer.ItemId);
        Assert.Equal(0, timer.CompletedFocusCount);
        Assert.Null(timer.EndsAt);
    }

    [Fact]
    public void Pause_and_resume_are_ignored_when_not_applicable()
    {
        var timer = new PomodoroTimer();
        timer.Pause(T0);
        timer.Resume(T0);
        Assert.Equal(PomodoroPhase.Idle, timer.Phase);
        Assert.Null(timer.Tick(T0));
    }

    [Theory]
    [InlineData(0, 5, 15, 4)]
    [InlineData(25, 0, 15, 4)]
    [InlineData(25, 5, 0, 4)]
    [InlineData(25, 5, 15, 0)]
    public void Settings_must_be_positive(int focus, int shortBreak, int longBreak, int cycles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PomodoroSettings(focus, shortBreak, longBreak, cycles));
    }

    [Fact]
    public void Board_logs_completed_focus_on_linked_task()
    {
        var board = new TaskBoard();
        var item = board.AddTask(new NewTask("Deep work"), Actor.User, T0);
        board.StartFocus(item.Id, Actor.User, T0);

        var events = board.TickPomodoro(T0.AddMinutes(40));

        Assert.Equal([PomodoroEventKind.FocusCompleted, PomodoroEventKind.BreakCompleted], events.Select(e => e.Kind));
        Assert.Contains(board.Activity, a => a.Kind == ActivityKind.FocusCompleted && a.ItemId == item.Id);
    }

    [Fact]
    public void Board_rejects_focus_on_unknown_task()
    {
        var board = new TaskBoard();
        Assert.Throws<TaskNotFoundException>(() => board.StartFocus(Guid.NewGuid(), Actor.User, T0));
    }
}
