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

    Task UpdateAsync(string id, SyncedFields fields, CancellationToken cancellationToken);

    /// <summary>Archives the item (or finishes it, where nothing can be archived); it can be brought back there.</summary>
    Task ArchiveAsync(string id, CancellationToken cancellationToken);

    Task MarkAsync(string id, Guid localId, CancellationToken cancellationToken);
}

/// <summary>Which group a connector keeps in step, and which side new tasks are created on.</summary>
public sealed record ConnectorTarget(Guid GroupId, ConnectorDirection Direction);

/// <summary>What a run did: the plan, how many changes went through, what went wrong, and the links to keep.</summary>
public sealed record ConnectorRun(ConnectorPlanResult Plan, int Done, IReadOnlyList<string> Problems, IReadOnlyList<ConnectorLink> Links)
{
    public int Failed => Plan.Ops.Count - Done;
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
    public async Task<ConnectorPlanResult> PlanAsync(IConnectorRemote remote, ConnectorTarget target, IReadOnlyList<ConnectorLink> links, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(links);
        var now = time.GetUtcNow();
        var local = await store.ReadAsync(b => LocalItems(b, target, links, now), cancellationToken).ConfigureAwait(false);
        var items = (await remote.ListAsync(cancellationToken).ConfigureAwait(false)).ToList();

        // A linked item that isn't in the list (a filtered view, archived) is looked up before it counts as gone.
        var listed = items.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var link in links.Where(l => l.State != LinkState.LocalDeleted && !listed.Contains(l.RemoteId)))
        {
            if (await remote.FindAsync(link.RemoteId, cancellationToken).ConfigureAwait(false) is { } found)
            {
                items.Add(found);
            }
        }

        return ConnectorPlan.Make(new ConnectorPlanInput(connector, local, items, links, target.Direction, remote.Fields) { Normalize = remote.Normalize });
    }

    public async Task<ConnectorRun> RunAsync(IConnectorRemote remote, ConnectorTarget target, IReadOnlyList<ConnectorLink> links, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(remote, target, links, cancellationToken).ConfigureAwait(false);
        var done = new List<ConnectorOpResult>();
        var problems = new List<string>();
        foreach (var op in plan.Ops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                done.Add(new ConnectorOpResult(op, await ApplyAsync(op, remote, target, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException or KeyNotFoundException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                problems.Add($"{Describe(op)}: {ex.Message}");
            }
        }

        return new ConnectorRun(plan, done.Count, problems, ConnectorPlan.Commit(links, plan, done));
    }

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

    private async Task<string?> ApplyAsync(ConnectorOp op, IConnectorRemote remote, ConnectorTarget target, CancellationToken cancellationToken)
    {
        switch (op)
        {
            case CreateRemote c:
                return await remote.CreateAsync(c.LocalId, c.Fields, cancellationToken).ConfigureAwait(false);
            case UpdateRemote u:
                await remote.UpdateAsync(u.RemoteId, u.Fields, cancellationToken).ConfigureAwait(false);
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

        if (fields.HasFlag(ConnectorFields.Done) && current.Done != f.Done)
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
        foreach (var item in board.Items)
        {
            var discoverable = item.Parent is null && item.GroupId == target.GroupId && item.ArchivedAt is null
                && (item.CompletedAt is null || now - item.CompletedAt < RecentlyDone);
            if (discoverable || linked.Contains(item.Id))
            {
                result.Add(new LocalItem(item.Id, FieldsOf(item), discoverable, item.ArchivedAt is not null));
            }
        }

        return result;
    }
}
