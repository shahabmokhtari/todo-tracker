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
    private readonly string _seenPath = Path.Combine(options.DataDirectory, "timer-seen.json");

    /// <summary>Longer than this between two sightings of a running timer: the app wasn't running (or the computer slept).</summary>
    private TimeSpan AbandonedAfter => TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(10).Ticks, options.TickInterval.Ticks * 3));

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await _dispatcher.DispatchDueAsync(now, cancellationToken).ConfigureAwait(false);
        var focusEvents = await store.UpdateAsync(b => b.TickPomodoro(now), cancellationToken).ConfigureAwait(false);
        // After sleep several transitions can happen at once; only the latest is worth a notification.
        if (focusEvents.Count > 0)
        {
            events.RaisePomodoro(focusEvents[^1]);
        }

        await WatchTimerAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Notes when this computer's timer was last seen running; a timer that wasn't seen for a long while (the app
    /// stopped, or the computer slept) ends when it was last seen, not hours later.
    /// </summary>
    private async Task WatchTimerAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var start = await store.ReadAsync(b => b.RunningTimer() is { } r && r.Entry.Device == TaskBoard.ThisDevice ? r.Entry.Start : (DateTimeOffset?)null, cancellationToken).ConfigureAwait(false);
        if (start is null)
        {
            return;
        }

        if (LastSeen() is { } seen && seen >= start && now - seen > AbandonedAfter)
        {
            await store.UpdateAsync(b => b.CloseAbandonedTimers(TaskBoard.ThisDevice, seen), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            Directory.CreateDirectory(options.DataDirectory);
            await File.WriteAllTextAsync(_seenPath, System.Text.Json.JsonSerializer.Serialize(now), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogTickFailed(logger, ex);
        }
    }

    private DateTimeOffset? LastSeen()
    {
        try
        {
            return File.Exists(_seenPath) ? System.Text.Json.JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(_seenPath)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
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
