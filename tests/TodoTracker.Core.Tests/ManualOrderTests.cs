using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>
/// People decide what comes first: once they arrange Do now, their order wins (only a due reminder jumps ahead).
/// Tasks they never placed slot in by the automatic rules (priority, overdue, deadline, age).
/// </summary>
public class ManualOrderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    private WorkItem Add(string title, Priority priority = Priority.Normal, WorkItem? parent = null, Guid? group = null) =>
        _board.AddTask(new NewTask(title) { Priority = priority, ParentId = parent?.Id, GroupId = group }, Actor.User, T0);

    private List<string> Now(Guid? group = null) => Agenda.Build(_board, T0, groupId: group).Now.Select(e => e.Item.Title).ToList();

    [Fact]
    public void Arranged_order_beats_priority_and_the_first_one_is_the_focus()
    {
        Add("Low", Priority.Low);
        var high = Add("High", Priority.High);
        var low = _board.Items[0];
        Assert.Equal(["High", "Low"], Now());

        _board.ArrangeNow([low.Id, high.Id]);

        Assert.Equal(["Low", "High"], Now());
        Assert.Equal("Low", Agenda.Build(_board, T0).Focus!.Item.Title);
    }

    [Fact]
    public void Tasks_never_placed_slot_in_by_priority()
    {
        var a = Add("A");
        var b = Add("B", Priority.High);
        var c = Add("C", Priority.Low);
        _board.ArrangeNow([a.Id, b.Id, c.Id]);

        Add("Urgent", Priority.Critical);
        Add("Normal later");

        Assert.Equal(["Urgent", "A", "B", "Normal later", "C"], Now());
    }

    [Fact]
    public void A_due_reminder_still_jumps_ahead()
    {
        var a = Add("A");
        var b = Add("B");
        _board.ArrangeNow([a.Id, b.Id]);
        _board.AddReminder(b.Id, T0.AddMinutes(-1), "now!", Actor.User, T0.AddMinutes(-2));

        Assert.Equal(["B", "A"], Now());
    }

    [Fact]
    public void Arranging_one_group_keeps_the_other_groups_places()
    {
        var work = _board.Groups[0].Id;
        var personal = _board.Groups[1].Id;
        var w1 = Add("W1", group: work);
        var p1 = Add("P1", group: personal);
        var w2 = Add("W2", group: work);
        var p2 = Add("P2", group: personal);
        _board.ArrangeNow([w1.Id, p1.Id, w2.Id, p2.Id]);

        _board.ArrangeNow([p2.Id, p1.Id]);

        Assert.Equal(["W1", "P2", "W2", "P1"], Now());
        Assert.Equal(["P2", "P1"], Now(personal));
    }

    [Fact]
    public void Put_first_moves_tasks_to_the_top_in_the_given_order()
    {
        Add("A", Priority.Critical);
        var b = Add("B");
        var c = Add("C", Priority.Low);

        _board.PutFirst([c.Id, b.Id], T0);

        Assert.Equal(["C", "B", "A"], Now());
    }

    [Fact]
    public void Done_and_deleted_tasks_drop_out_of_the_order()
    {
        var a = Add("A");
        var b = Add("B");
        _board.ArrangeNow([b.Id, a.Id]);
        _board.Complete(b.Id, Actor.User, T0);
        _board.Delete(a.Id, Actor.User, T0);

        _board.ArrangeNow([]);

        Assert.Empty(_board.NowOrder);
    }

    [Fact]
    public void Arranging_rejects_unknown_or_repeated_tasks()
    {
        var a = Add("A");

        Assert.Throws<ArgumentException>(() => _board.ArrangeNow([a.Id, a.Id]));
        Assert.Throws<TaskNotFoundException>(() => _board.ArrangeNow([Guid.NewGuid()]));
    }

    [Fact]
    public void A_task_can_be_put_before_a_sibling_without_changing_its_parent()
    {
        var trip = Add("Trip");
        var flights = Add("Flights", parent: trip);
        var hotel = Add("Hotel", parent: trip);
        var visa = Add("Visa", parent: trip);

        _board.Reorder(visa.Id, before: flights.Id);
        Assert.Equal(["Visa", "Flights", "Hotel"], trip.Children.Select(c => c.Title));

        _board.Reorder(visa.Id, before: null);
        Assert.Equal(["Flights", "Hotel", "Visa"], trip.Children.Select(c => c.Title));
        Assert.Equal(trip, visa.Parent);
        Assert.Throws<ArgumentException>(() => _board.Reorder(visa.Id, before: trip.Id));
        Assert.DoesNotContain(_board.Activity, e => e.Kind == ActivityKind.Moved);
        _ = hotel;
    }

    [Fact]
    public void Top_level_tasks_can_be_reordered_too()
    {
        var a = Add("A");
        var b = Add("B");

        _board.Reorder(b.Id, before: a.Id);

        Assert.Equal(["B", "A"], _board.Items.Select(i => i.Title));
    }

    [Fact]
    public void The_order_survives_saving_and_loading()
    {
        var a = Add("A");
        var b = Add("B");
        _board.ArrangeNow([b.Id, a.Id]);

        var loaded = BoardSerializer.Deserialize(BoardSerializer.Serialize(_board));

        Assert.Equal([b.Id, a.Id], loaded.NowOrder);
    }
}
