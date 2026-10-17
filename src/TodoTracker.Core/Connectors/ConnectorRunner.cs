using System.Globalization;

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
/// <summary>Which group a connector keeps in step, and which side new tasks are created on. <paramref name="AdoptMarked"/>:
/// the outside list is where the tasks live (see <see cref="ConnectorPlanInput.AdoptMarked"/>).
/// <paramref name="GroupOnly"/>: only the group's tasks are in this list (a repository per group): a task moved to
/// another group leaves it, as if deleted here (its item is archived), and goes to that group's list.</summary>
public sealed record ConnectorTarget(Guid GroupId, ConnectorDirection Direction, bool AdoptMarked = false, bool GroupOnly = false);

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

        var plan = ConnectorPlan.Make(new ConnectorPlanInput(connector, local, items, links, target.Direction, remote.Fields) { Normalize = remote.Normalize, AdoptMarked = target.AdoptMarked });
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
        if (board.Find(c.Id) is not null)
        {
            // Already here (another group, or finished long ago): linked as it is, never a second task with its id.
            return UpdateHere(board, new UpdateLocal(c.Id, c.Fields), fields);
        }

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
        // Finished first: a repeat rule applied after it doesn't move a finished task on to its next time.
        if (f.Done)
        {
            board.Complete(c.Id, _actor, now);
        }

        if (fields.HasFlag(ConnectorFields.Extras))
        {
            ApplyExtras(board, board.Find(c.Id)!, f, now);
        }

        return 0;
    }

    private int UpdateHere(TaskBoard board, UpdateLocal u, ConnectorFields fields)
    {
        var now = time.GetUtcNow();
        var item = board.Find(u.LocalId) ?? throw new KeyNotFoundException("The task is gone.");
        var current = FieldsOf(item, now);
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

        // Not done after all: reopened before the rest (a planned block or a column can't be set on a finished task).
        if (fields.HasFlag(ConnectorFields.Done) && current.Done && !f.Done)
        {
            board.Reopen(u.LocalId, _actor, now);
            current = current with { Done = false };
        }

        if (fields.HasFlag(ConnectorFields.Extras) && !SyncedFields.Same(current, f, ConnectorFields.Extras))
        {
            ApplyExtras(board, item, f, now);
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

    private SyncedFields FieldsOf(WorkItem item, DateTimeOffset now) => new(
        item.Title,
        item.CompletedAt is not null,
        item.Deadline is { } deadline ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(deadline, zone).DateTime) : null,
        item.Priority,
        [.. item.Tags],
        item.Details)
    {
        Extras = ExtrasOf(item, now),
    };

    /// <summary>The time format extras use (UTC, to the minute).</summary>
    public static string Moment(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseMoment(string text) =>
        DateTimeOffset.TryParseExact(text.Trim(), "yyyy-MM-dd'T'HH:mm'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>A task's extras (see <see cref="ExtraKeys"/>): only what's set.</summary>
    public static Dictionary<string, string> ExtrasOf(WorkItem item, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        var extras = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                extras[key] = value;
            }
        }

        // A snooze that's over isn't one any more.
        Set(ExtraKeys.Snooze, item.NextActionAt is { } at && at > now && item.CompletedAt is null ? Moment(at) : null);
        Set(ExtraKeys.Remind, string.Join(',', PendingReminders(item).Select(r => Moment(r.DueAt)).Distinct().Order(StringComparer.Ordinal)));
        // Inbox is where tasks start: only another column is worth saying.
        Set(ExtraKeys.Stage, item.Parent is null && item.Stage != Stage.Inbox ? item.Stage.ToString().ToLowerInvariant() : null);
        Set(ExtraKeys.Labels, string.Join('\n', item.Labels.Order(StringComparer.OrdinalIgnoreCase)));
        Set(ExtraKeys.Icon, item.Icon);
        Set(ExtraKeys.Color, item.Color);
        Set(ExtraKeys.Plan, item.PlannedAt is { } plan ? $"{Moment(plan)}/{(int)item.PlannedLength.TotalMinutes}" : null);
        Set(ExtraKeys.Repeat, item.Repeat?.ToString());
        Set(ExtraKeys.Energy, item.Energy?.ToString(CultureInfo.InvariantCulture));
        return extras;
    }

    /// <summary>Reminders set by hand that haven't gone off or been dismissed (a snooze's own reminder goes with the snooze).</summary>
    private static IEnumerable<Reminder> PendingReminders(WorkItem item) =>
        item.Reminders.Where(r => r.Kind == ReminderKind.Manual && r.NotifiedAt is null && r.DismissedAt is null);

    /// <summary>
    /// Makes the task's extras what <paramref name="f"/> says. A value this app can't take (a color that isn't one, a
    /// plan on a finished task) is left as it is here: one bad value never holds up the rest of the task.
    /// </summary>
    private void ApplyExtras(TaskBoard board, WorkItem item, SyncedFields f, DateTimeOffset now)
    {
        var current = ExtrasOf(item, now);
        string Now(string key) => current.TryGetValue(key, out var v) ? v : string.Empty;
        bool Changed(string key) => !SyncedFields.SameExtra(Now(key), f.Extra(key));
        void Try(Action change)
        {
            try
            {
                change();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException)
            {
                // left as it is here
            }
        }

        if (Changed(ExtraKeys.Snooze) && item.CompletedAt is null)
        {
            if (ParseMoment(f.Extra(ExtraKeys.Snooze)) is { } until)
            {
                Try(() => board.ScheduleNextAction(item.Id, until, _actor, now, notify: true));
            }
            else if (f.Extra(ExtraKeys.Snooze).Length == 0 && item.NextActionAt is not null)
            {
                Try(() => board.ClearNextAction(item.Id, _actor, now));
            }
        }

        if (Changed(ExtraKeys.Remind)) Try(() =>
        {
            var wanted = f.Extra(ExtraKeys.Remind).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ParseMoment).OfType<DateTimeOffset>().Select(Moment).ToHashSet(StringComparer.Ordinal);
            foreach (var reminder in PendingReminders(item).ToList())
            {
                if (!wanted.Remove(Moment(reminder.DueAt)))
                {
                    board.DismissReminder(item.Id, reminder.Id, _actor, now);
                }
            }

            foreach (var at in wanted)
            {
                board.AddReminder(item.Id, ParseMoment(at)!.Value, null, _actor, now);
            }
        });

        // A finished task's column doesn't matter (setting one would reopen it).
        if (Changed(ExtraKeys.Stage) && item.Parent is null && item.CompletedAt is null && (f.Extra(ExtraKeys.Stage).Length == 0 ? Stage.Inbox : Enum.TryParse<Stage>(f.Extra(ExtraKeys.Stage), ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : (Stage?)null) is { } stage)
        {
            Try(() => board.SetStage(item.Id, stage, _actor, now));
        }

        if (Changed(ExtraKeys.Labels))
        {
            Try(() => board.SetLabels(item.Id, f.Extra(ExtraKeys.Labels).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), _actor, now));
        }

        if (Changed(ExtraKeys.Plan))
        {
            var parts = f.Extra(ExtraKeys.Plan).Split('/');
            if (ParseMoment(parts[0]) is { } start)
            {
                Try(() => board.Plan(item.Id, start, parts.Length > 1 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? minutes : null, _actor, now));
            }
            else if (f.Extra(ExtraKeys.Plan).Length == 0 && item.PlannedAt is not null)
            {
                Try(() => board.Unplan(item.Id, _actor, now));
            }
        }

        var look = new TaskChanges
        {
            Icon = Changed(ExtraKeys.Icon) && f.Extra(ExtraKeys.Icon).Length > 0 ? f.Extra(ExtraKeys.Icon) : null,
            ClearIcon = Changed(ExtraKeys.Icon) && f.Extra(ExtraKeys.Icon).Length == 0,
            Color = Changed(ExtraKeys.Color) && f.Extra(ExtraKeys.Color).Length > 0 ? f.Extra(ExtraKeys.Color) : null,
            ClearColor = Changed(ExtraKeys.Color) && f.Extra(ExtraKeys.Color).Length == 0,
        };
        if (look.Icon is not null || look.ClearIcon || look.Color is not null || look.ClearColor)
        {
            Try(() => board.Update(item.Id, look, _actor, now));
        }

        if (Changed(ExtraKeys.Repeat))
        {
            Try(() => board.Update(item.Id, f.Extra(ExtraKeys.Repeat).Length > 0 ? new TaskChanges { Repeat = f.Extra(ExtraKeys.Repeat) } : new TaskChanges { ClearRepeat = true }, _actor, now));
        }

        if (Changed(ExtraKeys.Energy))
        {
            Try(() => board.Update(item.Id, int.TryParse(f.Extra(ExtraKeys.Energy), NumberStyles.None, CultureInfo.InvariantCulture, out var level) ? new TaskChanges { Energy = level } : new TaskChanges { ClearEnergy = true }, _actor, now));
        }
    }

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
            if (target.GroupOnly && item.GroupId != target.GroupId)
            {
                continue; // in another group's list now
            }

            if (discoverable || linked.Contains(item.Id))
            {
                result.Add(new LocalItem(item.Id, FieldsOf(item, now), discoverable, item.IsArchived));
            }
        }

        return result;
    }
}
