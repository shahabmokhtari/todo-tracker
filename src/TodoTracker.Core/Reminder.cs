namespace TodoTracker.Core;

public sealed record Reminder(DateTimeOffset DueAt, string Message, bool IsDismissed = false)
{
    public bool IsDue(DateTimeOffset now) => !IsDismissed && DueAt <= now;

    public Reminder Dismiss() => this with { IsDismissed = true };
}
