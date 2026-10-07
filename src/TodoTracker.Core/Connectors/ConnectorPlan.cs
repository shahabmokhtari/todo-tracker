using TodoTracker.Core.Vault;

namespace TodoTracker.Core.Connectors;

/// <summary>The task fields a connector can keep in step (each connector supports some of them).</summary>
[Flags]
public enum ConnectorFields
{
    None = 0,
    Title = 1,
    Done = 2,
    Due = 4,
    Priority = 8,
    Tags = 16,
    Notes = 32,
}

/// <summary>Which side new tasks are created on (linked tasks always sync both ways).</summary>
public enum ConnectorDirection
{
    Both,

    /// <summary>Bring the outside list's new items in; don't send tasks from here.</summary>
    ImportOnly,

    /// <summary>Send new tasks from here; don't bring the outside list's items in.</summary>
    ExportOnly,
}

public enum LinkState
{
    Active,

    /// <summary>The task was deleted here (the outside item was archived): kept so it isn't imported again.</summary>
    LocalDeleted,

    /// <summary>The outside item is gone (deleted, archived, no access): the task here is untouched.</summary>
    RemoteGone,
}

/// <summary>A task's synced fields. Tags compare as a set; notes ignore trailing space and null equals empty.</summary>
public sealed record SyncedFields(string Title, bool Done, DateOnly? Due, Priority Priority, IReadOnlyList<string> Tags, string? Notes)
{
    public bool Equals(SyncedFields? other) => other is not null && Same(this, other, (ConnectorFields)~0);

    public override int GetHashCode() => HashCode.Combine(Title, Done, Due, Priority, NormalNotes(Notes));

    internal static bool Same(SyncedFields a, SyncedFields b, ConnectorFields fields) =>
        (!fields.HasFlag(ConnectorFields.Title) || a.Title == b.Title)
        && (!fields.HasFlag(ConnectorFields.Done) || a.Done == b.Done)
        && (!fields.HasFlag(ConnectorFields.Due) || a.Due == b.Due)
        && (!fields.HasFlag(ConnectorFields.Priority) || a.Priority == b.Priority)
        && (!fields.HasFlag(ConnectorFields.Tags) || NormalTags(a.Tags).SequenceEqual(NormalTags(b.Tags)))
        && (!fields.HasFlag(ConnectorFields.Notes) || NormalNotes(a.Notes) == NormalNotes(b.Notes));

    private static IEnumerable<string> NormalTags(IReadOnlyList<string> tags) =>
        tags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().Order(StringComparer.Ordinal);

    private static string NormalNotes(string? notes) => (notes ?? string.Empty).TrimEnd();
}

/// <summary>A task here. <paramref name="Discoverable"/>: may be sent out when it isn't linked yet (an open top-level
/// task of the connector's group, or one finished lately).</summary>
public sealed record LocalItem(Guid Id, SyncedFields Fields, bool Discoverable, bool Archived);

/// <summary>An item in the outside list; <paramref name="Marker"/> is the task id it carries (set by us).</summary>
public sealed record RemoteItem(string Id, SyncedFields Fields, Guid? Marker);

/// <summary>A task and its outside item, with the fields as they were when they last agreed.</summary>
public sealed record ConnectorLink(Guid LocalId, string RemoteId, SyncedFields? Snapshot, LinkState State = LinkState.Active);

public abstract record ConnectorOp;

public sealed record CreateLocal(Guid Id, string RemoteId, SyncedFields Fields) : ConnectorOp;

public sealed record CreateRemote(Guid LocalId, SyncedFields Fields) : ConnectorOp;

public sealed record UpdateLocal(Guid LocalId, SyncedFields Fields) : ConnectorOp;

public sealed record UpdateRemote(string RemoteId, Guid LocalId, SyncedFields Fields) : ConnectorOp;

/// <summary>Archive (Notion) or finish (To Do) the outside item of a task deleted here: both can be undone there.</summary>
public sealed record ArchiveRemote(string RemoteId) : ConnectorOp;

/// <summary>Write the task id onto the outside item, so a copy is never made of it.</summary>
public sealed record MarkRemote(string RemoteId, Guid LocalId) : ConnectorOp;

public sealed record ConnectorPlanInput(
    string Connector,
    IReadOnlyList<LocalItem> Local,
    IReadOnlyList<RemoteItem> Remote,
    IReadOnlyList<ConnectorLink> Links,
    ConnectorDirection Direction,
    ConnectorFields Fields)
{
    /// <summary>How the other side shows a task's fields (To Do has no "critical"): compared that way, so a value it
    /// can only approximate isn't taken as a change.</summary>
    public Func<SyncedFields, SyncedFields>? Normalize { get; init; }
}

/// <summary>The changes to make, and the links as they'll be once every change went through.</summary>
public sealed record ConnectorPlanResult(IReadOnlyList<ConnectorOp> Ops, IReadOnlyList<ConnectorLink> Links);

/// <summary>A change that went through (<paramref name="CreatedRemoteId"/>: the new outside item's id).</summary>
public sealed record ConnectorOpResult(ConnectorOp Op, string? CreatedRemoteId = null);

/// <summary>
/// Two-way sync with an outside list, worked out without touching either side. Never loses data: nothing is ever
/// deleted (a task deleted here archives its outside item; an outside item that's gone leaves the task alone), each
/// field takes the side that changed (the task here wins when both did), and every create is idempotent (outside
/// items carry the task id; imported tasks get an id made from the outside id).
/// </summary>
public static class ConnectorPlan
{
    /// <summary>The id a task imported from <paramref name="remoteId"/> gets (the same on every computer).</summary>
    public static Guid ImportId(string connector, string remoteId) => VaultText.StableGuid($"connector:{connector}:{remoteId}");

    public static ConnectorPlanResult Make(ConnectorPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var ops = new List<ConnectorOp>();
        var links = new List<ConnectorLink>();
        var local = input.Local.ToDictionary(l => l.Id);
        var remote = input.Remote.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var linkedLocal = new HashSet<Guid>();
        var linkedRemote = new HashSet<string>(StringComparer.Ordinal);
        var fields = input.Fields;
        var n = input.Normalize ?? (f => f);

        void Merge(LocalItem here, RemoteItem there, SyncedFields? snapshot, ConnectorLink link)
        {
            var merged = MergeFields(here.Fields, there.Fields, snapshot, fields, n);
            if (!SyncedFields.Same(merged, here.Fields, fields))
            {
                ops.Add(new UpdateLocal(here.Id, merged));
            }

            if (!SyncedFields.Same(n(merged), n(there.Fields), fields))
            {
                ops.Add(new UpdateRemote(there.Id, here.Id, merged));
            }

            if (there.Marker != here.Id)
            {
                ops.Add(new MarkRemote(there.Id, here.Id));
            }

            links.Add(link with { Snapshot = merged, State = LinkState.Active });
        }

        foreach (var link in input.Links)
        {
            linkedLocal.Add(link.LocalId);
            linkedRemote.Add(link.RemoteId);
            local.TryGetValue(link.LocalId, out var here);
            remote.TryGetValue(link.RemoteId, out var there);
            if (here is { Archived: true })
            {
                links.Add(link); // put away here: kept, nothing synced either way
            }
            else if (here is not null && there is not null)
            {
                Merge(here, there, link.State == LinkState.LocalDeleted ? null : link.Snapshot, link);
            }
            else if (there is not null)
            {
                if (link.State == LinkState.LocalDeleted)
                {
                    // Changed there after it was deleted here: it's wanted again (with its old id).
                    if (link.Snapshot is null || !SyncedFields.Same(n(there.Fields), n(link.Snapshot), fields))
                    {
                        ops.Add(new CreateLocal(link.LocalId, there.Id, there.Fields));
                        links.Add(link with { Snapshot = there.Fields, State = LinkState.Active });
                    }
                    else
                    {
                        links.Add(link);
                    }
                }
                else
                {
                    ops.Add(new ArchiveRemote(there.Id));
                    // Archiving finishes it at least (To Do keeps it as done): that's the state to compare with later.
                    links.Add(link with { Snapshot = (link.Snapshot ?? there.Fields) with { Done = true }, State = LinkState.LocalDeleted });
                }
            }
            else if (here is not null)
            {
                // Gone there: the task here stays as it is.
                links.Add(link.State == LinkState.LocalDeleted ? link : link with { State = LinkState.RemoteGone });
            }
            else
            {
                links.Add(link.State == LinkState.Active ? link with { State = LinkState.LocalDeleted } : link);
            }
        }

        foreach (var there in input.Remote.Where(r => !linkedRemote.Contains(r.Id)))
        {
            var importId = ImportId(input.Connector, there.Id);
            if (there.Marker is { } marker)
            {
                // Ours: linked to its task (never copied). Its task isn't here: deleted, or not on this computer yet.
                if (local.TryGetValue(marker, out var marked) && !marked.Archived && linkedLocal.Add(marker))
                {
                    Merge(marked, there, null, new ConnectorLink(marker, there.Id, null));
                }
            }
            else if (local.TryGetValue(importId, out var imported))
            {
                // Imported before, but the link wasn't saved: finish the import.
                if (linkedLocal.Add(importId))
                {
                    Merge(imported, there, there.Fields, new ConnectorLink(importId, there.Id, null));
                }
            }
            else if (input.Direction != ConnectorDirection.ExportOnly && !there.Fields.Done)
            {
                linkedLocal.Add(importId);
                ops.Add(new CreateLocal(importId, there.Id, there.Fields));
                ops.Add(new MarkRemote(there.Id, importId));
                links.Add(new ConnectorLink(importId, there.Id, there.Fields));
            }
        }

        if (input.Direction != ConnectorDirection.ImportOnly)
        {
            foreach (var here in input.Local.Where(l => l.Discoverable && !l.Archived && !linkedLocal.Contains(l.Id)))
            {
                ops.Add(new CreateRemote(here.Id, here.Fields));
            }
        }

        return new ConnectorPlanResult(ops, links);
    }

    /// <summary>
    /// The links to keep after the sync: a link whose changes didn't all go through keeps its old version (so they are
    /// tried again); new outside items are linked.
    /// </summary>
    public static IReadOnlyList<ConnectorLink> Commit(IReadOnlyList<ConnectorLink> previous, ConnectorPlanResult plan, IReadOnlyList<ConnectorOpResult> done)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(done);
        var succeeded = done.Select(d => d.Op).ToHashSet();
        var result = new List<ConnectorLink>();
        foreach (var link in plan.Links)
        {
            // A missing mark is written again next time; it never holds the link back.
            if (!plan.Ops.Any(op => Concerns(op, link) && op is not MarkRemote && !succeeded.Contains(op)))
            {
                result.Add(link);
            }
            else if (previous.FirstOrDefault(p => p.RemoteId == link.RemoteId) is { } old)
            {
                result.Add(old);
            }
        }

        foreach (var created in done.Where(d => d.Op is CreateRemote && d.CreatedRemoteId is not null))
        {
            var op = (CreateRemote)created.Op;
            result.Add(new ConnectorLink(op.LocalId, created.CreatedRemoteId!, op.Fields));
        }

        return result;
    }

    private static bool Concerns(ConnectorOp op, ConnectorLink link) => op switch
    {
        CreateLocal c => c.RemoteId == link.RemoteId,
        UpdateLocal u => u.LocalId == link.LocalId,
        UpdateRemote u => u.RemoteId == link.RemoteId,
        ArchiveRemote a => a.RemoteId == link.RemoteId,
        MarkRemote m => m.RemoteId == link.RemoteId,
        _ => false,
    };

    /// <summary>Field by field: the side that changed since <paramref name="snapshot"/> wins; both changed (or no
    /// snapshot): the task here wins. Fields the connector doesn't have keep the value here.</summary>
    private static SyncedFields MergeFields(SyncedFields here, SyncedFields there, SyncedFields? snapshot, ConnectorFields fields, Func<SyncedFields, SyncedFields> n)
    {
        T Pick<T>(ConnectorFields field, Func<SyncedFields, T> get)
        {
            if (!fields.HasFlag(field) || snapshot is null)
            {
                return get(here);
            }

            var localChanged = !SyncedFields.Same(here, snapshot, field);
            var remoteChanged = !SyncedFields.Same(n(there), n(snapshot), field);
            return remoteChanged && !localChanged ? get(there) : get(here);
        }

        return new SyncedFields(
            Pick(ConnectorFields.Title, f => f.Title),
            Pick(ConnectorFields.Done, f => f.Done),
            Pick(ConnectorFields.Due, f => f.Due),
            Pick(ConnectorFields.Priority, f => f.Priority),
            Pick(ConnectorFields.Tags, f => f.Tags),
            Pick(ConnectorFields.Notes, f => f.Notes));
    }
}
