using System.Globalization;
using TodoTracker.Core;

namespace TodoTracker.Cli;

/// <summary>
/// Finds the task a person or agent means: a full id, an id prefix (as printed by <c>tt</c>, 6+ hex digits), or words
/// from its title. An exact title beats a partial one, and <paramref name="prefer"/> (e.g. open tasks for <c>done</c>)
/// breaks ties. Anything ambiguous is an error that lists the candidates, never a guess.
/// </summary>
internal static class TaskResolver
{
    private const int MinPrefix = 6;
    private const int MaxListed = 8;

    public static WorkItem Resolve(TaskBoard board, string reference, Func<WorkItem, bool>? prefer = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        var text = (reference ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw new ArgumentException("Say which task.");
        }

        if (Guid.TryParse(text, out var id))
        {
            return board.Find(id) ?? throw new ArgumentException($"No task has id {text}.");
        }

        var hex = text.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (hex.Length >= MinPrefix && hex.All(Uri.IsHexDigit))
        {
            var byId = board.AllItems().Where(i => i.Id.ToString("N").StartsWith(hex, StringComparison.Ordinal)).ToList();
            if (byId.Count > 0)
            {
                return Single(byId, text, prefer);
            }
        }

        var exact = board.AllItems().Where(i => string.Equals(i.Title.Trim(), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0 && Narrow(exact, prefer) is { Count: 1 } one)
        {
            return one[0];
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var partial = board.AllItems().Where(i => words.All(w => i.Title.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        if (partial.Count == 0)
        {
            throw new ArgumentException($"No task matches \"{text}\". Try: tt list {text}");
        }

        return Single(partial, text, prefer);
    }

    public static string ShortId(Guid id) => id.ToString("N")[..8];

    /// <summary>A full id, or an id prefix as tt prints them.</summary>
    public static bool LooksLikeId(string text)
    {
        var hex = (text ?? string.Empty).Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        return Guid.TryParse(text, out _) || (hex.Length >= MinPrefix && hex.All(Uri.IsHexDigit));
    }

    private static WorkItem Single(List<WorkItem> candidates, string text, Func<WorkItem, bool>? prefer)
    {
        var narrowed = Narrow(candidates, prefer);
        if (narrowed.Count == 1)
        {
            return narrowed[0];
        }

        var listed = narrowed.Take(MaxListed).Select(i => $"  {ShortId(i.Id)}  {(i.IsDone ? "[x]" : "[ ]")} {string.Join(" › ", i.Path)}");
        var more = narrowed.Count > MaxListed ? $"{Environment.NewLine}  … and {(narrowed.Count - MaxListed).ToString(CultureInfo.InvariantCulture)} more" : string.Empty;
        throw new ArgumentException($"\"{text}\" matches {narrowed.Count.ToString(CultureInfo.InvariantCulture)} tasks; use an id:{Environment.NewLine}{string.Join(Environment.NewLine, listed)}{more}");
    }

    private static List<WorkItem> Narrow(List<WorkItem> candidates, Func<WorkItem, bool>? prefer)
    {
        if (prefer is null)
        {
            return candidates;
        }

        var preferred = candidates.Where(prefer).ToList();
        return preferred.Count > 0 ? preferred : candidates;
    }
}

/// <summary>"When" words for snooze and deadlines: 45m, 2h, 3d, tomorrow, today, a date, an ISO date-time, or a snooze rule (next week, fri 14:30).</summary>
internal static class CliTime
{
    public static DateTimeOffset Defer(string when, DateTimeOffset now, TimeZoneInfo zone)
    {
        var text = (when ?? string.Empty).Trim();
        var at = Parse(text, now, zone) ?? throw new ArgumentException(TodoTracker.Core.Snooze.Hint(text, now, zone));
        return at > now ? at : throw new ArgumentException($"{text} is in the past. Snooze to a later time (e.g. 2h or tomorrow).");
    }

    private static DateTimeOffset? Parse(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        // One token only, so "2h and more words" is an error rather than "2h" with the rest dropped.
        if (!text.Contains(' ', StringComparison.Ordinal) && QuickCaptureParser.Parse($"x @{text}", now, zone).NextActionAt is { } at)
        {
            return at;
        }

        // The same words as everywhere else ("wed 14:30", "next week") before .NET's own date reading, which reads a
        // weekday as today's date.
        if (TodoTracker.Core.Snooze.Parse(text, now, zone) is { } rule)
        {
            return rule;
        }

        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var wall = date.ToDateTime(new TimeOnly(9, 0));
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        return Exact(text, zone);
    }

    public static DateTimeOffset Deadline(string when, DateTimeOffset now, TimeZoneInfo zone)
    {
        var text = (when ?? string.Empty).Trim();
        if (QuickCaptureParser.Parse($"x due:{text}", now, zone).Deadline is { } due)
        {
            return due;
        }

        if (QuickCaptureParser.Parse($"x @{text}", now, zone).NextActionAt is { } at)
        {
            return at;
        }

        return Exact(text, zone) ?? throw new ArgumentException($"\"{text}\" isn't a deadline. Use today, tomorrow, 3d, 2026-02-01, 2026-02-01T14:30, or none.");
    }

    private static DateTimeOffset? Exact(string text, TimeZoneInfo zone)
    {
        if (!text.Contains('T', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > text.IndexOf('T', StringComparison.Ordinal) + 1))
        {
            return parsed;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall))
        {
            wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        return null;
    }
}
