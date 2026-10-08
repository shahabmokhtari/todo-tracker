namespace TodoTracker.Core;

/// <summary>Hooks for stores that load the board from files edited outside the app.</summary>
public sealed partial class TaskBoard
{
    /// <summary>Creates an empty board (no default groups) for a store to fill.</summary>
    internal static TaskBoard CreateEmpty() => new(seedDefaultGroups: false);

    internal void AddLoadedGroup(Guid id, string name, string? color) => GroupList.Add(new TaskGroup(id, name, color));

    internal void AddLoadedLabel(string name, string color)
    {
        if (FindLabel(name) is null)
        {
            LabelDefinitionList.Add(new LabelDefinition(name, color));
        }
    }

    /// <summary>Adds a parsed task tree (children already linked) as a top-level task in <paramref name="groupId"/>.</summary>
    internal void AttachLoaded(WorkItem root, Guid groupId)
    {
        foreach (var item in root.SelfAndDescendants())
        {
            if (_index.ContainsKey(item.Id))
            {
                throw new InvalidDataException($"Duplicate task id {item.Id}.");
            }
        }

        foreach (var item in root.SelfAndDescendants())
        {
            _index.Add(item.Id, item);
            item.Board = this;
        }

        root.Parent = null;
        root.OwnGroupId = groupId;
        RootList.Add(root);
        foreach (var label in root.SelfAndDescendants().SelectMany(i => i.Labels).ToList())
        {
            EnsureLabel(label);
        }
    }

    /// <summary>Ids in <paramref name="root"/>'s tree that are already used on this board.</summary>
    internal bool HasAnyId(WorkItem root) => root.SelfAndDescendants().Any(i => _index.ContainsKey(i.Id));

    /// <summary>Stable re-sort of the top-level tasks (e.g. into the order a store remembered).</summary>
    internal void ReorderRoots(Func<WorkItem, int> key)
    {
        var sorted = RootList.Select((r, i) => (r, i)).OrderBy(x => key(x.r)).ThenBy(x => x.i).Select(x => x.r).ToList();
        RootList.Clear();
        RootList.AddRange(sorted);
    }

    /// <summary>A box was checked outside the app: finish the subtree and advance a sequence like the app would.</summary>
    internal void CompleteExternally(WorkItem item, Actor actor, DateTimeOffset now)
    {
        var at = item.CompletedAt ?? now;
        foreach (var descendant in item.SelfAndDescendants().Where(d => !d.IsDone))
        {
            descendant.CompletedAt = at;
        }

        Log(now, item.Id, ActivityKind.Completed, $"Completed \"{item.Title}\"", actor);
        AdvanceSequence(item, actor, now);
        ReleaseWaiters(now);
    }

    internal void LogExternal(Guid itemId, ActivityKind kind, string summary, Actor actor, DateTimeOffset now) => Log(now, itemId, kind, summary, actor);

    /// <summary>Records that a task's rich (HTML) version was added, replaced, or removed.</summary>
    public void NoteRichContentChanged(Guid id, bool removed, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        Log(now, id, ActivityKind.Updated, removed ? $"Removed the rich version of \"{item.Title}\"" : $"Updated the rich version of \"{item.Title}\"", actor);
    }
}
