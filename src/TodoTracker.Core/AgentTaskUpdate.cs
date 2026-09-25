namespace TodoTracker.Core;

public sealed record AgentTaskUpdate(
    Guid TaskId,
    string Note,
    DateTimeOffset? NextActionAt,
    TaskPriority? Priority);
