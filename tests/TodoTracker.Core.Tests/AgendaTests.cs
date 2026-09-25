using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class AgendaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    private WorkItem Add(string title, WorkItem? parent = null, Priority priority = Priority.Normal, bool sequential = false, TimeSpan? delay = null, DateTimeOffset? at = null, DateTimeOffset? deadline = null) =>
        _board.AddTask(new NewTask(title) { ParentId = parent?.Id, Priority = priority, Sequential = sequential, StepDelay = delay, Deadline = deadline }, Actor.User, at ?? T0);

    [Fact]
    public void Leaf_task_is_actionable_and_is_the_focus()
    {
        var item = Add("Write spec");

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(ItemState.Actionable, Agenda.StateOf(item, T0));
        Assert.Equal(item.Id, Assert.Single(dashboard.Now).Item.Id);
        Assert.Equal(item.Id, dashboard.Focus?.Item.Id);
        Assert.Empty(dashboard.Waiting);
    }

    [Fact]
    public void Empty_board_has_no_focus()
    {
        var dashboard = Agenda.Build(_board, T0);
        Assert.Null(dashboard.Focus);
        Assert.Empty(dashboard.Now);
    }

    [Fact]
    public void Container_is_hidden_and_its_leaves_show_with_breadcrumb()
    {
        var x = Add("Feature X");
        var a = Add("Feature A", x);

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(ItemState.Container, Agenda.StateOf(x, T0));
        var entry = Assert.Single(dashboard.Now);
        Assert.Equal(a.Id, entry.Item.Id);
        Assert.Equal(["Feature X"], entry.Breadcrumb);
    }

    [Fact]
    public void Parent_whose_children_are_all_done_becomes_actionable_for_wrap_up()
    {
        var x = Add("Feature X");
        var a = Add("Feature A", x);
        _board.Complete(a.Id, Actor.User, T0);

        Assert.Equal(ItemState.Actionable, Agenda.StateOf(x, T0));
    }

    [Fact]
    public void Scheduled_task_waits_until_its_time_then_returns()
    {
        var b = Add("Feature B");
        _board.ScheduleNextAction(b.Id, T0.AddHours(24), Actor.User, T0);

        var before = Agenda.Build(_board, T0.AddHours(23));
        var after = Agenda.Build(_board, T0.AddHours(24));

        Assert.Empty(before.Now);
        var waiting = Assert.Single(before.Waiting);
        Assert.Equal(T0.AddHours(24), waiting.WakeAt);
        Assert.Equal(b.Id, Assert.Single(after.Now).Item.Id);
        Assert.Empty(after.Waiting);
    }

    [Fact]
    public void Due_reminder_surfaces_a_waiting_task_first_with_attention()
    {
        var urgent = Add("Critical thing", priority: Priority.Critical);
        var b = Add("Feature B");
        _board.ScheduleNextAction(b.Id, T0.AddDays(2), Actor.User, T0);
        _board.AddReminder(b.Id, T0.AddHours(1), "Check B metrics", Actor.User, T0);

        var dashboard = Agenda.Build(_board, T0.AddHours(1));

        Assert.Equal([b.Id, urgent.Id], dashboard.Now.Select(e => e.Item.Id));
        Assert.True(dashboard.Now[0].NeedsAttention);
        Assert.Equal("Check B metrics", dashboard.Now[0].DueReminder?.Message);
        Assert.Empty(dashboard.Waiting);
    }

    [Fact]
    public void Dismissed_reminder_no_longer_surfaces_the_task()
    {
        var b = Add("Feature B");
        _board.ScheduleNextAction(b.Id, T0.AddDays(2), Actor.User, T0);
        var reminder = _board.AddReminder(b.Id, T0, "Check", Actor.User, T0);
        _board.DismissReminder(b.Id, reminder.Id, Actor.User, T0);

        var dashboard = Agenda.Build(_board, T0.AddHours(1));

        Assert.Empty(dashboard.Now);
        Assert.Single(dashboard.Waiting);
    }

    [Fact]
    public void Only_the_first_open_step_of_a_sequence_is_visible()
    {
        var a = Add("Feature A", sequential: true, delay: TimeSpan.FromHours(24));
        var s1 = Add("Step 1", a);
        var s2 = Add("Step 2", a);

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(ItemState.Locked, Agenda.StateOf(s2, T0));
        Assert.Equal(s1.Id, Assert.Single(dashboard.Now).Item.Id);
        Assert.Empty(dashboard.Waiting);
    }

    [Fact]
    public void Descendants_of_locked_items_are_locked()
    {
        var x = Add("Feature X", sequential: true);
        Add("Feature A", x);
        var b = Add("Feature B", x);
        var b1 = Add("B step 1", b);

        Assert.Equal(ItemState.Locked, Agenda.StateOf(b1, T0));
    }

    [Fact]
    public void Snoozed_container_is_listed_once_in_waiting_and_hides_its_children()
    {
        var a = Add("Feature A");
        var a1 = Add("A step 1", a);
        Add("A step 2", a);
        _board.ScheduleNextAction(a.Id, T0.AddHours(3), Actor.User, T0);

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(ItemState.Waiting, Agenda.StateOf(a1, T0));
        Assert.Empty(dashboard.Now);
        Assert.Equal(a.Id, Assert.Single(dashboard.Waiting).Item.Id);
    }

    [Fact]
    public void Done_items_are_excluded()
    {
        var a = Add("Done thing");
        _board.Complete(a.Id, Actor.User, T0);

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(ItemState.Done, Agenda.StateOf(a, T0));
        Assert.Empty(dashboard.Now);
        Assert.Empty(dashboard.Overview);
    }

    [Fact]
    public void Now_orders_by_priority_then_overdue_then_deadline_then_age()
    {
        var lowOld = Add("low", priority: Priority.Low, at: T0.AddMinutes(-10));
        var normalNoDeadline = Add("normal-none");
        var normalLaterDeadline = Add("normal-later", deadline: T0.AddDays(3));
        var normalSoonDeadline = Add("normal-soon", deadline: T0.AddDays(1));
        var normalOverdue = Add("normal-overdue", deadline: T0.AddHours(-1));
        var high = Add("high", priority: Priority.High, at: T0.AddMinutes(5));

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(
            [high.Id, normalOverdue.Id, normalSoonDeadline.Id, normalLaterDeadline.Id, normalNoDeadline.Id, lowOld.Id],
            dashboard.Now.Select(e => e.Item.Id));
        Assert.True(dashboard.Now[1].IsOverdue);
    }

    [Fact]
    public void Steps_inherit_effective_priority_from_ancestors()
    {
        var x = Add("Feature X", priority: Priority.Critical);
        var a = Add("Feature A", x, Priority.Low);
        var other = Add("Other", priority: Priority.High);

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal(Priority.Critical, dashboard.Now[0].EffectivePriority);
        Assert.Equal([a.Id, other.Id], dashboard.Now.Select(e => e.Item.Id));
    }

    [Fact]
    public void Waiting_orders_by_wake_time_then_priority()
    {
        var late = Add("late", priority: Priority.Critical);
        var earlyLow = Add("early-low", priority: Priority.Low);
        var earlyHigh = Add("early-high", priority: Priority.High);
        _board.ScheduleNextAction(late.Id, T0.AddHours(5), Actor.User, T0);
        _board.ScheduleNextAction(earlyLow.Id, T0.AddHours(1), Actor.User, T0);
        _board.ScheduleNextAction(earlyHigh.Id, T0.AddHours(1), Actor.User, T0);

        var dashboard = Agenda.Build(_board, T0);

        Assert.Equal([earlyHigh.Id, earlyLow.Id, late.Id], dashboard.Waiting.Select(e => e.Item.Id));
    }

    [Fact]
    public void Overview_summarizes_progress_per_workstream()
    {
        var x = Add("Feature X", priority: Priority.High);
        var a = Add("Feature A", x, sequential: true, delay: TimeSpan.FromHours(24));
        var a1 = Add("A1", a);
        Add("A2", a);
        var b = Add("Feature B", x, Priority.Critical);
        Add("B1", b);
        _board.Complete(a1.Id, Actor.User, T0);

        var dashboard = Agenda.Build(_board, T0);

        var overview = Assert.Single(dashboard.Overview);
        Assert.Equal(x.Id, overview.Item.Id);
        Assert.Equal(1, overview.DoneLeaves);
        Assert.Equal(3, overview.TotalLeaves);
        Assert.Equal(1, overview.ActionableCount);
        Assert.Equal(T0.AddHours(24), overview.NextWakeAt);
        Assert.Equal(Priority.Critical, overview.TopPriority);
    }

    [Fact]
    public void Recent_notes_are_newest_first_and_bounded()
    {
        var a = Add("A");
        var b = Add("B", a);
        _board.AddNote(a.Id, "first", Actor.User, T0);
        _board.AddNote(b.Id, "second", Actor.User, T0.AddMinutes(1));
        _board.AddNote(a.Id, "third", Actor.User, T0.AddMinutes(2));

        var dashboard = Agenda.Build(_board, T0, recentNoteCount: 2);

        Assert.Equal(["third", "second"], dashboard.RecentNotes.Select(n => n.Note.Text));
        Assert.Equal(b.Id, dashboard.RecentNotes[1].Item.Id);
        Assert.Empty(Agenda.Build(_board, T0, recentNoteCount: 0).RecentNotes);
    }

    [Fact]
    public void Rollout_example_end_to_end()
    {
        // Feature X needs A and B rolled out; each has steps gated 24h apart.
        var x = Add("Roll out feature X", priority: Priority.High);
        var a = Add("Feature A", x, sequential: true, delay: TimeSpan.FromHours(24));
        var b = Add("Feature B", x, sequential: true, delay: TimeSpan.FromHours(24));
        var a1 = Add("A: ring 0", a);
        var a2 = Add("A: ring 1", a);
        var b1 = Add("B: ring 0", b);
        var b2 = Add("B: ring 1", b);

        Assert.Equal([a1.Id, b1.Id], Agenda.Build(_board, T0).Now.Select(e => e.Item.Id));

        _board.Complete(a1.Id, Actor.User, T0.AddHours(1));
        _board.AddNote(a1.Id, "ring 0 healthy", Actor.User, T0.AddHours(1));
        var afterA1 = Agenda.Build(_board, T0.AddHours(2));
        Assert.Equal([b1.Id], afterA1.Now.Select(e => e.Item.Id));
        Assert.Equal([a2.Id], afterA1.Waiting.Select(e => e.Item.Id));

        _board.Complete(b1.Id, Actor.User, T0.AddHours(3));
        var nextDay = Agenda.Build(_board, T0.AddHours(25));
        Assert.Equal([a2.Id], nextDay.Now.Select(e => e.Item.Id));
        Assert.Equal([b2.Id], nextDay.Waiting.Select(e => e.Item.Id));

        _board.Complete(a2.Id, Actor.User, T0.AddHours(25));
        _board.Complete(b2.Id, Actor.User, T0.AddHours(27));
        var wrapUp = Agenda.Build(_board, T0.AddHours(28));
        Assert.Equal([x.Id], wrapUp.Now.Select(e => e.Item.Id));
    }
}
