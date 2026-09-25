namespace TodoTracker.Core;

/// <summary>A task or subtask. Mutations go through <see cref="TaskBoard"/> so they are validated and logged.</summary>
public sealed class WorkItem
{
    internal readonly List<WorkItem> ChildList = [];
    internal readonly List<Reminder> ReminderList = [];
    internal readonly List<Note> NoteList = [];

    internal WorkItem(Guid id, string title, Priority priority, DateTimeOffset createdAt)
    {
        Id = id;
        Title = title;
        Priority = priority;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Title { get; internal set; }

    public string? Details { get; internal set; }

    public Priority Priority { get; internal set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public DateTimeOffset? Deadline { get; internal set; }

    /// <summary>The item is deferred (waiting) until this time.</summary>
    public DateTimeOffset? NextActionAt { get; internal set; }

    /// <summary>Children are steps that must be completed in order.</summary>
    public bool Sequential { get; internal set; }

    /// <summary>For sequential items: minimum wait between finishing one step and starting the next.</summary>
    public TimeSpan? StepDelay { get; internal set; }

    /// <summary>Group (tab) of the root task. Children always report their root's group.</summary>
    public Guid GroupId => Parent?.GroupId ?? OwnGroupId;

    internal Guid OwnGroupId { get; set; }

    public WorkItem? Parent { get; internal set; }

    public IReadOnlyList<WorkItem> Children => ChildList;

    public IReadOnlyList<Reminder> Reminders => ReminderList;

    public IReadOnlyList<Note> Notes => NoteList;

    public bool IsDone => CompletedAt is not null;

    public bool HasOpenChildren => ChildList.Exists(c => !c.IsDone);

    /// <summary>Titles from the root down to and including this item.</summary>
    public IReadOnlyList<string> Path => Ancestors().Reverse().Select(a => a.Title).Append(Title).ToList();

    public IEnumerable<WorkItem> Ancestors()
    {
        for (var current = Parent; current is not null; current = current.Parent)
        {
            yield return current;
        }
    }

    public IEnumerable<WorkItem> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in ChildList)
        {
            foreach (var descendant in child.SelfAndDescendants())
            {
                yield return descendant;
            }
        }
    }

    public WorkItem Root => Ancestors().LastOrDefault() ?? this;
}

public sealed record NewTask(string Title)
{
    public Guid? ParentId { get; init; }

    public Guid? GroupId { get; init; }

    public Priority Priority { get; init; } = Priority.Normal;

    public string? Details { get; init; }

    public DateTimeOffset? Deadline { get; init; }

    public bool Sequential { get; init; }

    public TimeSpan? StepDelay { get; init; }
}

public sealed record TaskChanges
{
    public string? Title { get; init; }

    public string? Details { get; init; }

    public Priority? Priority { get; init; }

    public DateTimeOffset? Deadline { get; init; }

    public bool ClearDeadline { get; init; }

    public bool? Sequential { get; init; }

    public TimeSpan? StepDelay { get; init; }

    public bool ClearStepDelay { get; init; }
}
