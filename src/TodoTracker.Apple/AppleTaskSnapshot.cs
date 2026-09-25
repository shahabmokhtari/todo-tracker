using System.Globalization;
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
        var dueAt = item.EffectiveDueAt();
        var dueText = dueAt is { } value
            ? $" due {value.LocalDateTime.ToString("g", CultureInfo.InvariantCulture)}"
            : " with no due time";

        return new AppleTaskSnapshot(
            item.Id,
            item.Title,
            item.Priority.ToString(),
            item.Status.ToString(),
            $"{item.Priority} priority task {item.Title}{dueText}",
            dueAt);
    }
}
