namespace TodoTracker.Core;

/// <summary>
/// One search syntax for every surface (search box, CLI, MCP): words match the title or details; <c>#tag</c>
/// (nested tags included), <c>label:name</c>, <c>group:name</c>, and <c>is:open</c>/<c>is:done</c> narrow it down.
/// Tags and labels are inherited, so <c>#release</c> also finds the steps of a tagged project. Quote multi-word
/// values: <c>label:"deep work"</c>. All terms must match (case-insensitive).
/// </summary>
public sealed class TaskQuery
{
    private readonly List<string> _words = [];
    private readonly List<string> _tags = [];
    private readonly List<string> _labels = [];
    private readonly List<string> _groups = [];
    private bool? _done;

    private TaskQuery()
    {
    }

    public static TaskQuery Empty { get; } = new();

    /// <summary>Whether the query says which state it wants (<c>is:open</c> or <c>is:done</c>).</summary>
    public bool HasState => _done is not null;

    public bool IsEmpty => _words.Count == 0 && _tags.Count == 0 && _labels.Count == 0 && _groups.Count == 0 && _done is null;

    public static TaskQuery Parse(string? text)
    {
        var query = new TaskQuery();
        foreach (var token in Tokenize(text ?? string.Empty))
        {
            if (token.StartsWith('#') && token.Length > 1)
            {
                query._tags.Add(token[1..]);
            }
            else if (TryValue(token, "label:", out var label))
            {
                query._labels.Add(label);
            }
            else if (TryValue(token, "group:", out var group))
            {
                query._groups.Add(group);
            }
            else if (TryValue(token, "is:", out var state) && state.ToLowerInvariant() is "open" or "done")
            {
                query._done = state.Equals("done", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                query._words.Add(token);
            }
        }

        return query;
    }

    /// <summary>Matching tasks in tree order (parents before their subtasks).</summary>
    public IEnumerable<WorkItem> Apply(TaskBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return board.AllItems().Where(i => Matches(i, board));
    }

    public bool Matches(WorkItem item, TaskBoard board)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(board);
        if (_done is { } done && item.IsDone != done)
        {
            return false;
        }

        var lineage = item.Ancestors().Prepend(item).ToList();
        return _words.TrueForAll(w => Contains(item.Title, w) || Contains(item.Details, w))
            && _tags.TrueForAll(t => lineage.Exists(i => i.Tags.Any(tag => IsTagMatch(tag, t))))
            && _labels.TrueForAll(l => lineage.Exists(i => i.Labels.Any(label => string.Equals(label, l, StringComparison.OrdinalIgnoreCase))))
            && _groups.TrueForAll(g => board.Groups.Any(gr => gr.Id == item.GroupId && (string.Equals(gr.Name, g, StringComparison.OrdinalIgnoreCase) || gr.Id.ToString() == g)));
    }

    private static bool IsTagMatch(string tag, string query) =>
        string.Equals(tag, query, StringComparison.OrdinalIgnoreCase) || tag.StartsWith(query + "/", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryValue(string token, string prefix, out string value)
    {
        value = token.Length > prefix.Length && token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? token[prefix.Length..] : string.Empty;
        return value.Length > 0;
    }

    /// <summary>Splits on whitespace; double quotes group words (and are dropped), also inside <c>label:"a b"</c>.</summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
