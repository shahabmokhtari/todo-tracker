namespace TodoTracker.Core;

public sealed record AgendaEntry(
    WorkItem Item,
    ItemState State,
    Priority EffectivePriority,
    bool NeedsAttention,
    Reminder? DueReminder,
    DateTimeOffset? WakeAt,
    bool IsOverdue,
    IReadOnlyList<string> Breadcrumb)
{
    /// <summary>The open task it waits for ("after task X"), itself or through its parent.</summary>
    public WorkItem? WaitingFor { get; init; }
}

public sealed record OverviewEntry(
    WorkItem Item,
    int DoneLeaves,
    int TotalLeaves,
    int ActionableCount,
    int WaitingCount,
    DateTimeOffset? NextWakeAt,
    Priority TopPriority);

public sealed record RecentNote(WorkItem Item, Note Note);

public sealed record GroupCount(int Now, int Waiting, int Attention);

public sealed record DashboardSnapshot(
    AgendaEntry? Focus,
    IReadOnlyList<AgendaEntry> Now,
    IReadOnlyList<AgendaEntry> Waiting,
    IReadOnlyList<OverviewEntry> Overview,
    IReadOnlyList<RecentNote> RecentNotes,
    IReadOnlyDictionary<Guid, GroupCount> GroupCounts);

/// <summary>
/// Time-aware projection of the board: what to do now, what is deferred, and a high-level overview.
/// This is the single source of truth for ordering rules (mirrored by the Swift core and verified
/// by the shared scenarios in <c>tests/fixtures/scenarios</c>).
/// </summary>
public static class Agenda
{
    public static ItemState StateOf(WorkItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDone)
        {
            return ItemState.Done;
        }

        if (IsLocked(item))
        {
            return ItemState.Locked;
        }

        if (Waits(item, now) || item.Ancestors().Any(a => Waits(a, now)))
        {
            return ItemState.Waiting;
        }

        return item.HasOpenChildren ? ItemState.Container : ItemState.Actionable;
    }

    public static Priority EffectivePriority(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Ancestors().Select(a => a.Priority).Append(item.Priority).Max();
    }

    public static DashboardSnapshot Build(TaskBoard board, DateTimeOffset now, int recentNoteCount = 5, Guid? groupId = null, TaskQuery? filter = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        var allOpen = board.AllItems().Where(i => !i.IsDone && !i.IsArchived).Select(i => Describe(i, now)).ToList();
        var nowAll = allOpen.Where(e => e.State == ItemState.Actionable || (e.NeedsAttention && e.State is ItemState.Waiting or ItemState.Container)).ToList();
        var waitingAll = allOpen.Where(e => e.State == ItemState.Waiting && !e.NeedsAttention && Waits(e.Item, now) && !e.Item.Ancestors().Any(a => Waits(a, now))).ToList();

        var counts = board.Groups.ToDictionary(
            g => g.Id,
            g => new GroupCount(
                nowAll.Count(e => e.Item.GroupId == g.Id),
                waitingAll.Count(e => e.Item.GroupId == g.Id),
                nowAll.Count(e => e.Item.GroupId == g.Id && e.NeedsAttention)));

        // Group tabs keep their totals; the filter narrows only what is listed.
        bool InScope(WorkItem item) => (groupId is null || item.GroupId == groupId) && (filter is null || filter.IsEmpty || filter.Matches(item, board));

        // A due reminder comes first; then the person's own order; then tasks they never placed, by the automatic rules
        // (priority, overdue, deadline, age). New tasks never jump above what someone arranged.
        var rank = board.NowOrder.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        var scoped = nowAll.Where(e => InScope(e.Item)).ToList();
        var nowList = Arrange(scoped.Where(e => e.NeedsAttention), rank).Concat(Arrange(scoped.Where(e => !e.NeedsAttention), rank)).ToList();

        // Back at a time first (soonest first); then those waiting for another task.
        var waitingList = waitingAll.Where(e => InScope(e.Item))
            .OrderBy(e => e.WakeAt ?? DateTimeOffset.MaxValue)
            .ThenByDescending(e => e.EffectivePriority)
            .ThenBy(e => e.Item.CreatedAt)
            .ToList();

        var overview = board.Items.Where(r => !r.IsDone && InScope(r)).Select(r => Summarize(r, nowList, waitingList)).ToList();

        var notes = recentNoteCount <= 0
            ? []
            : board.AllItems().Where(i => !i.IsArchived && InScope(i))
                .SelectMany(i => i.Notes.Select(n => new RecentNote(i, n)))
                .OrderByDescending(n => n.Note.At)
                .Take(recentNoteCount)
                .ToList();

        return new DashboardSnapshot(nowList.FirstOrDefault(), nowList, waitingList, overview, notes, counts);
    }

    private static int CompareAutomatic(AgendaEntry x, AgendaEntry y)
    {
        var c = y.EffectivePriority.CompareTo(x.EffectivePriority);
        if (c == 0)
        {
            c = y.IsOverdue.CompareTo(x.IsOverdue);
        }

        if (c == 0)
        {
            c = (x.Item.Deadline ?? DateTimeOffset.MaxValue).CompareTo(y.Item.Deadline ?? DateTimeOffset.MaxValue);
        }

        return c != 0 ? c : x.Item.CreatedAt.CompareTo(y.Item.CreatedAt);
    }

    /// <summary>Placed tasks in the person's order, then the others in the automatic order.</summary>
    private static IEnumerable<AgendaEntry> Arrange(IEnumerable<AgendaEntry> entries, Dictionary<Guid, int> rank)
    {
        var list = entries.ToList();
        return list.Where(e => rank.ContainsKey(e.Item.Id)).OrderBy(e => rank[e.Item.Id])
            .Concat(list.Where(e => !rank.ContainsKey(e.Item.Id)).Order(Comparer<AgendaEntry>.Create(CompareAutomatic)));
    }

    private static AgendaEntry Describe(WorkItem item, DateTimeOffset now)
    {
        var state = StateOf(item, now);
        var dueReminder = state is ItemState.Locked or ItemState.Done
            ? null
            : item.Reminders.Where(r => r.IsDue(now)).OrderBy(r => r.DueAt).FirstOrDefault();
        var wakeAt = state == ItemState.Waiting
            ? item.Ancestors().Select(a => a.NextActionAt).Append(item.NextActionAt).Where(t => t > now).Max()
            : null;
        return new AgendaEntry(
            item,
            state,
            EffectivePriority(item),
            dueReminder is not null,
            dueReminder,
            wakeAt,
            item.Deadline < now,
            item.Ancestors().Reverse().Select(a => a.Title).ToList())
        {
            WaitingFor = state == ItemState.Waiting ? item.WaitingFor ?? item.Ancestors().Select(a => a.WaitingFor).FirstOrDefault(w => w is not null) : null,
        };
    }

    private static bool IsLocked(WorkItem item) => TaskBoard.FindBlockingStep(item) is not null;

    /// <summary>Snoozed until a later time, or until another task is done.</summary>
    private static bool Waits(WorkItem item, DateTimeOffset now) => item.NextActionAt > now || item.WaitingFor is not null;


    private static OverviewEntry Summarize(WorkItem root, IReadOnlyList<AgendaEntry> now, IReadOnlyList<AgendaEntry> waiting)
    {
        var subtree = root.SelfAndDescendants().ToList();
        var ids = subtree.Select(i => i.Id).ToHashSet();
        var leaves = subtree.Where(i => i.Children.Count == 0).ToList();
        return new OverviewEntry(
            root,
            leaves.Count(l => l.IsDone),
            leaves.Count,
            now.Count(e => ids.Contains(e.Item.Id)),
            waiting.Count(e => ids.Contains(e.Item.Id)),
            waiting.Where(e => ids.Contains(e.Item.Id)).Select(e => e.WakeAt).Min(),
            subtree.Where(i => !i.IsDone).Select(EffectivePriority).DefaultIfEmpty(root.Priority).Max());
    }
}
