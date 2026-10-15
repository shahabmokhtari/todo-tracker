using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoTracker.Server.Hooks;

/// <summary>
/// Something that happened, as hooks, the event stream (<c>GET /api/events</c>) and server plugins see it. Versioned:
/// fields are only ever added. <see cref="Id"/> is stable for the same happening (a hook can skip one it already saw).
/// </summary>
public sealed record TaskEvent(
    int Version,
    string Id,
    string Event,
    DateTimeOffset At,
    EventActor Actor,
    string Summary,
    EventTask? Task)
{
    public const int CurrentVersion = 1;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>Who did it: <c>user</c>, <c>agent</c> (with its name), <c>vault</c> (an edit in the files), <c>system</c>…</summary>
public sealed record EventActor(string Kind, string? Name);

/// <summary>The task it's about, as it is now (gone: only its id and title).</summary>
public sealed record EventTask(
    Guid Id,
    string Title,
    IReadOnlyList<string> Path,
    string? Group,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Labels,
    string Priority,
    DateTimeOffset? Deadline,
    bool Done,
    string? Repeat,
    string? Url);

/// <summary>The event names (camelCase) and what each means; Settings and docs/hooks.md list them.</summary>
public static class EventNames
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["taskCreated"] = "A task or subtask was added",
        ["taskUpdated"] = "Its title, details, priority, deadline, tags, look or repeat rule changed",
        ["taskCompleted"] = "Done (a repeating task: this time of it)",
        ["taskReopened"] = "Not done after all",
        ["taskDeleted"] = "Deleted",
        ["taskScheduled"] = "Snoozed, waiting for another task, or back",
        ["taskMoved"] = "Moved in the tree, to another group or on the board",
        ["taskArchived"] = "Put away",
        ["noteAdded"] = "A note was written on it",
        ["reminderAdded"] = "A reminder was set",
        ["reminderDue"] = "A reminder went off",
        ["reminderDismissed"] = "A reminder was dismissed",
        ["attachmentAdded"] = "A file or picture was attached",
        ["attachmentRemoved"] = "A file was removed",
        ["labelChanged"] = "Its labels changed",
        ["groupChanged"] = "A group (tab) was added, renamed or removed",
        ["timeLogged"] = "The timer started, or time was logged, corrected or removed",
        ["focusStarted"] = "A focus session started",
        ["focusEnded"] = "A focus session ended (a break starts)",
        ["breakEnded"] = "A break ended",
        ["test"] = "Sent from Settings › Hooks to try a hook",
    };
}
