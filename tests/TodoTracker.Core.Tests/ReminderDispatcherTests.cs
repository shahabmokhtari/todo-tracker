using TodoTracker.Core;

namespace TodoTracker.Core.Tests;

public class ReminderDispatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private sealed class RecordingNotifier : IReminderNotifier
    {
        public List<ReminderNotification> Received { get; } = [];

        public Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken)
        {
            Received.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IReminderNotifier
    {
        public Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken) =>
            throw new HttpRequestException("teams down");
    }

    [Fact]
    public async Task Dispatches_due_reminders_exactly_once()
    {
        var store = new InMemoryBoardStore();
        var ids = await store.UpdateAsync(b =>
        {
            var x = b.AddTask(new NewTask("Feature X") { Priority = Priority.High }, Actor.User, T0);
            var step = b.AddTask(new NewTask("Ring 1") { ParentId = x.Id }, Actor.User, T0);
            b.AddReminder(step.Id, T0.AddMinutes(-1), "Go", Actor.User, T0);
            b.AddReminder(step.Id, T0.AddHours(1), "Later", Actor.User, T0);
            return (x.Id, step.Id);
        });
        var notifier = new RecordingNotifier();
        var dispatcher = new ReminderDispatcher(store, [notifier]);

        var first = await dispatcher.DispatchDueAsync(T0);
        var second = await dispatcher.DispatchDueAsync(T0);

        var sent = Assert.Single(notifier.Received);
        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal("Go", sent.Message);
        Assert.Equal("Ring 1", sent.Title);
        Assert.Equal(["Feature X"], sent.Breadcrumb);
        Assert.Equal(Priority.High, sent.Priority);
        Assert.Equal(ids.Item2, sent.ItemId);
        Assert.True(await store.ReadAsync(b => b.Get(ids.Item2).Reminders[0].NotifiedAt == T0));
        Assert.True(await store.ReadAsync(b => b.Activity.Any(a => a.Kind == ActivityKind.ReminderFired)));
    }

    [Fact]
    public async Task Skips_done_and_dismissed_reminders()
    {
        var store = new InMemoryBoardStore();
        await store.UpdateAsync(b =>
        {
            var done = b.AddTask(new NewTask("Done"), Actor.User, T0);
            b.AddReminder(done.Id, T0, "x", Actor.User, T0);
            b.Complete(done.Id, Actor.User, T0);
            var dismissed = b.AddTask(new NewTask("Dismissed"), Actor.User, T0);
            var r = b.AddReminder(dismissed.Id, T0, "x", Actor.User, T0);
            b.DismissReminder(dismissed.Id, r.Id, Actor.User, T0);
        });
        var notifier = new RecordingNotifier();

        await new ReminderDispatcher(store, [notifier]).DispatchDueAsync(T0.AddHours(1));

        Assert.Empty(notifier.Received);
    }

    [Fact]
    public async Task A_failing_notifier_does_not_block_others()
    {
        var store = new InMemoryBoardStore();
        await store.UpdateAsync(b => b.AddReminder(b.AddTask(new NewTask("x"), Actor.User, T0).Id, T0, "go", Actor.User, T0));
        var notifier = new RecordingNotifier();
        var errors = new List<Exception>();

        await new ReminderDispatcher(store, [new ThrowingNotifier(), notifier], errors.Add).DispatchDueAsync(T0);

        Assert.Single(notifier.Received);
        Assert.IsType<HttpRequestException>(Assert.Single(errors));
    }
}
