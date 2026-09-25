namespace TodoTracker.Core;

public enum PomodoroPhase
{
    Idle,
    Focus,
    ShortBreak,
    LongBreak,
}

public enum PomodoroEventKind
{
    FocusCompleted,
    BreakCompleted,
}

public sealed record PomodoroEvent(PomodoroEventKind Kind, Guid? ItemId, DateTimeOffset At);

public sealed record PomodoroSettings
{
    public PomodoroSettings(int focusMinutes = 25, int shortBreakMinutes = 5, int longBreakMinutes = 15, int focusesBeforeLongBreak = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(focusMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shortBreakMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(longBreakMinutes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(focusesBeforeLongBreak);
        FocusMinutes = focusMinutes;
        ShortBreakMinutes = shortBreakMinutes;
        LongBreakMinutes = longBreakMinutes;
        FocusesBeforeLongBreak = focusesBeforeLongBreak;
    }

    public int FocusMinutes { get; }

    public int ShortBreakMinutes { get; }

    public int LongBreakMinutes { get; }

    public int FocusesBeforeLongBreak { get; }

    public TimeSpan DurationOf(PomodoroPhase phase) => phase switch
    {
        PomodoroPhase.ShortBreak => TimeSpan.FromMinutes(ShortBreakMinutes),
        PomodoroPhase.LongBreak => TimeSpan.FromMinutes(LongBreakMinutes),
        _ => TimeSpan.FromMinutes(FocusMinutes),
    };
}

/// <summary>
/// Pomodoro state machine. Breaks start automatically after a focus session; a new focus session
/// always needs an explicit start so the user stays in control.
/// </summary>
public sealed class PomodoroTimer
{
    public PomodoroTimer(PomodoroSettings? settings = null)
    {
        Settings = settings ?? new PomodoroSettings();
    }

    public PomodoroSettings Settings { get; internal set; }

    public PomodoroPhase Phase { get; private set; } = PomodoroPhase.Idle;

    public DateTimeOffset? EndsAt { get; private set; }

    public TimeSpan? PausedRemaining { get; private set; }

    public Guid? ItemId { get; private set; }

    public int CompletedFocusCount { get; private set; }

    public bool IsRunning => EndsAt is not null;

    public TimeSpan Remaining(DateTimeOffset now)
    {
        if (EndsAt is { } endsAt)
        {
            var left = endsAt - now;
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        return PausedRemaining ?? Settings.DurationOf(Phase);
    }

    public void StartFocus(DateTimeOffset now, Guid? itemId = null)
    {
        Phase = PomodoroPhase.Focus;
        ItemId = itemId;
        PausedRemaining = null;
        EndsAt = now + Settings.DurationOf(PomodoroPhase.Focus);
    }

    public void Pause(DateTimeOffset now)
    {
        if (EndsAt is null || Phase == PomodoroPhase.Idle)
        {
            return;
        }

        PausedRemaining = Remaining(now);
        EndsAt = null;
    }

    public void Resume(DateTimeOffset now)
    {
        if (PausedRemaining is not { } remaining || Phase == PomodoroPhase.Idle)
        {
            return;
        }

        EndsAt = now + remaining;
        PausedRemaining = null;
    }

    public void Reset()
    {
        Phase = PomodoroPhase.Idle;
        EndsAt = null;
        PausedRemaining = null;
        ItemId = null;
        CompletedFocusCount = 0;
    }

    /// <summary>Skips the current phase: focus goes to a (short) break without counting; a break ends.</summary>
    public void Skip(DateTimeOffset now)
    {
        if (Phase == PomodoroPhase.Focus)
        {
            BeginBreak(PomodoroPhase.ShortBreak, now);
        }
        else if (Phase != PomodoroPhase.Idle)
        {
            GoIdle();
        }
    }

    /// <summary>Advances at most one phase transition. Call repeatedly to catch up after sleep.</summary>
    public PomodoroEvent? Tick(DateTimeOffset now)
    {
        if (EndsAt is not { } endsAt || now < endsAt)
        {
            return null;
        }

        if (Phase == PomodoroPhase.Focus)
        {
            CompletedFocusCount++;
            var item = ItemId;
            var breakPhase = CompletedFocusCount % Settings.FocusesBeforeLongBreak == 0 ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak;
            BeginBreak(breakPhase, endsAt);
            return new PomodoroEvent(PomodoroEventKind.FocusCompleted, item, endsAt);
        }

        var itemId = ItemId;
        GoIdle();
        return new PomodoroEvent(PomodoroEventKind.BreakCompleted, itemId, endsAt);
    }

    internal void DetachItem() => ItemId = null;

    internal void Restore(PomodoroPhase phase, DateTimeOffset? endsAt, TimeSpan? pausedRemaining, Guid? itemId, int completedFocusCount)
    {
        Phase = phase;
        EndsAt = phase == PomodoroPhase.Idle ? null : endsAt;
        PausedRemaining = phase == PomodoroPhase.Idle || endsAt is not null ? null : pausedRemaining;
        ItemId = itemId;
        CompletedFocusCount = Math.Max(0, completedFocusCount);
    }

    private void BeginBreak(PomodoroPhase phase, DateTimeOffset from)
    {
        Phase = phase;
        PausedRemaining = null;
        EndsAt = from + Settings.DurationOf(phase);
    }

    private void GoIdle()
    {
        Phase = PomodoroPhase.Idle;
        EndsAt = null;
        PausedRemaining = null;
    }
}
