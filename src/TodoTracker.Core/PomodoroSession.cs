namespace TodoTracker.Core;

public sealed class PomodoroSession
{
    public PomodoroSession(TimeSpan focusLength, TimeSpan breakLength)
    {
        if (focusLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(focusLength), "Focus length must be positive.");
        }

        if (breakLength <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(breakLength), "Break length must be positive.");
        }

        FocusLength = focusLength;
        BreakLength = breakLength;
    }

    public TimeSpan FocusLength { get; }

    public TimeSpan BreakLength { get; }

    public DateTimeOffset? StartedAt { get; private set; }

    public bool IsBreak { get; private set; }

    public void Start(DateTimeOffset startedAt)
    {
        StartedAt = startedAt;
        IsBreak = false;
    }

    public void StartBreak(DateTimeOffset startedAt)
    {
        StartedAt = startedAt;
        IsBreak = true;
    }

    public TimeSpan Remaining(DateTimeOffset now)
    {
        if (StartedAt is null)
        {
            return IsBreak ? BreakLength : FocusLength;
        }

        var length = IsBreak ? BreakLength : FocusLength;
        var remaining = length - (now - StartedAt.Value);
        return remaining <= TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
