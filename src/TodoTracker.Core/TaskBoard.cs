using System.Text.RegularExpressions;

namespace TodoTracker.Core;

/// <summary>
/// Aggregate root for all tasks, groups, activity, and focus state. Every mutation validates
/// its inputs before changing anything and records an <see cref="ActivityEntry"/>.
/// </summary>
public sealed partial class TaskBoard
{
    public const int CurrentSchemaVersion = 1;
    public const int MaxTitleLength = 300;
    public const int MaxTextLength = 10_000;

    internal readonly List<WorkItem> RootList = [];
    internal readonly List<ActivityEntry> ActivityList = [];
    internal readonly List<TaskGroup> GroupList = [];
    private readonly Dictionary<Guid, WorkItem> _index = [];

    public TaskBoard()
        : this(seedDefaultGroups: true)
    {
    }

    internal TaskBoard(bool seedDefaultGroups)
    {
        if (seedDefaultGroups)
        {
            SeedDefaultGroups();
        }
    }

    public IReadOnlyList<WorkItem> Items => RootList;

    public IReadOnlyList<ActivityEntry> Activity => ActivityList;

    public IReadOnlyList<TaskGroup> Groups => GroupList;

    public Guid DefaultGroupId => GroupList[0].Id;

    public PomodoroTimer Pomodoro { get; internal set; } = new();

    public IEnumerable<WorkItem> AllItems() => RootList.SelectMany(r => r.SelfAndDescendants());

    public WorkItem? Find(Guid id) => _index.GetValueOrDefault(id);

    public WorkItem Get(Guid id) => Find(id) ?? throw TaskNotFoundException.ForTask(id);

    public TaskGroup GetGroup(Guid id) => GroupList.Find(g => g.Id == id) ?? throw TaskNotFoundException.ForGroup(id);

    public WorkItem AddTask(NewTask spec, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var title = RequireText(spec.Title, nameof(spec.Title), MaxTitleLength, "A task title is required.");
        ValidateStepDelay(spec.StepDelay);
        var tags = NormalizeTags(spec.Tags);
        var labels = ValidateLabelNames(spec.Labels);
        var parent = spec.ParentId is { } parentId ? Get(parentId) : null;
        var groupId = parent is null ? (spec.GroupId is { } g ? GetGroup(g).Id : DefaultGroupId) : parent.GroupId;

        if (spec.Id is { } chosen && (chosen == Guid.Empty || Find(chosen) is not null))
        {
            throw chosen == Guid.Empty
                ? new ArgumentException("A task id can't be empty.", nameof(spec))
                : new InvalidOperationException("There is already a task with this id.");
        }

        var item = new WorkItem(spec.Id ?? Guid.NewGuid(), title, spec.Priority, now)
        {
            Details = OptionalText(spec.Details, MaxTextLength),
            Deadline = spec.Deadline,
            Sequential = spec.Sequential,
            StepDelay = spec.StepDelay,
            OwnGroupId = groupId,
            Stage = spec.Stage ?? Stage.Inbox,
        };
        item.TagList.AddRange(tags);
        item.LabelList.AddRange(labels.Select(l => EnsureLabel(l).Name));
        Attach(item, parent);
        Log(now, item.Id, ActivityKind.Created, parent is null ? $"Created \"{title}\"" : $"Added \"{title}\" to \"{parent.Title}\"", actor);
        return item;
    }

    public IReadOnlyList<WorkItem> AddSteps(Guid parentId, IEnumerable<string> titles, TimeSpan? stepDelay, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(titles);
        var parent = Get(parentId);
        ValidateStepDelay(stepDelay);
        var clean = titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => RequireText(t, nameof(titles), MaxTitleLength, "Step titles are required.")).ToList();
        if (clean.Count == 0)
        {
            throw new ArgumentException("At least one step title is required.", nameof(titles));
        }

        parent.Sequential = true;
        parent.StepDelay = stepDelay ?? parent.StepDelay;
        return clean.Select(t => AddTask(new NewTask(t) { ParentId = parent.Id }, actor, now)).ToList();
    }

    public void Update(Guid id, TaskChanges changes, Actor actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var item = Get(id);
        var title = changes.Title is null ? item.Title : RequireText(changes.Title, nameof(changes.Title), MaxTitleLength, "A task title is required.");
        ValidateStepDelay(changes.StepDelay);

        item.Title = title;
        if (changes.Details is not null)
        {
            item.Details = OptionalText(changes.Details, MaxTextLength);
        }

        item.Priority = changes.Priority ?? item.Priority;
        item.Deadline = changes.ClearDeadline ? null : changes.Deadline ?? item.Deadline;
        item.Sequential = changes.Sequential ?? item.Sequential;
        item.StepDelay = changes.ClearStepDelay ? null : changes.StepDelay ?? item.StepDelay;
        Log(now, id, ActivityKind.Updated, $"Updated \"{item.Title}\"", actor);
    }

    public void Delete(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);

        // A timer on what's deleted ends now. A subtask's time stays with its task (the work happened).
        StopTimers(item.SelfAndDescendants(), now);
        if (item.Parent is { } parent)
        {
            parent.ChildList.Remove(item);
            parent.TimeEntryList.AddRange(item.SelfAndDescendants().SelectMany(i => i.TimeEntryList));
            parent.TimeEntryList.Sort((a, b) => a.Start.CompareTo(b.Start));
        }
        else
        {
            RootList.Remove(item);
        }

        foreach (var removed in item.SelfAndDescendants())
        {
            _index.Remove(removed.Id);
        }

        if (Pomodoro.ItemId is { } focused && !_index.ContainsKey(focused))
        {
            Pomodoro.DetachItem();
        }

        // Tasks waiting for what's gone come back, like when it's done.
        ReleaseWaiters(now, item.SelfAndDescendants().ToDictionary(i => i.Id, i => i.Title));

        Log(now, id, ActivityKind.Deleted, $"Deleted \"{item.Title}\"", actor);
    }

    public Note AddNote(Guid id, string text, Actor actor, DateTimeOffset now, string? sourceUrl = null, string? sourceTitle = null)
    {
        var item = Get(id);
        var clean = RequireText(text, nameof(text), MaxTextLength, "A note cannot be empty.");
        if (sourceUrl is not null && !IsHttpUrl(sourceUrl))
        {
            throw new ArgumentException("Source URLs must be absolute http(s) URLs.", nameof(sourceUrl));
        }

        var note = new Note(Guid.NewGuid(), now, clean, actor, sourceUrl, OptionalText(sourceTitle, MaxTitleLength));
        item.NoteList.Add(note);
        Log(now, id, ActivityKind.NoteAdded, clean, actor);
        return note;
    }

    /// <summary>Notes are autosaved while typed: the first save adds the note, later ones update it in place.
    /// Edits within 10 minutes of writing it are part of writing it (no extra timeline entries).</summary>
    public Note UpdateNote(Guid id, Guid noteId, string text, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var note = item.NoteList.Find(n => n.Id == noteId) ?? throw new TaskNotFoundException($"Note {noteId} was not found.");
        var clean = RequireText(text, nameof(text), MaxTextLength, "A note cannot be empty.");
        if (clean == note.Text)
        {
            return note;
        }

        note.Text = clean;
        if (now - note.At > TimeSpan.FromMinutes(10))
        {
            Log(now, id, ActivityKind.Updated, $"Edited a note on \"{item.Title}\"", actor);
        }

        return note;
    }

    public void Complete(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (item.IsDone)
        {
            return;
        }

        if (FindBlockingStep(item) is { } blocker)
        {
            throw new InvalidOperationException($"Finish \"{blocker.Title}\" before \"{item.Title}\".");
        }

        foreach (var descendant in item.SelfAndDescendants().Where(d => !d.IsDone))
        {
            descendant.CompletedAt = now;
        }

        StopTimers(item.SelfAndDescendants(), now);
        Log(now, id, ActivityKind.Completed, $"Completed \"{item.Title}\"", actor);
        AdvanceSequence(item, actor, now);
        ReleaseWaiters(now);
    }

    /// <summary>
    /// Snoozes a task until another one is done ("after task X"); then it comes back with a reminder. Replaces a
    /// snooze until a time.
    /// </summary>
    public void WaitFor(Guid id, Guid afterId, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var other = Get(afterId);
        if (other == item)
        {
            throw new ArgumentException("A task can't wait for itself.", nameof(afterId));
        }

        if (WaitProblem(item, other) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        item.AfterId = other.Id;
        item.NextActionAt = null;
        DismissPendingScheduleReminders(item, now);
        Log(now, id, ActivityKind.Scheduled, $"Waits until \"{other.Title}\" is done", actor);
    }

    /// <summary>Whether <paramref name="id"/> may wait for <paramref name="afterId"/> (what the pickers offer).</summary>
    public bool CanWaitFor(Guid id, Guid afterId) =>
        Find(id) is { } item && Find(afterId) is { } other && other != item && WaitProblem(item, other) is null;

    /// <summary>Why a task can't wait for another, or null when it can.</summary>
    private static string? WaitProblem(WorkItem item, WorkItem other)
    {
        if (item.IsDone)
        {
            return $"\"{item.Title}\" is already done.";
        }

        if (other.IsDone)
        {
            return $"\"{other.Title}\" is already done.";
        }

        // Its own subtasks or parents would never get done while it waits.
        var family = item.SelfAndDescendants().Concat(item.Ancestors()).ToHashSet();
        if (family.Contains(other))
        {
            return $"\"{item.Title}\" can't wait for \"{other.Title}\", which is part of it.";
        }

        // Nor would a circle: follow what holds "other" up (what it, its parents and its open subtasks wait for, and its
        // open subtasks themselves); reaching this task's family means they'd wait for each other.
        var seen = new HashSet<WorkItem>();
        var queue = new Queue<WorkItem>([other]);
        while (queue.TryDequeue(out var next))
        {
            if (!seen.Add(next))
            {
                continue;
            }

            if (family.Contains(next))
            {
                return $"\"{other.Title}\" is already waiting for \"{item.Title}\": they'd wait for each other.";
            }

            foreach (var holder in next.Ancestors().Prepend(next).Select(a => a.WaitingFor).OfType<WorkItem>().Concat(next.Children.Where(c => !c.IsDone)))
            {
                queue.Enqueue(holder);
            }
        }

        return null;
    }

    /// <summary>
    /// Tasks whose wait ended come back, with a reminder saying why: what they waited for is done (however it got done:
    /// the app, an edit in the files, or sync), or was just <paramref name="deleted"/> here. A task merely missing (its
    /// file can't be read right now, or hasn't synced yet) doesn't end a wait. Waiters under <paramref name="skipRoots"/>
    /// (files that can't be saved right now) are left for later. Returns whether any came back.
    /// </summary>
    internal bool ReleaseWaiters(DateTimeOffset now, IReadOnlyDictionary<Guid, string>? deleted = null, IReadOnlySet<Guid>? skipRoots = null)
    {
        bool Ended(Guid after) => _index.TryGetValue(after, out var other) ? other.IsDone : deleted?.ContainsKey(after) == true;
        var released = false;
        foreach (var waiter in AllItems().Where(i => i.AfterId is { } after && Ended(after) && skipRoots?.Contains(i.Root.Id) != true).ToList())
        {
            var after = waiter.AfterId!.Value;
            var why = _index.TryGetValue(after, out var other) ? $"\"{other.Title}\" is done" : $"\"{deleted![after]}\" was deleted";
            waiter.AfterId = null;
            released = true;
            if (!waiter.IsDone)
            {
                waiter.ReminderList.Add(new Reminder(Guid.NewGuid(), now, $"{why}: back to \"{waiter.Title}\"", ReminderKind.NextAction));
                Log(now, waiter.Id, ActivityKind.Scheduled, $"Back: {why}", Actor.System);
            }
        }

        return released;
    }

    public void Reopen(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (!item.IsDone)
        {
            return;
        }

        var completedAt = item.CompletedAt;
        item.CompletedAt = null;
        item.Root.ArchivedAt = null;

        // Undo the cascade from completing a parent: children finished by that same action reopen too.
        foreach (var descendant in item.SelfAndDescendants().Skip(1).Where(d => d.CompletedAt == completedAt))
        {
            descendant.CompletedAt = null;
        }

        foreach (var ancestor in item.Ancestors().Where(a => a.IsDone))
        {
            ancestor.CompletedAt = null;
        }

        Log(now, id, ActivityKind.Reopened, $"Reopened \"{item.Title}\"", actor);
    }

    /// <summary>Defers the item until <paramref name="at"/>. With <paramref name="notify"/>, a reminder fires at that time.</summary>
    public void ScheduleNextAction(Guid id, DateTimeOffset at, Actor actor, DateTimeOffset now, bool notify = false, string? message = null)
    {
        var item = Get(id);
        var cleanMessage = OptionalText(message, MaxTitleLength);
        item.NextActionAt = at;
        item.AfterId = null;
        DismissPendingScheduleReminders(item, now);
        if (notify)
        {
            item.ReminderList.Add(new Reminder(Guid.NewGuid(), at, cleanMessage ?? DefaultReminderMessage(item), ReminderKind.NextAction));
        }

        Log(now, id, ActivityKind.Scheduled, $"Next action {at:u}{(notify ? " (with reminder)" : string.Empty)}", actor);
    }

    public void ClearNextAction(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        item.NextActionAt = null;
        item.AfterId = null;
        DismissPendingScheduleReminders(item, now);
        Log(now, id, ActivityKind.Scheduled, "Cleared next action; back to Now", actor);
    }

    public Reminder AddReminder(Guid id, DateTimeOffset dueAt, string? message, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var reminder = new Reminder(Guid.NewGuid(), dueAt, OptionalText(message, MaxTitleLength) ?? DefaultReminderMessage(item), ReminderKind.Manual);
        item.ReminderList.Add(reminder);
        Log(now, id, ActivityKind.ReminderAdded, $"Reminder at {dueAt:u}: {reminder.Message}", actor);
        return reminder;
    }

    public void DismissReminder(Guid id, Guid reminderId, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var reminder = item.ReminderList.Find(r => r.Id == reminderId) ?? throw new TaskNotFoundException($"Reminder {reminderId} was not found.");
        if (reminder.DismissedAt is not null)
        {
            return;
        }

        reminder.DismissedAt = now;
        Log(now, id, ActivityKind.ReminderDismissed, $"Dismissed reminder: {reminder.Message}", actor);
    }

    internal void MarkReminderNotified(WorkItem item, Reminder reminder, DateTimeOffset now)
    {
        reminder.NotifiedAt = now;
        Log(now, item.Id, ActivityKind.ReminderFired, $"Reminder: {reminder.Message}", Actor.System);
    }

    public IReadOnlyList<ActivityEntry> Timeline(Guid? id = null)
    {
        IEnumerable<ActivityEntry> entries = ActivityList;
        if (id is { } itemId)
        {
            var scope = Get(itemId).SelfAndDescendants().Select(i => i.Id).ToHashSet();
            entries = entries.Where(e => scope.Contains(e.ItemId));
        }

        // Stable: newest first, preserving insertion order for identical timestamps (reversed).
        return entries.Select((e, i) => (e, i)).OrderByDescending(x => x.e.At).ThenByDescending(x => x.i).Select(x => x.e).ToList();
    }

    // ---- Groups -------------------------------------------------------------------------

    public TaskGroup AddGroup(string name, string? color, Actor actor, DateTimeOffset now)
    {
        var clean = ValidateGroup(name, color, except: null);
        var group = new TaskGroup(Guid.NewGuid(), clean, color);
        GroupList.Add(group);
        Log(now, Guid.Empty, ActivityKind.GroupChanged, $"Added group \"{clean}\"", actor);
        return group;
    }

    public void UpdateGroup(Guid groupId, string name, string? color, Actor actor, DateTimeOffset now)
    {
        var group = GetGroup(groupId);
        var clean = ValidateGroup(name, color, except: group);
        group.Name = clean;
        group.Color = color;
        Log(now, Guid.Empty, ActivityKind.GroupChanged, $"Updated group \"{clean}\"", actor);
    }

    /// <summary>Deletes a group, moving its tasks to <paramref name="moveTasksTo"/>.</summary>
    public void DeleteGroup(Guid groupId, Guid moveTasksTo, Actor actor, DateTimeOffset now)
    {
        var group = GetGroup(groupId);
        if (GroupList.Count == 1)
        {
            throw new InvalidOperationException("At least one group is required.");
        }

        var target = GetGroup(moveTasksTo);
        if (target == group)
        {
            throw new InvalidOperationException("Choose a different group to move the tasks into.");
        }

        foreach (var root in RootList.Where(r => r.OwnGroupId == group.Id))
        {
            root.OwnGroupId = target.Id;
        }

        GroupList.Remove(group);
        Log(now, Guid.Empty, ActivityKind.GroupChanged, $"Deleted group \"{group.Name}\"", actor);
    }

    public void MoveToGroup(Guid id, Guid groupId, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var group = GetGroup(groupId);
        if (item.Parent is not null)
        {
            throw new InvalidOperationException("Only top-level tasks can change group; subtasks follow their parent.");
        }

        item.OwnGroupId = group.Id;
        Log(now, id, ActivityKind.Moved, $"Moved \"{item.Title}\" to {group.Name}", actor);
    }

    // ---- Pomodoro -----------------------------------------------------------------------

    /// <summary>Starts a focus session (on a task, timed as focus time on this <paramref name="device"/>).</summary>
    public void StartFocus(Guid? itemId, Actor actor, DateTimeOffset now, string? device = null)
    {
        var item = itemId is { } id ? Get(id) : null;
        if (item is { IsDone: true })
        {
            throw new InvalidOperationException($"\"{item.Title}\" is already done.");
        }

        StopFocusTimer(now);
        Pomodoro.StartFocus(now, item?.Id);
        if (item is not null)
        {
            StartTimerCore(item, TimeSource.Focus, now, device);
            Log(now, item.Id, ActivityKind.FocusStarted, $"Focus started on \"{item.Title}\"", actor);
        }
    }

    public void PauseFocus(DateTimeOffset now)
    {
        if (Pomodoro.Phase == PomodoroPhase.Focus)
        {
            StopFocusTimer(now);
        }

        Pomodoro.Pause(now);
    }

    public void ResumeFocus(DateTimeOffset now, string? device = null)
    {
        var resuming = Pomodoro.Phase == PomodoroPhase.Focus && !Pomodoro.IsRunning;
        Pomodoro.Resume(now);

        // A timer started by hand meanwhile keeps running: the session goes on without timing its task.
        if (resuming && Pomodoro.ItemId is { } id && Find(id) is { IsDone: false } item && RunningTimer(now) is null)
        {
            StartTimerCore(item, TimeSource.Focus, now, device);
        }
    }

    public void SkipFocus(DateTimeOffset now)
    {
        // A session that has already run out ends on time first, so Skip skips the break that followed it.
        TickPomodoro(now);
        if (Pomodoro.Phase == PomodoroPhase.Focus)
        {
            StopFocusTimer(now);
        }

        Pomodoro.Skip(now);
    }

    public void ResetFocus(DateTimeOffset now)
    {
        StopFocusTimer(now);
        Pomodoro.Reset();
    }

    /// <summary>
    /// The task "Start next focus" picks up: the last session's, if it's still there and open (null: none; the apps
    /// then offer their top task).
    /// </summary>
    public WorkItem? NextFocusItem() =>
        Pomodoro.ItemId is { } id && Find(id) is { IsDone: false, IsArchived: false } item ? item : null;

    /// <summary>
    /// "Start next focus" (on the break screen, or when the break is over): ends the break and starts a session on
    /// the last session's task, else on <paramref name="fallbackItemId"/>. A session that just ran out counts first; one
    /// still running is never restarted (a second click or window mustn't cut it, and its time, short).
    /// </summary>
    public WorkItem? StartNextFocus(Guid? fallbackItemId, Actor actor, DateTimeOffset now, string? device = null)
    {
        TickPomodoro(now);
        if (Pomodoro.Phase == PomodoroPhase.Focus)
        {
            throw new InvalidOperationException("A focus session is already going.");
        }

        var item = NextFocusItem() ?? (fallbackItemId is { } id && Find(id) is { IsDone: false } fallback ? fallback : null);
        StartFocus(item?.Id, actor, now, device);
        return item;
    }

    public IReadOnlyList<PomodoroEvent> TickPomodoro(DateTimeOffset now)
    {
        var events = new List<PomodoroEvent>();
        while (Pomodoro.Tick(now) is { } evt)
        {
            events.Add(evt);
            if (evt.Kind == PomodoroEventKind.FocusCompleted)
            {
                // The session ended at its end time, whenever this runs (after sleep, say).
                StopFocusTimer(evt.At);
                if (evt.ItemId is { } id && Find(id) is { } item)
                {
                    Log(evt.At, id, ActivityKind.FocusCompleted, $"Focus session completed on \"{item.Title}\"", Actor.System);
                }
            }
        }

        return events;
    }

    // ---- Internals ----------------------------------------------------------------------

    internal void Attach(WorkItem item, WorkItem? parent)
    {
        item.Board = this;
        if (!_index.TryAdd(item.Id, item))
        {
            throw new InvalidDataException($"Duplicate task id {item.Id}.");
        }

        item.Parent = parent;
        (parent?.ChildList ?? RootList).Add(item);
    }

    internal void AddLoadedGroup(TaskGroup group) => GroupList.Add(group);

    internal void AddLoadedActivity(ActivityEntry entry) => ActivityList.Add(entry);

    internal void SeedDefaultGroups()
    {
        GroupList.Add(new TaskGroup(Guid.NewGuid(), "Work", "#3b82f6"));
        GroupList.Add(new TaskGroup(Guid.NewGuid(), "Personal", "#22c55e"));
    }

    /// <summary>The first unfinished earlier step that blocks this item or any of its ancestors.</summary>
    internal static WorkItem? FindBlockingStep(WorkItem item)
    {
        for (WorkItem? current = item; current is not null; current = current.Parent)
        {
            if (BlockingStep(current) is { } blocker)
            {
                return blocker;
            }
        }

        return null;
    }

    /// <summary>The earlier open sibling that must be finished first, when the parent is sequential.</summary>
    internal static WorkItem? BlockingStep(WorkItem item)
    {
        if (item.Parent is not { Sequential: true } parent)
        {
            return null;
        }

        var firstOpen = parent.ChildList.Find(c => !c.IsDone);
        return firstOpen is not null && firstOpen != item ? firstOpen : null;
    }

    private void AdvanceSequence(WorkItem completed, Actor actor, DateTimeOffset now)
    {
        if (completed.Parent is not { Sequential: true } parent)
        {
            return;
        }

        var next = parent.ChildList.Find(c => !c.IsDone);
        if (next is null)
        {
            parent.CompletedAt = now;
            Log(now, parent.Id, ActivityKind.Completed, $"Completed \"{parent.Title}\" (all steps done)", Actor.System);
            AdvanceSequence(parent, actor, now);
            return;
        }

        if (parent.StepDelay is { } delay)
        {
            var gate = now + delay;
            if (next.NextActionAt is null || next.NextActionAt < gate)
            {
                next.NextActionAt = gate;
                Log(now, next.Id, ActivityKind.Scheduled, $"Unlocks {gate:u} ({delay.TotalHours:0.#}h after previous step)", actor);
            }
        }
    }

    /// <summary>
    /// Rescheduling replaces earlier schedule reminders and silences reminders that are already due (even if
    /// delivered), so "Later" on a ringing reminder really moves the item to Waiting. Future manual reminders stay.
    /// </summary>
    private static void DismissPendingScheduleReminders(WorkItem item, DateTimeOffset now)
    {
        foreach (var reminder in item.ReminderList.Where(r => r.DismissedAt is null && ((r.Kind == ReminderKind.NextAction && r.NotifiedAt is null) || r.DueAt <= now)))
        {
            reminder.DismissedAt = now;
        }
    }

    private void Log(DateTimeOffset at, Guid itemId, ActivityKind kind, string summary, Actor actor) =>
        ActivityList.Add(new ActivityEntry(at, itemId, kind, summary, actor));

    private string ValidateGroup(string name, string? color, TaskGroup? except)
    {
        var clean = RequireText(name, nameof(name), 60, "A group name is required.");
        if (color is not null)
        {
            ValidateColor(color);
        }

        if (GroupList.Exists(g => g != except && string.Equals(g.Name, clean, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A group named \"{clean}\" already exists.", nameof(name));
        }

        return clean;
    }

    private static string DefaultReminderMessage(WorkItem item) => $"Time to act on: {item.Title}";

    private static void ValidateStepDelay(TimeSpan? delay)
    {
        if (delay is { } d && d < TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "Step delay must be at least one minute.");
        }
    }

    internal static string RequireText(string? value, string paramName, int maxLength, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, paramName);
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength
            ? throw new ArgumentException($"Must be at most {maxLength} characters.", paramName)
            : trimmed;
    }

    private static string? OptionalText(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null : RequireText(value, nameof(value), maxLength, string.Empty);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
