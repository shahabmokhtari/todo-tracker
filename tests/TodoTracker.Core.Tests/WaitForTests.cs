using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

/// <summary>Snoozing a task until another one is done ("after task X").</summary>
public sealed class WaitForTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private readonly TaskBoard _board = new();

    private WorkItem Add(string title) => _board.AddTask(new NewTask(title), Actor.User, T0);

    [Fact]
    public void A_task_waits_until_the_other_one_is_done_then_comes_back_with_a_reminder()
    {
        var quote = Add("Get the quote");
        var order = Add("Order the parts");

        _board.WaitFor(order.Id, quote.Id, Actor.User, T0);

        Assert.Equal(ItemState.Waiting, Agenda.StateOf(order, T0));
        var waiting = Agenda.Build(_board, T0).Waiting.Single();
        Assert.Equal(order.Id, waiting.Item.Id);
        Assert.Equal(quote.Id, waiting.WaitingFor?.Id);
        Assert.Null(waiting.WakeAt);

        _board.Complete(quote.Id, Actor.User, T0.AddHours(1));

        Assert.Null(order.AfterId);
        var back = Agenda.Build(_board, T0.AddHours(1));
        Assert.Equal(order.Id, back.Focus?.Item.Id);
        Assert.True(back.Focus!.NeedsAttention);
        Assert.Contains("Get the quote", back.Focus.DueReminder!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bringing_it_back_or_deleting_the_other_task_ends_the_wait()
    {
        var a = Add("A");
        var b = Add("B");
        var c = Add("C");
        _board.WaitFor(b.Id, a.Id, Actor.User, T0);
        _board.WaitFor(c.Id, a.Id, Actor.User, T0);

        _board.ClearNextAction(b.Id, Actor.User, T0);
        Assert.Null(b.AfterId);
        Assert.Equal(ItemState.Actionable, Agenda.StateOf(b, T0));

        _board.Delete(a.Id, Actor.User, T0);
        Assert.Null(c.AfterId);
        Assert.Equal(ItemState.Actionable, Agenda.StateOf(c, T0));
    }

    [Fact]
    public void Waiting_for_itself_a_done_task_or_in_a_circle_is_refused()
    {
        var a = Add("A");
        var b = Add("B");
        var done = Add("Done already");
        _board.Complete(done.Id, Actor.User, T0);
        _board.WaitFor(b.Id, a.Id, Actor.User, T0);

        Assert.Throws<ArgumentException>(() => _board.WaitFor(a.Id, a.Id, Actor.User, T0));
        Assert.Throws<InvalidOperationException>(() => _board.WaitFor(a.Id, b.Id, Actor.User, T0));
        Assert.Throws<InvalidOperationException>(() => _board.WaitFor(a.Id, done.Id, Actor.User, T0));
    }

    [Fact]
    public void Subtasks_of_a_task_that_waits_wait_too()
    {
        var a = Add("A");
        var trip = Add("Trip");
        var step = _board.AddTask(new NewTask("Book") { ParentId = trip.Id }, Actor.User, T0);

        _board.WaitFor(trip.Id, a.Id, Actor.User, T0);

        Assert.Equal(ItemState.Waiting, Agenda.StateOf(step, T0));
        Assert.Equal([trip.Id], Agenda.Build(_board, T0).Waiting.Select(e => e.Item.Id));
    }

    [Fact]
    public void The_wait_is_kept_in_the_board_file()
    {
        var a = Add("A");
        var b = Add("B");
        _board.WaitFor(b.Id, a.Id, Actor.User, T0);

        var again = BoardSerializer.Deserialize(BoardSerializer.Serialize(_board));

        Assert.Equal(a.Id, again.Get(b.Id).AfterId);
        Assert.Equal(ItemState.Waiting, Agenda.StateOf(again.Get(b.Id), T0));
    }
}
