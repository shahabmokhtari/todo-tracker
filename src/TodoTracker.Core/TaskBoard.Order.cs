namespace TodoTracker.Core;

/// <summary>Manual order: what comes first in Do now, and the order of siblings in the tree.</summary>
public sealed partial class TaskBoard
{
    internal List<Guid> NowOrderList { get; } = [];

    /// <summary>The Do now order the person arranged (tasks not listed slot in by the automatic rules).</summary>
    public IReadOnlyList<Guid> NowOrder => NowOrderList;

    /// <summary>
    /// Records the order of the tasks someone sees in Do now (e.g. after dragging one). Only the places those tasks
    /// already had are reused, so arranging one group's tab doesn't move the other groups' tasks.
    /// </summary>
    public void ArrangeNow(IReadOnlyList<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Distinct().Count() != ids.Count)
        {
            throw new ArgumentException("Each task can appear only once.", nameof(ids));
        }

        foreach (var id in ids)
        {
            Get(id);
        }

        var arranged = ids.ToHashSet();
        var start = NowOrderList.FindIndex(arranged.Contains);
        var rest = NowOrderList.Where(id => !arranged.Contains(id)).ToList();
        var slots = NowOrderList.Select((id, i) => (id, i)).Where(x => arranged.Contains(x.id)).Select(x => x.i).ToList();
        List<Guid> result;
        if (slots.Count == ids.Count)
        {
            // Same set of places: keep them, fill them in the new order.
            result = [.. NowOrderList];
            for (var k = 0; k < slots.Count; k++)
            {
                result[slots[k]] = ids[k];
            }
        }
        else
        {
            var at = start < 0 ? 0 : NowOrderList.Take(start).Count(id => !arranged.Contains(id));
            result = [.. rest];
            result.InsertRange(at, ids);
        }

        NowOrderList.Clear();
        NowOrderList.AddRange(result.Where(id => Find(id) is { IsDone: false }));
    }

    /// <summary>Puts <paramref name="ids"/> at the top of Do now, in that order; the first becomes the focus.</summary>
    public void PutFirst(IReadOnlyList<Guid> ids, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var current = Agenda.Build(this, now, recentNoteCount: 0).Now.Select(e => e.Item.Id).Where(id => !ids.Contains(id));
        ArrangeNow([.. ids, .. current]);
    }

    /// <summary>Moves a task among its siblings, before <paramref name="before"/> (or to the end). Its parent stays.</summary>
    public void Reorder(Guid id, Guid? before)
    {
        var item = Get(id);
        var siblings = item.Parent?.ChildList ?? RootList;
        if (before == id)
        {
            return;
        }

        var target = before is { } b ? Get(b) : null;
        if (target is not null && !siblings.Contains(target))
        {
            throw new ArgumentException($"\"{target.Title}\" isn't next to \"{item.Title}\"; use move to put a task somewhere else.", nameof(before));
        }

        siblings.Remove(item);
        siblings.Insert(target is null ? siblings.Count : siblings.IndexOf(target), item);
    }
}
