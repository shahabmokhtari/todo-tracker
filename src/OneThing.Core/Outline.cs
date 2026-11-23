using System.Collections.Frozen;

namespace OneThing.Core;

/// <summary>What the Tasks tree shows: unfinished tasks, what can be done now, what's waiting, or everything.</summary>
public enum OutlineShow
{
    /// <summary>Every task that isn't finished (the default).</summary>
    Todo,

    /// <summary>Tasks that can be done now, with their parents.</summary>
    Now,

    /// <summary>Tasks that wait (snoozed, waiting for another task) or come after an unfinished step, with their parents.</summary>
    Waiting,

    /// <summary>Finished tasks too.</summary>
    All,
}

/// <summary>How deep "Open to" opens the tree.</summary>
public enum OutlineLevel
{
    /// <summary>Only the group rows (under All groups).</summary>
    Groups,

    /// <summary>The top-level tasks, closed.</summary>
    Tasks,

    /// <summary>One level of subtasks.</summary>
    Subtasks,

    /// <summary>Everything.</summary>
    All,
}

public enum OutlineRowKind
{
    Group,
    Task,
}

public enum OutlinePartKind
{
    /// <summary>"All groups": the way out of the zoom.</summary>
    All,
    Group,
    Task,
}

/// <summary>
/// How the Tasks tree is looked at (per device, never saved in the tasks): the group picked (none: all groups), the
/// task zoomed into, what's shown, the search text, the tasks that are open and the groups that are closed.
/// </summary>
public sealed record OutlineOptions
{
    public Guid? GroupId { get; init; }

    public Guid? ZoomId { get; init; }

    public OutlineShow Show { get; init; } = OutlineShow.Todo;

    public string? Query { get; init; }

    public IReadOnlySet<Guid> Expanded { get; init; } = FrozenSet<Guid>.Empty;

    public IReadOnlySet<Guid> ClosedGroups { get; init; } = FrozenSet<Guid>.Empty;

    /// <summary>There's search text: the tree is narrowed to it and shown open on the way to what matched.</summary>
    public bool Searching => !string.IsNullOrWhiteSpace(Query);
}

/// <summary>
/// A row of the Tasks tree. <see cref="Title"/> is what's shown (without the parent's name it repeats);
/// <see cref="Done"/>/<see cref="Total"/> count the leaves under it (0/0 for a task without subtasks; a group adds up
/// its top-level tasks, one each for a task without subtasks); <see cref="Left"/> is <c>Total - Done</c>.
/// <see cref="CanOutdent"/>: it can move out one level here (not a top-level task, nor one at the top of a zoom).
/// </summary>
public sealed record OutlineRow(
    OutlineRowKind Kind,
    Guid Id,
    int Depth,
    string Title,
    string FullTitle,
    bool HasChildren,
    bool Expanded,
    int Done,
    int Total,
    int Left,
    ItemState? State,
    bool CanOutdent)
{
    /// <summary>The task (task rows).</summary>
    public WorkItem? Item { get; init; }

    /// <summary>The group (group rows).</summary>
    public TaskGroup? Group { get; init; }
}

/// <summary>A part of the path above a zoomed tree: All groups › group › each parent › the zoomed task.</summary>
public sealed record OutlinePathPart(OutlinePartKind Kind, Guid? Id, string Title, int Done = 0, int Total = 0);

/// <summary>What "Open to" leaves open: the tasks open and the groups closed.</summary>
public sealed record OutlineOpen(IReadOnlySet<Guid> Expanded, IReadOnlySet<Guid> ClosedGroups);

/// <summary>
/// The Tasks tree as rows: the same rules in C# (here, and <c>ot tree</c>), the web app (outlinemodel.js), Swift and
/// Kotlin, checked against tests/fixtures/outline.json. Only how the tree is shown; nothing in the tasks changes.
/// </summary>
public static class Outline
{
    /// <summary>The first part of the path.</summary>
    public const string AllGroupsTitle = "All groups";

    private static readonly string[] Separators = [":", " -", " –", " —"];

    /// <summary>The rows to draw, in order.</summary>
    public static IReadOnlyList<OutlineRow> Rows(TaskBoard board, OutlineOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(options);
        var shown = new Shown(board, options, now);
        var rows = new List<OutlineRow>();
        var zoom = ResolveZoom(shown);

        void Walk(IEnumerable<WorkItem> items, int depth)
        {
            foreach (var item in items.Where(shown.Kept))
            {
                var children = item.Children.Where(shown.Kept).ToList();
                var open = children.Count > 0 && (options.Searching || options.Expanded.Contains(item.Id));
                var (done, total) = Leaves(item);
                var parent = item.Parent;
                rows.Add(new OutlineRow(
                    OutlineRowKind.Task,
                    item.Id,
                    depth,
                    ShownTitle(item.Title, parent?.Title),
                    item.Title,
                    children.Count > 0,
                    open,
                    done,
                    total,
                    total - done,
                    Agenda.StateOf(item, now),
                    parent is not null && parent != zoom)
                { Item = item });
                if (open)
                {
                    Walk(children, depth + 1);
                }
            }
        }

        if (zoom is not null)
        {
            Walk(zoom.Children, 0);
            return rows;
        }

        var roots = shown.Roots().ToList();
        if (options.GroupId is { } picked)
        {
            Walk(roots.Where(r => r.GroupId == picked), 0);
            return rows;
        }

        // Empty groups stay (somewhere to drop into) unless a search or a narrowing choice is on.
        var hideEmpty = options.Searching || options.Show is OutlineShow.Now or OutlineShow.Waiting;
        foreach (var group in board.Groups)
        {
            var tasks = roots.Where(r => r.GroupId == group.Id).ToList();
            var hasChildren = tasks.Exists(shown.Kept);
            if (!hasChildren && hideEmpty)
            {
                continue;
            }

            var (done, total) = GroupLeaves(tasks);
            var open = options.Searching || !options.ClosedGroups.Contains(group.Id);
            rows.Add(new OutlineRow(OutlineRowKind.Group, group.Id, 0, group.Name, group.Name, hasChildren, open, done, total, total - done, null, false) { Group = group });
            if (open)
            {
                Walk(tasks, 1);
            }
        }

        return rows;
    }

    /// <summary>The path above the tree while zoomed (empty when not).</summary>
    public static IReadOnlyList<OutlinePathPart> Path(TaskBoard board, OutlineOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(options);
        if (ResolveZoom(new Shown(board, options, now)) is not { } zoom)
        {
            return [];
        }

        var group = board.GetGroup(zoom.GroupId);
        var parts = new List<OutlinePathPart> { new(OutlinePartKind.All, null, AllGroupsTitle), new(OutlinePartKind.Group, group.Id, group.Name) };
        WorkItem? previous = null;
        foreach (var item in zoom.Ancestors().Reverse().Append(zoom))
        {
            var (done, total) = Leaves(item);
            parts.Add(new OutlinePathPart(OutlinePartKind.Task, item.Id, ShownTitle(item.Title, previous?.Title), done, total));
            previous = item;
        }

        return parts;
    }

    /// <summary>
    /// The zoom to use: the asked task when it's shown and has subtasks; else its nearest parent that is; else none
    /// (the top). A task that's gone (deleted, archived, in another group than the one picked) has none.
    /// </summary>
    public static Guid? ResolveZoom(TaskBoard board, OutlineOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(options);
        return ResolveZoom(new Shown(board, options, now))?.Id;
    }

    /// <summary>
    /// "Open to" a level: which tasks are open and which groups closed afterwards. It changes only what's in view (under
    /// the zoom, the group picked, or every group); tasks elsewhere stay as they were. Groups closes every group (under
    /// All groups; elsewhere it's the same as Tasks); the other levels open every group.
    /// </summary>
    public static OutlineOpen OpenTo(TaskBoard board, OutlineOptions options, OutlineLevel level, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(options);
        var shown = new Shown(board, options, now);
        var zoom = ResolveZoom(shown);
        var top = zoom is not null
            ? zoom.Children.Where(shown.InQuery).ToList()
            : shown.Roots().Where(r => options.GroupId is not { } g || r.GroupId == g).ToList();
        var expanded = options.Expanded.ToHashSet();
        void Each(IEnumerable<WorkItem> items, int depth, Action<WorkItem, int> visit)
        {
            foreach (var item in items)
            {
                visit(item, depth);
                Each(item.Children.Where(shown.InQuery), depth + 1, visit);
            }
        }

        Each(top, 0, (item, depth) =>
        {
            var open = item.Children.Any(shown.InQuery) && level switch
            {
                OutlineLevel.All => true,
                OutlineLevel.Subtasks => depth == 0,
                _ => false,
            };
            if (open)
            {
                expanded.Add(item.Id);
            }
            else
            {
                expanded.Remove(item.Id);
            }
        });

        IReadOnlySet<Guid> closed = zoom is not null || options.GroupId is not null
            ? options.ClosedGroups
            : level == OutlineLevel.Groups ? board.Groups.Select(g => g.Id).ToHashSet() : FrozenSet<Guid>.Empty;
        return new OutlineOpen(expanded, closed);
    }

    /// <summary>
    /// Where New task adds: inside the zoomed task, else at the top of the group picked (all groups: the first group).
    /// </summary>
    public static (Guid? ParentId, Guid GroupId) NewTaskPlace(TaskBoard board, OutlineOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(options);
        var zoom = ResolveZoom(new Shown(board, options, now));
        return (zoom?.Id, zoom?.GroupId ?? options.GroupId ?? board.DefaultGroupId);
    }

    /// <summary>
    /// A subtask's title without its parent's name in front: when it starts with the parent's title (any case)
    /// followed by <c>:</c>, <c> -</c>, <c> –</c> or <c> —</c>, the rest (leading spaces dropped), unless that's empty.
    /// </summary>
    public static string ShownTitle(string title, string? parentTitle)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (string.IsNullOrEmpty(parentTitle) || !title.StartsWith(parentTitle, StringComparison.OrdinalIgnoreCase))
        {
            return title;
        }

        var after = title[parentTitle.Length..];
        foreach (var separator in Separators)
        {
            if (after.StartsWith(separator, StringComparison.Ordinal))
            {
                var rest = after[separator.Length..].TrimStart();
                return rest.Length > 0 ? rest : title;
            }
        }

        return title;
    }

    /// <summary>Parses todo, now, waiting or all (any case); null for anything else.</summary>
    public static OutlineShow? ParseShow(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "todo" or "to do" => OutlineShow.Todo,
        "now" => OutlineShow.Now,
        "waiting" => OutlineShow.Waiting,
        "all" or "everything" => OutlineShow.All,
        _ => null,
    };

    /// <summary>Finished leaves and all leaves under the task (not counting the task itself).</summary>
    private static (int Done, int Total) Leaves(WorkItem item)
    {
        var leaves = item.SelfAndDescendants().Skip(1).Where(i => i.Children.Count == 0).ToList();
        return (leaves.Count(l => l.IsDone), leaves.Count);
    }

    private static (int Done, int Total) GroupLeaves(IEnumerable<WorkItem> roots)
    {
        var (done, total) = (0, 0);
        foreach (var root in roots)
        {
            var (d, t) = Leaves(root);
            (done, total) = t > 0 ? (done + d, total + t) : (done + (root.IsDone ? 1 : 0), total + 1);
        }

        return (done, total);
    }

    private static WorkItem? ResolveZoom(Shown shown)
    {
        if (shown.Options.ZoomId is not { } id || shown.Board.Find(id) is not { } item || item.IsArchived)
        {
            return null;
        }

        if (shown.Options.GroupId is { } group && item.GroupId != group)
        {
            return null;
        }

        for (WorkItem? at = item; at is not null; at = at.Parent)
        {
            if (at.Children.Count > 0 && shown.Kept(at))
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>Which tasks the search and the Show choice keep (each kept task's parents are kept too).</summary>
    private sealed class Shown
    {
        private readonly HashSet<Guid>? _query;
        private readonly Dictionary<Guid, bool> _kept = [];
        private readonly DateTimeOffset _now;

        public Shown(TaskBoard board, OutlineOptions options, DateTimeOffset now)
        {
            Board = board;
            Options = options;
            _now = now;
            if (options.Searching)
            {
                // As /api/tree?q= does: the matches, their parents (for context) and their subtasks.
                _query = [];
                foreach (var match in TaskQuery.Parse(options.Query).Apply(board))
                {
                    _query.UnionWith(match.SelfAndDescendants().Select(i => i.Id));
                    _query.UnionWith(match.Ancestors().Select(i => i.Id));
                }
            }
        }

        public TaskBoard Board { get; }

        public OutlineOptions Options { get; }

        /// <summary>Top-level tasks that aren't archived and that the search keeps.</summary>
        public IEnumerable<WorkItem> Roots() => Board.Items.Where(r => r.ArchivedAt is null && InQuery(r));

        public bool InQuery(WorkItem item) => _query is null || _query.Contains(item.Id);

        public bool Kept(WorkItem item)
        {
            if (!_kept.TryGetValue(item.Id, out var kept))
            {
                kept = !item.IsArchived && InQuery(item) && (Itself(item) || item.Children.Any(Kept));
                _kept[item.Id] = kept;
            }

            return kept;
        }

        private bool Itself(WorkItem item) => Options.Show switch
        {
            OutlineShow.All => true,
            OutlineShow.Now => Agenda.StateOf(item, _now) == ItemState.Actionable,
            OutlineShow.Waiting => Agenda.StateOf(item, _now) is ItemState.Waiting or ItemState.Locked,
            _ => !item.IsDone,
        };
    }
}
