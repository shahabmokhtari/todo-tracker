namespace OneThing.Core;

/// <summary>Manual order: what comes first in Do now, and the order of siblings in the tree.</summary>
public sealed partial class TaskBoard
{
    internal List<Guid> NowOrderList { get; } = [];

    /// <summary>The Do now order the person arranged (tasks not listed slot in by the automatic rules).</summary>
    public IReadOnlyList<Guid> NowOrder => NowOrderList;

    /// <summary>
    /// When Do now was last arranged. A reminder that went off before then was there to see: the task stays where it was
    /// put. One going off after it brings its task to the top again.
    /// </summary>
    public DateTimeOffset? NowArrangedAt { get; internal set; }

    /// <summary>How much energy there is in a day (⚡; null: <see cref="Energy.DefaultPerDay"/>). Kept with the board, so every app uses it.</summary>
    public int? EnergyPerDay { get; internal set; }

    public int DailyEnergy => EnergyPerDay ?? Energy.DefaultPerDay;

    /// <summary>Sets how much energy there is in a day (null: back to the default). The timeline says who set it.</summary>
    public void SetEnergyPerDay(int? perDay, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        EnergyPerDay = perDay is null or (>= 1 and <= Energy.MaxPerDay)
            ? perDay
            : throw new ArgumentOutOfRangeException(nameof(perDay), $"A day's energy is 1 to {Energy.MaxPerDay} ⚡.");
        Log(now, Guid.Empty, ActivityKind.Updated, perDay is { } n ? $"Set a day's energy to {n} ⚡" : "Set a day's energy back to the default", actor);
    }

    /// <summary>
    /// Records the order of the tasks someone sees in Do now (e.g. after dragging one), tasks with a due reminder
    /// included. Only the places those tasks already had are reused, so arranging one group's tab doesn't move the
    /// other groups' tasks. The timeline says who arranged it.
    /// </summary>
    public void ArrangeNow(IReadOnlyList<Guid> ids, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArrangeNowCore(ids, now);
        Log(now, ids.Count > 0 ? ids[0] : Guid.Empty, ActivityKind.Moved, ids.Count > 0 ? $"Arranged Do now (\"{Get(ids[0]).Title}\" first)" : "Arranged Do now", actor);
    }

    private void ArrangeNowCore(IReadOnlyList<Guid> ids, DateTimeOffset now)
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
        NowArrangedAt = now;
    }

    /// <summary>
    /// Puts <paramref name="ids"/> at the top of Do now, in that order (only a reminder going off later comes above
    /// them); the first becomes the focus. The timeline says who put it first.
    /// </summary>
    public void PutFirst(IReadOnlyList<Guid> ids, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(actor);
        if (ids.Select(Get).FirstOrDefault(i => i.IsDone) is { } done)
        {
            throw new InvalidOperationException($"\"{done.Title}\" is already done.");
        }

        var current = Agenda.Build(this, now, recentNoteCount: 0).Now.Select(e => e.Item.Id).Where(id => !ids.Contains(id));
        ArrangeNowCore([.. ids, .. current], now);
        if (ids.Count > 0)
        {
            Log(now, ids[0], ActivityKind.Updated, $"Put \"{Get(ids[0]).Title}\" first", actor);
        }
    }

    /// <summary>Puts a task right after another one next to it (whatever is between them on screen).</summary>
    public void ReorderAfter(Guid id, Guid after, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var item = Get(id);
        var target = Get(after);
        var siblings = item.Parent?.ChildList ?? RootList;
        if (after == id)
        {
            return;
        }

        if (!siblings.Contains(target))
        {
            throw new ArgumentException($"\"{target.Title}\" isn't next to \"{item.Title}\"; use move to put a task somewhere else.", nameof(after));
        }

        siblings.Remove(item);
        siblings.Insert(siblings.IndexOf(target) + 1, item);
        Log(now, item.Id, ActivityKind.Moved, $"Moved \"{item.Title}\" after \"{target.Title}\"", actor);
    }

    /// <summary>Moves a task among its siblings, before <paramref name="before"/> (or to the end). Its parent stays.</summary>
    public void Reorder(Guid id, Guid? before, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(actor);
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
        Log(now, item.Id, ActivityKind.Moved, target is null ? $"Moved \"{item.Title}\" to the end" : $"Moved \"{item.Title}\" before \"{target.Title}\"", actor);
    }
}
