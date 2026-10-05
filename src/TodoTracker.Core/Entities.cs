namespace TodoTracker.Core;

public sealed class Reminder
{
    internal Reminder(Guid id, DateTimeOffset dueAt, string message, ReminderKind kind)
    {
        Id = id;
        DueAt = dueAt;
        Message = message;
        Kind = kind;
    }

    public Guid Id { get; }

    public DateTimeOffset DueAt { get; }

    public string Message { get; }

    public ReminderKind Kind { get; }

    public DateTimeOffset? NotifiedAt { get; internal set; }

    public DateTimeOffset? DismissedAt { get; internal set; }

    public bool IsPending => DismissedAt is null;

    public bool IsDue(DateTimeOffset now) => DismissedAt is null && DueAt <= now;
}

public sealed class Note
{
    internal Note(Guid id, DateTimeOffset at, string text, Actor author, string? sourceUrl, string? sourceTitle)
    {
        Id = id;
        At = at;
        Text = text;
        Author = author;
        SourceUrl = sourceUrl;
        SourceTitle = sourceTitle;
    }

    public Guid Id { get; }

    public DateTimeOffset At { get; }

    public string Text { get; internal set; }

    public Actor Author { get; }

    public string? SourceUrl { get; }

    public string? SourceTitle { get; }
}

public sealed record ActivityEntry(DateTimeOffset At, Guid ItemId, ActivityKind Kind, string Summary, Actor Actor);

/// <summary>A file attached to a task. The bytes live in the store; <see cref="Path"/> is relative to the store root.</summary>
public sealed class Attachment
{
    internal Attachment(Guid id, string fileName, string path, long size, DateTimeOffset addedAt, Actor addedBy)
    {
        Id = id;
        FileName = fileName;
        Path = path;
        Size = size;
        AddedAt = addedAt;
        AddedBy = addedBy;
    }

    public Guid Id { get; }

    public string FileName { get; }

    /// <summary>Location relative to the store root, with forward slashes (e.g. <c>_attachments/7b0c2f9e/plan.pdf</c>).</summary>
    public string Path { get; internal set; }

    public long Size { get; }

    public DateTimeOffset AddedAt { get; }

    public Actor AddedBy { get; }
}

/// <summary>A curated, colored label (tags are free-form; labels are the few the user picks from).</summary>
public sealed class LabelDefinition
{
    internal LabelDefinition(string name, string color)
    {
        Name = name;
        Color = color;
    }

    public string Name { get; internal set; }

    /// <summary><c>#rrggbb</c>.</summary>
    public string Color { get; internal set; }
}

public sealed class TaskGroup
{
    internal TaskGroup(Guid id, string name, string? color)
    {
        Id = id;
        Name = name;
        Color = color;
    }

    public Guid Id { get; }

    public string Name { get; internal set; }

    /// <summary>Optional <c>#rrggbb</c> accent color for the group tab.</summary>
    public string? Color { get; internal set; }
}
