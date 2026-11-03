using OneThing.Core;

namespace OneThing.Core.Tests;

/// <summary>Every change says who made it: arranging, energy, timers and focus land on the timeline with their actor.</summary>
public class AttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
    private static readonly Actor Bot = Actor.Agent("helper");
    private readonly TaskBoard _board = new();

    private WorkItem Task(string title, Guid? parent = null) => _board.AddTask(new NewTask(title) { ParentId = parent }, Actor.User, T0);

    private ActivityEntry Last => _board.Activity[^1];

    [Fact]
    public void Arranging_do_now_says_who_arranged_it()
    {
        var a = Task("A");
        var b = Task("B");

        _board.ArrangeNow([b.Id, a.Id], Bot, T0.AddMinutes(1));

        Assert.Equal(Bot, Last.Actor);
        Assert.Equal(ActivityKind.Moved, Last.Kind);
        Assert.Contains("\"B\"", Last.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Putting_a_task_first_is_said_once()
    {
        var a = Task("A");
        Task("B");
        var before = _board.Activity.Count;

        _board.PutFirst([a.Id], Bot, T0.AddMinutes(1));

        Assert.Equal(before + 1, _board.Activity.Count);
        Assert.Equal("Put \"A\" first", Last.Summary);
    }

    [Fact]
    public void Reordering_siblings_says_who_moved_the_task()
    {
        var trip = Task("Trip");
        var flights = Task("Flights", trip.Id);
        var visa = Task("Visa", trip.Id);

        _board.Reorder(visa.Id, flights.Id, Bot, T0.AddMinutes(1));
        Assert.Equal((Bot, visa.Id, ActivityKind.Moved), (Last.Actor, Last.ItemId, Last.Kind));

        _board.ReorderAfter(visa.Id, flights.Id, Actor.User, T0.AddMinutes(2));
        Assert.Equal((Actor.User, visa.Id), (Last.Actor, Last.ItemId));
    }

    [Fact]
    public void Changing_a_days_energy_says_who_changed_it()
    {
        _board.SetEnergyPerDay(6, Bot, T0);

        Assert.Equal(Bot, Last.Actor);
        Assert.Equal(Guid.Empty, Last.ItemId);
        Assert.Contains("6", Last.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopping_the_timer_says_who_stopped_it()
    {
        var item = Task("Write report");
        _board.StartTimer(item.Id, Actor.User, T0);

        _board.StopTimer(Bot, T0.AddMinutes(25));

        Assert.Equal((Bot, item.Id, ActivityKind.TimeLogged), (Last.Actor, Last.ItemId, Last.Kind));
        Assert.Contains("Stopped the timer", Last.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Stopping_no_timer_changes_nothing_on_the_timeline()
    {
        Task("Idle");
        var before = _board.Activity.Count;

        Assert.Null(_board.StopTimer(Bot, T0));
        Assert.Equal(before, _board.Activity.Count);
    }

    [Fact]
    public void Pausing_resuming_and_ending_focus_say_who_did_it()
    {
        var item = Task("Deep work");
        _board.StartFocus(item.Id, Actor.User, T0);

        _board.PauseFocus(Bot, T0.AddMinutes(5));
        Assert.Equal((Bot, item.Id), (Last.Actor, Last.ItemId));
        _board.ResumeFocus(Actor.User, T0.AddMinutes(6));
        Assert.Equal((Actor.User, item.Id), (Last.Actor, Last.ItemId));
        _board.SkipFocus(Bot, T0.AddMinutes(10));
        Assert.Equal((Bot, item.Id), (Last.Actor, Last.ItemId));

        _board.StartFocus(item.Id, Actor.User, T0.AddMinutes(20));
        _board.ResetFocus(Bot, T0.AddMinutes(22));
        Assert.Equal((Bot, item.Id), (Last.Actor, Last.ItemId));
    }

    [Fact]
    public void Timers_the_app_ends_by_itself_are_put_down_to_the_app()
    {
        var item = Task("Forgot me");
        _board.StartTimer(item.Id, Actor.User, T0, "laptop");

        Assert.Equal(1, _board.CloseAbandonedTimers("laptop", T0.AddMinutes(30)));
        Assert.Equal((Actor.System, item.Id, ActivityKind.TimeLogged), (Last.Actor, Last.ItemId, Last.Kind));

        _board.StartTimer(item.Id, Actor.User, T0.AddHours(1), "desk");
        Assert.Equal(1, _board.CloseForgottenTimers(T0.AddDays(2)));
        Assert.Equal((Actor.System, item.Id), (Last.Actor, Last.ItemId));
    }
}
