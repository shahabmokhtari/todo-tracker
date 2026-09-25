namespace TodoTracker.Core;

public sealed class WorkItem
{
    private readonly List<WorkItem> _subtasks = [];
    private readonly List<Reminder> _reminders = [];
    private readonly List<NoteEntry> _notes = [];

    public WorkItem(string title, TaskPriority priority = TaskPriority.Normal)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("A task title is required.", nameof(title));
        }

        Id = Guid.NewGuid();
        Title = title.Trim();
        Priority = priority;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }

    public string Title { get; private set; }

    public TaskPriority Priority { get; private set; }

    public WorkItemStatus Status { get; private set; } = WorkItemStatus.Active;

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? Deadline { get; private set; }

    public DateTimeOffset? NextActionAt { get; private set; }

    public string? Detail { get; private set; }

    public IReadOnlyList<WorkItem> Subtasks => _subtasks;

    public IReadOnlyList<Reminder> Reminders => _reminders;

    public IReadOnlyList<NoteEntry> Notes => _notes;

    public WorkItem AddSubtask(string title, TaskPriority priority = TaskPriority.Normal)
    {
        var subtask = new WorkItem(title, priority);
        _subtasks.Add(subtask);
        return subtask;
    }

    public void SetPriority(TaskPriority priority) => Priority = priority;

    public void SetDeadline(DateTimeOffset? deadline) => Deadline = deadline;

    public void SetDetail(string? detail) => Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();

    public void AddNote(string text, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("A note cannot be empty.", nameof(text));
        }

        _notes.Add(new NoteEntry(createdAt, text.Trim()));
    }

    public void AddReminder(DateTimeOffset dueAt, string message, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A reminder message is required.", nameof(message));
        }

        _reminders.Add(new Reminder(dueAt, message.Trim()));

        var currentTime = now ?? DateTimeOffset.UtcNow;
        if (Status != WorkItemStatus.Completed)
        {
            Status = _reminders.Any(reminder => reminder.IsDue(currentTime))
                ? WorkItemStatus.Active
                : WorkItemStatus.Waiting;
        }
    }

    public bool DismissReminder(Reminder reminder)
    {
        return DismissReminder(reminder.Id);
    }

    public bool DismissReminder(Guid reminderId)
    {
        var index = _reminders.FindIndex(reminder => reminder.Id == reminderId);
        if (index < 0)
        {
            return false;
        }

        _reminders[index] = _reminders[index].Dismiss();
        return true;
    }

    public void ScheduleNextAction(DateTimeOffset dueAt)
    {
        NextActionAt = dueAt;
        Status = WorkItemStatus.Waiting;
    }

    public void ScheduleNextActionAfter(TimeSpan delay, DateTimeOffset from)
    {
        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Delay cannot be negative.");
        }

        ScheduleNextAction(from.Add(delay));
    }

    public void MarkActive() => Status = WorkItemStatus.Active;

    public void Complete() => Status = WorkItemStatus.Completed;

    public void RefreshStatus(DateTimeOffset now)
    {
        if (Status == WorkItemStatus.Completed)
        {
            return;
        }

        Status = IsActionable(now) ? WorkItemStatus.Active : WorkItemStatus.Waiting;
    }

    public bool IsActionable(DateTimeOffset now)
    {
        if (Status == WorkItemStatus.Completed)
        {
            return false;
        }

        if (_reminders.Any(reminder => reminder.IsDue(now)))
        {
            return true;
        }

        if (NextActionAt is not null)
        {
            return NextActionAt <= now;
        }

        return _reminders.All(reminder => reminder.IsDismissed);
    }

    public DateTimeOffset? EffectiveDueAt()
    {
        var reminderDueAt = _reminders
            .Where(reminder => !reminder.IsDismissed)
            .Select(reminder => (DateTimeOffset?)reminder.DueAt)
            .Min();

        return EarliestNonNull(NextActionAt, reminderDueAt, Deadline);
    }

    private static DateTimeOffset? EarliestNonNull(params DateTimeOffset?[] values)
    {
        var nonNullValues = values
            .Where(value => value.HasValue)
            .Select(value => value!.Value);

        return nonNullValues.Any() ? nonNullValues.Min() : null;
    }
}
