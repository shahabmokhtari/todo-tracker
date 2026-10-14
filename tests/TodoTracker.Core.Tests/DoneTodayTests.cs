using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>What got done today: a list to look back on (and something to feel good about).</summary>
public sealed class DoneTodayTests
{
    // Monday 2026-01-05, 09:00 in New York (14:00 UTC).
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly DateTimeOffset Morning = new(2026, 1, 5, 14, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    [Fact]
    public void Lists_what_was_finished_since_local_midnight_newest_first()
    {
        var yesterday = Add("Yesterday's");
        var early = Add("Early");
        var later = Add("Later");
        Add("Still open");
        _board.Complete(yesterday.Id, Actor.User, Morning.AddHours(-10)); // 23:00 Sunday in New York
        _board.Complete(early.Id, Actor.User, Morning.AddHours(-8));      // 01:00 Monday in New York
        _board.Complete(later.Id, Actor.User, Morning.AddHours(1));

        var done = Agenda.DoneToday(_board, Morning.AddHours(2), NewYork);

        Assert.Equal(["Later", "Early"], done.Select(i => i.Title));
    }

    [Fact]
    public void A_parent_finished_with_its_subtasks_counts_once_but_steps_done_on_their_own_count_too()
    {
        var release = Add("Release");
        var ring0 = Add("Ring 0", release.Id);
        Add("Ring 1", release.Id);
        var other = Add("Other");
        var step = Add("Write tests", other.Id);
        _board.Complete(ring0.Id, Actor.User, Morning);
        _board.Complete(release.Id, Actor.User, Morning.AddMinutes(5));
        _board.Complete(step.Id, Actor.User, Morning.AddMinutes(6));

        var done = Agenda.DoneToday(_board, Morning.AddHours(1), NewYork);

        // Ring 0 was done first, by itself; Ring 1 was closed by finishing Release (shown once, as Release).
        Assert.Equal(["Write tests", "Release", "Ring 0"], done.Select(i => i.Title));
    }

    [Fact]
    public void The_last_step_of_an_in_order_task_counts_even_though_it_finishes_the_task()
    {
        // Review finding: the task finishes itself at the same moment as its last step, and the step was hidden.
        var release = Add("Release");
        _board.Update(release.Id, new TaskChanges { Sequential = true }, Actor.User, Morning);
        var ring0 = Add("Ring 0", release.Id);
        var ring1 = Add("Ring 1", release.Id);
        _board.Complete(ring0.Id, Actor.User, Morning);
        _board.Complete(ring1.Id, Actor.User, Morning.AddHours(1));

        var done = Agenda.DoneToday(_board, Morning.AddHours(2), NewYork);

        Assert.Equal(["Release", "Ring 1", "Ring 0"], done.Select(i => i.Title));
    }

    [Fact]
    public void Can_be_limited_to_a_group_and_leaves_out_archived_tasks()
    {
        var personal = _board.Groups.Single(g => g.Name == "Personal");
        var work = Add("Work thing");
        var home = _board.AddTask(new NewTask("Home thing") { GroupId = personal.Id }, Actor.User, Morning);
        var archived = Add("Put away");
        _board.Complete(work.Id, Actor.User, Morning);
        _board.Complete(home.Id, Actor.User, Morning);
        _board.Complete(archived.Id, Actor.User, Morning);
        _board.Archive(archived.Id, Actor.User, Morning);

        Assert.Equal(["Home thing"], Agenda.DoneToday(_board, Morning.AddHours(1), NewYork, personal.Id).Select(i => i.Title));
        Assert.Equal(2, Agenda.DoneToday(_board, Morning.AddHours(1), NewYork).Count);
    }

    private WorkItem Add(string title, Guid? parent = null) =>
        _board.AddTask(new NewTask(title) { ParentId = parent }, Actor.User, Morning.AddHours(-24));
}
