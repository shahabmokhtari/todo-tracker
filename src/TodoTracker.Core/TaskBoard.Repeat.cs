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
    /// it's simply done.
    /// </summary>
    internal bool TryRollForward(WorkItem item, Actor actor, DateTimeOffset now)
    {
        if (item.Repeat is not { } rule || rule.Count == 1)
        {
            return false;
        }

        var today = LocalDate(now);
        var anchor = rule.WhenDone || (item.Deadline ?? item.NextActionAt) is not { } scheduled ? today : LocalDate(scheduled);
        if (rule.Next(anchor, anchor > today ? anchor : today) is not { } next)
        {
            return false;
        }

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
        Log(now, item.Id, ActivityKind.Completed, $"Completed \"{item.Title}\" (repeats: next {next.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture)})", actor);
        return true;
    }

    /// <summary>Whether this repeating task's last change was moving on to its next time (so "not done after all" can undo it).</summary>
    internal bool WasRolledForward(WorkItem item) =>
        item is { Repeat: not null, IsDone: false }
        && ActivityList.FindLast(a => a.ItemId == item.Id && a.Kind is ActivityKind.Completed or ActivityKind.Reopened) is { Kind: ActivityKind.Completed };

    /// <summary>"Not done after all" on a repeating task that moved on: back to the time before (due a time earlier), in Do now.</summary>
    private void UndoRollForward(WorkItem item, Actor actor, DateTimeOffset now)
    {
        if (item.Deadline is { } due && item.Repeat!.Previous(LocalDate(due), LocalDate(due)) is { } before)
        {
            item.Deadline = LocalAt(before, LocalTime(due));
        }

        BringBack(item, now);
        item.NextActionAt = null;
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
