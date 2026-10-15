namespace TodoTracker.Core;

/// <summary>Repeating tasks: one task that moves on to its next time when it's done (no copies piling up).</summary>
public sealed partial class TaskBoard
{
    /// <summary>Where the user lives: a repeating task's next time is a calendar day there.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Local;

    private static Recurrence? ParseRepeat(string? text) => string.IsNullOrWhiteSpace(text) ? null : Recurrence.Parse(text);

    /// <summary>A rule written by hand (or by another app) that isn't understood is left out rather than failing the load.</summary>
    internal static Recurrence? ReadRepeat(string? text) => Recurrence.TryParse(text, out var rule, out _) ? rule : null;

    /// <summary>
    /// Done with this time of a repeating task: it moves on to its next time instead of finishing. Its deadline moves to
    /// that day (same time of day), it waits until then (the same number of days before the deadline, and at the same
    /// time, as it waited before; otherwise from the start of that day), its subtasks open again and what's left of this
    /// time (its reminders, its timer, Doing on the board) ends. Returns false when it doesn't repeat (any more): then
    /// it's simply done. A step of an in-order task doesn't repeat on its own (its task can): it would hold up the steps
    /// after it for good.
    /// </summary>
    internal bool TryRollForward(WorkItem item, Actor actor, DateTimeOffset now)
    {
        if (item.Repeat is not { } repeat || repeat.Count == 1 || item.Parent is { Sequential: true })
        {
            return false;
        }

        var today = LocalDate(now);
        var anchor = repeat.WhenDone || (item.Deadline ?? item.NextActionAt) is not { } scheduled ? today : LocalDate(scheduled);

        // Its day is fixed from its first time on (month ends, leap days and snoozes don't move it).
        var rule = repeat.PinnedTo(anchor);
        if (rule.Next(anchor, anchor > today ? anchor : today) is not { } next)
        {
            return false;
        }

        item.RepeatUndo = new RepeatUndo(item.Deadline, item.NextActionAt);
        var waitedUntil = item.NextActionAt;
        var reminded = waitedUntil is { } w && item.ReminderList.Exists(r => r.Kind == ReminderKind.NextAction && r.DueAt == w);
        var lead = item.Deadline is { } due && waitedUntil is { } waited ? Math.Max(0, LocalDate(due).DayNumber - LocalDate(waited).DayNumber) : 0;
        if (item.Deadline is { } deadline)
        {
            item.Deadline = LocalAt(next, LocalTime(deadline));
        }

        var back = waitedUntil is { } before ? LocalAt(next.AddDays(-lead), LocalTime(before)) : LocalAt(next, TimeOnly.MinValue);
        item.NextActionAt = back;
        item.AfterId = null;
        DismissPendingScheduleReminders(item, now);
        foreach (var reminder in item.ReminderList.Where(r => r.DismissedAt is null && r.DueAt < back))
        {
            reminder.DismissedAt = now;
        }

        if (reminded && back > now)
        {
            item.ReminderList.Add(new Reminder(Guid.NewGuid(), back, DefaultReminderMessage(item), ReminderKind.NextAction));
        }

        foreach (var descendant in item.SelfAndDescendants().Skip(1))
        {
            descendant.CompletedAt = null;
        }

        StopTimers(item.SelfAndDescendants(), now);
        if (item.Parent is null && item.Stage == Stage.Doing)
        {
            item.Stage = Stage.Next;
        }

        item.Repeat = rule.CountedDown();
        Log(now, item.Id, ActivityKind.Completed, $"Completed \"{item.Title}\" {RolledMarker}{next.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)})", actor);
        return true;
    }

    /// <summary>What every app writes in the "completed" entry of a repeating task that moved on (the task stays open).</summary>
    internal const string RolledMarker = "(repeats: next ";

    internal static bool IsRolled(ActivityEntry entry) => entry.Kind == ActivityKind.Completed && entry.Summary.Contains(RolledMarker, StringComparison.Ordinal);

    /// <summary>Whether this repeating task's last change was moving on to its next time (so "not done after all" can undo it).</summary>
    internal bool WasRolledForward(WorkItem item) =>
        item is { Repeat: not null, IsDone: false }
        && ActivityList.FindLast(a => a.ItemId == item.Id && a.Kind is ActivityKind.Completed or ActivityKind.Reopened) is { Kind: ActivityKind.Completed };

    /// <summary>
    /// "Not done after all" on a repeating task that moved on: its deadline and wait as they were before (without that
    /// record, e.g. written by an older app: due a time earlier and back in Do now).
    /// </summary>
    private void UndoRollForward(WorkItem item, Actor actor, DateTimeOffset now)
    {
        foreach (var reminder in item.ReminderList.Where(r => r.Kind == ReminderKind.NextAction && r.DismissedAt is null && r.DueAt > now))
        {
            reminder.DismissedAt = now;
        }

        if (item.RepeatUndo is { } undo)
        {
            item.Deadline = undo.Deadline;
            item.NextActionAt = undo.NextActionAt;
        }
        else
        {
            if (item.Deadline is { } due && item.Repeat!.Previous(LocalDate(due), LocalDate(due)) is { } before)
            {
                item.Deadline = LocalAt(before, LocalTime(due));
            }

            item.NextActionAt = null;
        }

        item.RepeatUndo = null;
        item.Repeat = item.Repeat!.CountedUp();
        Log(now, item.Id, ActivityKind.Reopened, $"Reopened \"{item.Title}\" (not done after all)", actor);
    }

    private DateOnly LocalDate(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZone).DateTime);

    private TimeOnly LocalTime(DateTimeOffset at) => TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZone).DateTime);

    private DateTimeOffset LocalAt(DateOnly day, TimeOnly time)
    {
        var wall = day.ToDateTime(time);

        // A time the clocks skip (spring forward) is read an hour later.
        if (TimeZone.IsInvalidTime(wall))
        {
            wall = wall.AddHours(1);
        }

        return new DateTimeOffset(wall, TimeZone.GetUtcOffset(wall));
    }
}
