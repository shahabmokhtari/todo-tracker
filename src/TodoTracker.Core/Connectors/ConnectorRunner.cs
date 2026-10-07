namespace TodoTracker.Core.Connectors;

/// <summary>An outside list (a Notion database, a Microsoft To Do list) as the sync sees it.</summary>
public interface IConnectorRemote
{
    /// <summary>The fields this list has (the others stay as they are here).</summary>
    ConnectorFields Fields { get; }

    /// <summary>How this list shows a task's fields (e.g. no "critical"), so approximations aren't taken as changes.</summary>
    SyncedFields Normalize(SyncedFields fields) => fields;

    Task<IReadOnlyList<RemoteItem>> ListAsync(CancellationToken cancellationToken);

    /// <summary>One item by id, or null when it's gone (deleted, archived, no access).</summary>
    Task<RemoteItem?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Creates an item carrying <paramref name="localId"/> as its marker; returns its id.</summary>
    Task<string> CreateAsync(Guid localId, SyncedFields fields, CancellationToken cancellationToken);

    /// <summary>Writes the fields that differ from <paramref name="previous"/> (what the item had), so values this app
    /// doesn't know (Notion's "In progress", a time of day) stay; null: writes them all.</summary>
    Task UpdateAsync(string id, SyncedFields fields, SyncedFields? previous, CancellationToken cancellationToken);

    /// <summary>Archives the item (or finishes it, where nothing can be archived); it can be brought back there.</summary>
    Task ArchiveAsync(string id, CancellationToken cancellationToken);

    Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken);
}

/// <summary>Which group a connector keeps in step, and which side new tasks are created on.</summary>
public sealed record ConnectorTarget(Guid GroupId, ConnectorDirection Direction);

/// <summary>What a run did: the plan, how many changes went through, what went wrong, and the links to keep.</summary>
public sealed record ConnectorRun(ConnectorPlanResult Plan, IReadOnlyList<ConnectorOpResult> Done, IReadOnlyList<string> Problems, IReadOnlyList<ConnectorLink> Links)
{
    public int Failed => Problems.Count;
}

/// <summary>
/// Runs a connector: reads the tasks here and the outside list, works out the changes (<see cref="ConnectorPlan"/>),
/// makes them, and returns the links to keep. A change that fails is reported and tried again next run.
/// </summary>
public sealed class ConnectorRunner(string connector, string name, IBoardStore store, TimeProvider time, TimeZoneInfo zone)
{
    /// <summary>Finished tasks this recent still go out when they aren't linked yet.</summary>
    public static readonly TimeSpan RecentlyDone = TimeSpan.FromDays(14);

    /// <summary>A date-only due date becomes a deadline at this time of day (like "due:tomorrow").</summary>
    public static readonly TimeOnly DueTime = new(17, 0);

    private readonly Actor _actor = new(ActorKind.Connector, name);

    /// <summary>What a run would do, without doing it.</summary>
    public async Task<ConnectorPlanResult> PlanAsync(IConnectorRemote remote, ConnectorTarget target, IReadOnlyList<ConnectorLink> links, CancellationToken cancellationToken) =>
        (await PlanWithItemsAsync(remote, target, links, cancellationToken).ConfigureAwait(false)).Plan;

    private async Task<(ConnectorPlanResult Plan, Dictionary<string, RemoteItem> Items)> PlanWithItemsAsync(IConnectorRemote remote, ConnectorTarget target, IReadOnlyList<ConnectorLink> links, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(links);
        var now = time.GetUtcNow();
        var local = await store.ReadAsync(b => LocalItems(b, target, links, now), cancellationToken).ConfigureAwait(false);
        var items = (await remote.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();

        // A linked item that isn't in the list (a filtered view, archived) is looked up before it counts as gone. Items
        // already known to be gone aren't looked up every run: they come back when they're in the list again.
        var listed = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var link in links.Where(l => l.State == LinkState.Active && !listed.Contains(l.RemoteId)))
        {
            if (await remote.FindAsync(link.RemoteId, cancellationToken).ConfigureAwait(false) is { } found)
            {
                items.Add(found);
            }
        }

        var plan = ConnectorPlan.Make(new ConnectorPlanInput(connector, local, items, links, target.Direction, remote.Fields) { Normalize = remote.Normalize });
        return (plan, items.GroupBy(i => i.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal));
    }

    /// <summary>
    /// Syncs once. Never throws for something going wrong on either side (only when <paramref name="cancellationToken"/>
    /// is cancelled): it's in <see cref="ConnectorRun.Problems"/>, and what didn't go through is tried next time.
    /// </summary>
    public async Task<ConnectorRun> RunAsync(IConnectorRemote remote, ConnectorTarget target, IReadOnlyList<ConnectorLink> links, CancellationToken cancellationToken)
    {
        ConnectorPlanResult plan;
        Dictionary<string, RemoteItem> items;
        try
        {
            (plan, items) = await PlanWithItemsAsync(remote, target, links, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProblem(ex, cancellationToken))
        {
            return new ConnectorRun(new ConnectorPlanResult([], links), [], [ex.Message], links);
        }

        var done = new List<ConnectorOpResult>();
        var problems = new List<string>();
        var notImported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in plan.Ops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (op is MarkRemote m && notImported.Contains(m.RemoteId))
            {
                continue; // its task wasn't created: no mark (so the import is tried again)
            }

            try
            {
                done.Add(new ConnectorOpResult(op, await ApplyAsync(op, remote, target, items, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception ex) when (IsProblem(ex, cancellationToken))
            {
                problems.Add($"{Describe(op)}: {ex.Message}");
                if (op is CreateLocal c)
                {
                    notImported.Add(c.RemoteId);
                }
            }
        }

        return new ConnectorRun(plan, done, problems, ConnectorPlan.Commit(links, plan, done));
    }

    /// <summary>Anything going wrong with a sync is reported, not thrown (except running out of memory, or being asked to stop).</summary>
    public static bool IsProblem(Exception ex, CancellationToken cancellationToken) =>
        ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static string Describe(ConnectorOp op) => op switch
    {
        CreateLocal c => $"Adding \"{c.Fields.Title}\" here",
        CreateRemote c => $"Sending \"{c.Fields.Title}\"",
        UpdateLocal u => $"Updating \"{u.Fields.Title}\" here",
        UpdateRemote u => $"Updating \"{u.Fields.Title}\"",
        ArchiveRemote => "Archiving a deleted task",
        MarkRemote => "Linking an item",
        _ => op.GetType().Name,
    };

    private async Task<string?> ApplyAsync(ConnectorOp op, IConnectorRemote remote, ConnectorTarget target, Dictionary<string, RemoteItem> items, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case CreateRemote c:
                return await remote.CreateAsync(c.LocalId, c.Fields, cancellationToken).ConfigureAwait(false);
            case UpdateRemote u:
                await remote.UpdateAsync(u.RemoteId, u.Fields, items.GetValueOrDefault(u.RemoteId)?.Fields, cancellationToken).ConfigureAwait(false);
                return null;
            case ArchiveRemote a:
                await remote.ArchiveAsync(a.RemoteId, cancellationToken).ConfigureAwait(false);
                return null;
            case MarkRemote m:
                await remote.MarkAsync(m.RemoteId, m.LocalId, cancellationToken).ConfigureAwait(false);
                return null;
            case CreateLocal c:
                await store.UpdateAsync(b => CreateHere(b, c, remote.Fields, target), cancellationToken).ConfigureAwait(false);
                return null;
            case UpdateLocal u:
                await store.UpdateAsync(b => UpdateHere(b, u, remote.Fields), cancellationToken).ConfigureAwait(false);
                return null;
            default:
                throw new InvalidOperationException($"Unknown change {op.GetType().Name}.");
        }
    }

    private int CreateHere(TaskBoard board, CreateLocal c, ConnectorFields fields, ConnectorTarget target)
    {
        var now = time.GetUtcNow();
        var groupId = board.Groups.Any(g => g.Id == target.GroupId) ? target.GroupId : (Guid?)null;
        var f = c.Fields;
        board.AddTask(
            new NewTask(f.Title)
            {
                Id = c.Id,
                GroupId = groupId,
                Priority = f.Priority,
                Deadline = f.Due is { } due ? DeadlineOn(due) : null,
                Tags = fields.HasFlag(ConnectorFields.Tags) ? f.Tags : null,
                Details = fields.HasFlag(ConnectorFields.Notes) ? f.Notes : null,
            },
            _actor,
            now);
        if (f.Done)
        {
            board.Complete(c.Id, _actor, now);
        }

        return 0;
    }

    private int UpdateHere(TaskBoard board, UpdateLocal u, ConnectorFields fields)
    {
        var now = time.GetUtcNow();
        var item = board.Find(u.LocalId) ?? throw new KeyNotFoundException("The task is gone.");
        var current = FieldsOf(item);
        var f = u.Fields;
        var dueChanged = fields.HasFlag(ConnectorFields.Due) && current.Due != f.Due;
        var changes = new TaskChanges
        {
            Title = fields.HasFlag(ConnectorFields.Title) && current.Title != f.Title ? f.Title : null,
            Priority = fields.HasFlag(ConnectorFields.Priority) && current.Priority != f.Priority ? f.Priority : null,
            // Same day: the time of day here is kept.
            Deadline = dueChanged && f.Due is { } due ? DeadlineOn(due) : null,
            ClearDeadline = dueChanged && f.Due is null,
            Details = fields.HasFlag(ConnectorFields.Notes) && (current.Notes ?? string.Empty).TrimEnd() != (f.Notes ?? string.Empty).TrimEnd() ? f.Notes ?? string.Empty : null,
        };
        if (changes.Title is not null || changes.Priority is not null || changes.Deadline is not null || changes.ClearDeadline || changes.Details is not null)
        {
            board.Update(u.LocalId, changes, _actor, now);
        }

        if (fields.HasFlag(ConnectorFields.Tags) && !SyncedFields.Same(current, f, ConnectorFields.Tags))
        {
            board.SetTags(u.LocalId, f.Tags, _actor, now);
        }

        // A step that's waiting for the one before it can't be finished yet: it is next time it can.
        if (fields.HasFlag(ConnectorFields.Done) && current.Done != f.Done && (!f.Done || TaskBoard.FindBlockingStep(board.Find(u.LocalId)!) is null))
        {
            if (f.Done)
            {
                board.Complete(u.LocalId, _actor, now);
            }
            else
            {
                board.Reopen(u.LocalId, _actor, now);
            }
        }

        return 0;
    }

    private DateTimeOffset DeadlineOn(DateOnly day)
    {
        var local = day.ToDateTime(DueTime);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private SyncedFields FieldsOf(WorkItem item) => new(
        item.Title,
        item.CompletedAt is not null,
        item.Deadline is { } deadline ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(deadline, zone).DateTime) : null,
        item.Priority,
        [.. item.Tags],
        item.Details);

    private List<LocalItem> LocalItems(TaskBoard board, ConnectorTarget target, IReadOnlyList<ConnectorLink> links, DateTimeOffset now)
    {
        var linked = links.Select(l => l.LocalId).ToHashSet();
        var result = new List<LocalItem>();
        // Every task (a linked one may have become a subtask); only top-level ones are new candidates. Archiving is
        // kept on the top-level task, so a subtask counts as archived with it.
        foreach (var item in board.AllItems())
        {
            var discoverable = item.Parent is null && item.GroupId == target.GroupId && !item.IsArchived
                && (item.CompletedAt is null || now - item.CompletedAt < RecentlyDone);
            if (discoverable || linked.Contains(item.Id))
            {
                result.Add(new LocalItem(item.Id, FieldsOf(item), discoverable, item.IsArchived));
            }
        }

        return result;
    }
}
