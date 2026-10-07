namespace TodoTracker.Core;

public enum Priority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

public enum ActorKind
{
    User,
    Agent,
    Browser,
    Teams,
    System,

    /// <summary>Someone edited the markdown files directly (Obsidian, an editor, or an agent with file tools).</summary>
    Vault,
}

/// <summary>Who made a change. Used for attribution in notes and the activity timeline.</summary>
public sealed record Actor(ActorKind Kind, string? Name = null)
{
    public static readonly Actor User = new(ActorKind.User);
    public static readonly Actor System = new(ActorKind.System);

    public static Actor Agent(string name) => new(ActorKind.Agent, string.IsNullOrWhiteSpace(name) ? "agent" : name.Trim());

    public string DisplayName => Kind switch
    {
        ActorKind.User => "You",
        ActorKind.Vault => "Edited in files",
        _ when !string.IsNullOrWhiteSpace(Name) => $"{Kind}: {Name}",
        _ => Kind.ToString(),
    };
}

/// <summary>Derived, time-dependent state of an item. Never persisted.</summary>
public enum ItemState
{
    Actionable,
    Waiting,
    Locked,
    Container,
    Done,
}

public enum ActivityKind
{
    Created,
    Updated,
    NoteAdded,
    Completed,
    Reopened,
    Scheduled,
    ReminderAdded,
    ReminderFired,
    ReminderDismissed,
    Deleted,
    Moved,
    FocusStarted,
    FocusCompleted,
    GroupChanged,
    AttachmentAdded,
    AttachmentRemoved,
    LabelChanged,
    Archived,
    TimeLogged,
}

/// <summary>Where a top-level task is on the board. Done is <see cref="WorkItem.IsDone"/>, not a stage.</summary>
public enum Stage
{
    Inbox,
    Next,
    Doing,
}

/// <summary>How time was recorded: a timer started by hand (or typed in), or a focus (Pomodoro) session.</summary>
public enum TimeSource
{
    Manual,
    Focus,
}

public enum ReminderKind
{
    Manual,
    NextAction,
}

public sealed class TaskNotFoundException(string message) : KeyNotFoundException(message)
{
    public static TaskNotFoundException ForTask(Guid id) => new($"Task {id} was not found.");

    public static TaskNotFoundException ForGroup(Guid id) => new($"Group {id} was not found.");
}
