namespace TodoTracker.Core;

/// <summary>
/// Deep links that open the installed app on a task: <c>todotracker://task/&lt;id&gt;</c>. Paste one anywhere (a note,
/// a chat, a calendar invite) and clicking it brings the app up with that task open.
/// </summary>
public static class TaskLinks
{
    public const string Scheme = "todotracker";

    public static string ForTask(Guid id) => $"{Scheme}://task/{id:D}";

    /// <summary>
    /// The path of the app a link opens ("/#/task/&lt;id&gt;", or "/" for the bare scheme), or null when it isn't a link
    /// the app follows. Only these two shapes are honoured, so a crafted link can never reach anything else.
    /// </summary>
    public static string? AppPath(string? link)
    {
        var text = link?.Trim();
        if (string.IsNullOrEmpty(text) || !text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = text[(Scheme.Length + 1)..].TrimStart('/');
        var end = rest.IndexOfAny(['?', '#']);
        var parts = (end < 0 ? rest : rest[..end]).TrimEnd('/').Split('/');
        return parts switch
        {
            [""] or ["open"] => "/",
            [var kind, var id] when kind.Equals("task", StringComparison.OrdinalIgnoreCase) && TryParseId(id, out var taskId) => $"/#/task/{taskId:D}",
            _ => null,
        };
    }

    // Only the forms links are made in (dashed, or 32 hex digits), the same in the Swift and Kotlin apps; not
    // Guid.TryParse's {…} and (…) forms.
    private static bool TryParseId(string text, out Guid id) => Guid.TryParseExact(text, "D", out id) || Guid.TryParseExact(text, "N", out id);
}
