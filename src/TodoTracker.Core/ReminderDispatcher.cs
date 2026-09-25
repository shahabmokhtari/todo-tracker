namespace TodoTracker.Core;

public sealed record ReminderNotification(
    Guid ItemId,
    Guid ReminderId,
    string Title,
    IReadOnlyList<string> Breadcrumb,
    string Message,
    Priority Priority,
    DateTimeOffset DueAt,
    Guid GroupId);

public interface IReminderNotifier
{
    Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken);
}

/// <summary>
/// Finds due reminders that have not been delivered yet, marks them delivered (at-most-once), then fans
/// them out to every notifier (desktop toast, Teams, ...). A failing notifier never blocks the others.
/// </summary>
public sealed class ReminderDispatcher(IBoardStore store, IEnumerable<IReminderNotifier> notifiers, Action<Exception>? onError = null)
{
    private readonly IReadOnlyList<IReminderNotifier> _notifiers = notifiers.ToList();

    public async Task<IReadOnlyList<ReminderNotification>> DispatchDueAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var due = await store.UpdateAsync(board =>
        {
            var list = new List<ReminderNotification>();
            foreach (var item in board.AllItems().Where(i => !i.IsDone))
            {
                foreach (var reminder in item.Reminders.Where(r => r.IsDue(now) && r.NotifiedAt is null))
                {
                    board.MarkReminderNotified(item, reminder, now);
                    list.Add(new ReminderNotification(
                        item.Id,
                        reminder.Id,
                        item.Title,
                        item.Ancestors().Reverse().Select(a => a.Title).ToList(),
                        reminder.Message,
                        Agenda.EffectivePriority(item),
                        reminder.DueAt,
                        item.GroupId));
                }
            }

            return list;
        }, cancellationToken).ConfigureAwait(false);

        foreach (var notification in due)
        {
            foreach (var notifier in _notifiers)
            {
                try
                {
                    await notifier.NotifyAsync(notification, cancellationToken).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // A broken integration (e.g. Teams offline) must not stop local reminders.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    onError?.Invoke(ex);
                }
            }
        }

        return due;
    }
}
