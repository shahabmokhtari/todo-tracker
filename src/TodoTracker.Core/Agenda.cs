namespace TodoTracker.Core;

public sealed record AgendaEntry(
    WorkItem Item,
    ItemState State,
    Priority EffectivePriority,
    bool NeedsAttention,
    Reminder? DueReminder,
    DateTimeOffset? WakeAt,
    bool IsOverdue,
    IReadOnlyList<string> Breadcrumb);

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

        if (item.NextActionAt > now || item.Ancestors().Any(a => a.NextActionAt > now))
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

    public static DashboardSnapshot Build(TaskBoard board, DateTimeOffset now, int recentNoteCount = 5, Guid? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        var allOpen = board.AllItems().Where(i => !i.IsDone).Select(i => Describe(i, now)).ToList();
        var nowAll = allOpen.Where(e => e.State == ItemState.Actionable || (e.NeedsAttention && e.State is ItemState.Waiting or ItemState.Container)).ToList();
        var waitingAll = allOpen.Where(e => e.State == ItemState.Waiting && !e.NeedsAttention && e.Item.NextActionAt > now && !e.Item.Ancestors().Any(a => a.NextActionAt > now)).ToList();

        var counts = board.Groups.ToDictionary(
            g => g.Id,
            g => new GroupCount(
                nowAll.Count(e => e.Item.GroupId == g.Id),
                waitingAll.Count(e => e.Item.GroupId == g.Id),
                nowAll.Count(e => e.Item.GroupId == g.Id && e.NeedsAttention)));

        bool InScope(WorkItem item) => groupId is null || item.GroupId == groupId;

        var nowList = nowAll.Where(e => InScope(e.Item))
            .OrderByDescending(e => e.NeedsAttention)
            .ThenByDescending(e => e.EffectivePriority)
            .ThenByDescending(e => e.IsOverdue)
            .ThenBy(e => e.Item.Deadline ?? DateTimeOffset.MaxValue)
            .ThenBy(e => e.Item.CreatedAt)
            .ToList();

        var waitingList = waitingAll.Where(e => InScope(e.Item))
            .OrderBy(e => e.WakeAt)
            .ThenByDescending(e => e.EffectivePriority)
            .ThenBy(e => e.Item.CreatedAt)
            .ToList();

        var overview = board.Items.Where(r => !r.IsDone && InScope(r)).Select(r => Summarize(r, nowList, waitingList)).ToList();

        var notes = recentNoteCount <= 0
            ? []
            : board.AllItems().Where(InScope)
                .SelectMany(i => i.Notes.Select(n => new RecentNote(i, n)))
                .OrderByDescending(n => n.Note.At)
                .Take(recentNoteCount)
                .ToList();

        return new DashboardSnapshot(nowList.FirstOrDefault(), nowList, waitingList, overview, notes, counts);
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
            item.Ancestors().Reverse().Select(a => a.Title).ToList());
    }

    private static bool IsLocked(WorkItem item)
    {
        for (WorkItem? current = item; current is not null; current = current.Parent)
        {
            if (TaskBoard.BlockingStep(current) is not null)
            {
                return true;
            }
        }

        return false;
    }

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
