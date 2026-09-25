using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TodoTracker.Core;

namespace TodoTracker.Server;

/// <summary>In-process events so a host (e.g. the Windows sidebar) can show native notifications.</summary>
public sealed class ServerEvents
{
    public event EventHandler<ReminderNotification>? Reminder;

    public event EventHandler<PomodoroEvent>? Pomodoro;

    internal void RaiseReminder(ReminderNotification notification) => Reminder?.Invoke(this, notification);

    internal void RaisePomodoro(PomodoroEvent evt) => Pomodoro?.Invoke(this, evt);
}

internal sealed class EventNotifier(ServerEvents events) : IReminderNotifier
{
    public Task NotifyAsync(ReminderNotification notification, CancellationToken cancellationToken)
    {
        events.RaiseReminder(notification);
        return Task.CompletedTask;
    }
}

/// <summary>Periodically delivers due reminders and advances the Pomodoro timer.</summary>
public sealed partial class ReminderLoop(
    IBoardStore store,
    IEnumerable<IReminderNotifier> notifiers,
    ServerEvents events,
    TimeProvider time,
    TodoTrackerServerOptions options,
    ILogger<ReminderLoop> logger) : BackgroundService
{
    private readonly ReminderDispatcher _dispatcher = new(store, notifiers, ex => LogNotifierFailed(logger, ex));

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await _dispatcher.DispatchDueAsync(now, cancellationToken).ConfigureAwait(false);
        var focusEvents = await store.UpdateAsync(b => b.TickPomodoro(now), cancellationToken).ConfigureAwait(false);
        foreach (var evt in focusEvents)
        {
            events.RaisePomodoro(evt);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnableBackgroundLoop)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.TickInterval, time);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Keep the loop alive; the next tick retries.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogTickFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A reminder notifier failed")]
    private static partial void LogNotifierFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reminder loop tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
