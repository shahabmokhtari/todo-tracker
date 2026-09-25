using TodoTracker.Core;

namespace TodoTracker.Apple;

public sealed record AppleTaskSnapshot(
    Guid Id,
    string Title,
    string Priority,
    string Status,
    string AccessibilityLabel,
    DateTimeOffset? DueAt)
{
    public static AppleTaskSnapshot From(WorkItem item)
    {
        var dueText = item.EffectiveDueAt() is { } dueAt
            ? $" due {dueAt.LocalDateTime:g}"
            : " with no due time";

        return new AppleTaskSnapshot(
            item.Id,
            item.Title,
            item.Priority.ToString(),
            item.Status.ToString(),
            $"{item.Priority} priority task {item.Title}{dueText}",
            item.EffectiveDueAt());
    }
}
