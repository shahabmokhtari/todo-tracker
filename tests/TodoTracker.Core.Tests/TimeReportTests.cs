using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>Reports: where the time went (by day, group, task, hour), what got done, and a timeline of the work.</summary>
public class TimeReportTests
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2");
    private static readonly DateTimeOffset Monday = new(2026, 1, 5, 7, 0, 0, TimeSpan.Zero); // 09:00 local
    private readonly TaskBoard _board = new();

    private WorkItem Task(string title, Guid? parent = null, Guid? group = null) =>
        _board.AddTask(new NewTask(title) { ParentId = parent, GroupId = group }, Actor.User, Monday.AddDays(-3));

    private TimeReport Build(DateOnly from, DateOnly to, Guid? group = null) =>
        TimeReport.Build(_board, from, to, Tz, Monday.AddDays(7), group);

    [Fact]
    public void Time_is_counted_per_local_day_and_split_at_midnight()
    {
        var item = Task("Report");
        _board.AddTime(item.Id, Monday, Monday.AddHours(1), Actor.User, Monday);
        _board.AddTime(item.Id, Monday.AddHours(14), Monday.AddHours(16), Actor.User, Monday); // 23:00–01:00 local

        var report = Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 7));

        Assert.Equal([3600 + 3600, 3600, 0], report.Days.Select(d => d.TrackedSeconds));
        Assert.Equal(3 * 3600, report.TrackedSeconds);
    }

    [Fact]
    public void Time_outside_the_range_does_not_count()
    {
        var item = Task("Report");
        _board.AddTime(item.Id, Monday.AddDays(-2), Monday.AddDays(-2).AddHours(1), Actor.User, Monday);
        _board.AddTime(item.Id, Monday.AddHours(-1), Monday.AddHours(1), Actor.User, Monday); // 08:00–10:00 local Monday

        var report = Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5));

        Assert.Equal(2 * 3600, report.TrackedSeconds);
    }

    [Fact]
    public void Focus_and_manual_time_are_told_apart()
    {
        var item = Task("Report");
        _board.StartFocus(item.Id, Actor.User, Monday);
        _board.TickPomodoro(Monday.AddMinutes(30));
        _board.AddTime(item.Id, Monday.AddHours(2), Monday.AddHours(3), Actor.User, Monday);

        var report = Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5));

        Assert.Equal(25 * 60, report.Days[0].FocusSeconds);
        Assert.Equal(1, report.FocusSessions);
    }

    [Fact]
    public void Time_adds_up_by_group_and_by_task_with_its_subtasks()
    {
        var personal = _board.Groups[1].Id;
        var work = Task("Release");
        var step = Task("Roll out", work.Id);
        var gym = Task("Gym", group: personal);
        _board.AddTime(step.Id, Monday, Monday.AddHours(2), Actor.User, Monday);
        _board.AddTime(work.Id, Monday.AddHours(3), Monday.AddHours(4), Actor.User, Monday);
        _board.AddTime(gym.Id, Monday.AddHours(5), Monday.AddHours(6), Actor.User, Monday);

        var report = Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5));

        Assert.Equal([(work.GroupId, 3 * 3600), (personal, 3600)], report.Groups.Select(g => (g.GroupId, g.TrackedSeconds)));
        Assert.Equal(["Release", "Gym"], report.Tasks.Select(t => t.Title));
        Assert.Equal(3 * 3600, report.Tasks[0].TrackedSeconds);
        Assert.Equal([3600], Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5), personal).Groups.Select(g => g.TrackedSeconds));
    }

    [Fact]
    public void What_got_done_and_started_is_counted_per_day_with_a_streak()
    {
        var a = Task("A");
        var b = Task("B");
        var c = Task("C");
        _board.Complete(a.Id, Actor.User, Monday);
        _board.Complete(b.Id, Actor.User, Monday.AddDays(1));
        _board.AddTime(c.Id, Monday.AddDays(2), Monday.AddDays(2).AddHours(1), Actor.User, Monday);

        var report = Build(new DateOnly(2026, 1, 4), new DateOnly(2026, 1, 7));

        Assert.Equal([0, 1, 1, 0], report.Days.Select(d => d.Completed));
        Assert.Equal(2, report.Completed);
        Assert.Equal(3, report.ActiveDays);
        Assert.Equal(3, report.LongestStreak);
    }

    [Fact]
    public void Time_by_weekday_and_hour_shows_when_work_happens()
    {
        var item = Task("Report");
        _board.AddTime(item.Id, Monday, Monday.AddMinutes(90), Actor.User, Monday); // Mon 09:00–10:30 local

        var report = Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 5));

        Assert.Equal(3600, report.HourGrid[(int)DayOfWeek.Monday][9]);
        Assert.Equal(1800, report.HourGrid[(int)DayOfWeek.Monday][10]);
    }

    [Fact]
    public void The_timeline_shows_each_task_with_its_work_and_its_life()
    {
        var item = Task("Release");
        _board.AddTime(item.Id, Monday, Monday.AddHours(1), Actor.User, Monday);
        _board.Complete(item.Id, Actor.User, Monday.AddDays(1));
        Task("Untouched"); // nothing happened to it in the range: not on the timeline

        var report = Build(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 7));

        var row = Assert.Single(report.Timeline);
        Assert.Equal("Release", row.Title);
        Assert.Equal(Monday.AddDays(-3), row.CreatedAt);
        Assert.Equal(Monday.AddDays(1), row.CompletedAt);
        Assert.Equal([(Monday, Monday.AddHours(1))], row.Work.Select(w => (w.Start, w.End)));
    }

    [Fact]
    public void A_running_timer_counts_until_now()
    {
        var item = Task("Report");
        _board.StartTimer(item.Id, Actor.User, Monday.AddDays(7).AddMinutes(-30));

        var report = Build(new DateOnly(2026, 1, 12), new DateOnly(2026, 1, 12));

        Assert.Equal(30 * 60, report.TrackedSeconds);
    }

    [Fact]
    public void A_range_of_more_than_two_years_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Build(new DateOnly(2020, 1, 1), new DateOnly(2026, 1, 1)));
        Assert.Throws<ArgumentException>(() => Build(new DateOnly(2026, 1, 7), new DateOnly(2026, 1, 5)));
    }
}
