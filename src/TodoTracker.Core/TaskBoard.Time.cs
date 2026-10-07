namespace TodoTracker.Core;

/// <summary>Time spent on tasks, where cards are on the board, and putting finished tasks away.</summary>
public sealed partial class TaskBoard
{
    /// <summary>The longest stretch that can be typed in by hand.</summary>
    public static readonly TimeSpan MaxTimeEntry = TimeSpan.FromHours(24);

    /// <summary>This computer, as time entries name it (timers it left running are its to end).</summary>
    public static string ThisDevice { get; } = Environment.MachineName;

    // ---- Time ----------------------------------------------------------------------------

    /// <summary>A timer running longer than this was forgotten (or its computer is gone): it counts this long at most.</summary>
    public static readonly TimeSpan ForgottenAfter = TimeSpan.FromHours(12);

    /// <summary>
    /// Ends every timer (any device) that has run longer than <see cref="ForgottenAfter"/>, at that length. Returns how
    /// many ended.
    /// </summary>
    public int CloseForgottenTimers(DateTimeOffset now)
    {
        var closed = 0;
        foreach (var entry in AllItems().SelectMany(i => i.TimeEntryList))
        {
            if (entry.IsRunning && now - entry.Start > ForgottenAfter)
            {
                entry.End = entry.Start + ForgottenAfter;
                closed++;
            }
        }

        return closed;
    }

    /// <summary>The timer that runs (the newest, if sync brought two together), or null. A forgotten one doesn't count.</summary>
    public (WorkItem Item, TimeEntry Entry)? RunningTimer(DateTimeOffset? now = null)
    {
        (WorkItem Item, TimeEntry Entry)? newest = null;
        foreach (var item in AllItems())
        {
            foreach (var entry in item.TimeEntryList)
            {
                if (entry.IsRunning && (now is null || now.Value - entry.Start <= ForgottenAfter) && (newest is null || entry.Start > newest.Value.Entry.Start))
                {
                    newest = (item, entry);
                }
            }
        }

        return newest;
    }

    /// <summary>
    /// Starts timing <paramref name="id"/> on this <paramref name="device"/>; any other timer stops (one runs at a
    /// time). Starting work on a card moves it to Doing.
    /// </summary>
    public TimeEntry StartTimer(Guid id, Actor actor, DateTimeOffset now, string? device = null)
    {
        var item = Get(id);
        if (item.IsDone)
        {
            throw new InvalidOperationException($"\"{item.Title}\" is already done.");
        }

        if (RunningTimer() is { } running && running.Item == item)
        {
            return running.Entry;
        }

        // A focus session on another task no longer counts for it (the session itself goes on).
        if (Pomodoro.Phase == PomodoroPhase.Focus && Pomodoro.ItemId is { } focused && focused != item.Id)
        {
            Pomodoro.DetachItem();
        }

        var entry = StartTimerCore(item, TimeSource.Manual, now, device);
        Log(now, item.Id, ActivityKind.TimeLogged, $"Started the timer on \"{item.Title}\"", actor);
        return entry;
    }

    /// <summary>Stops the timer (every running one, if sync brought two together); the newest is returned.</summary>
    public (WorkItem Item, TimeEntry Entry)? StopTimer(DateTimeOffset now)
    {
        var newest = RunningTimer();
        StopTimers(AllItems(), now);
        return newest;
    }

    /// <summary>Time typed in by hand (say, a meeting that ran without a timer).</summary>
    public TimeEntry AddTime(Guid id, DateTimeOffset start, DateTimeOffset end, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        ValidateSpan(start, end);
        var entry = new TimeEntry(Guid.NewGuid(), start, end, TimeSource.Manual, null);
        item.TimeEntryList.Add(entry);
        SortEntries(item);
        Log(now, item.Id, ActivityKind.TimeLogged, $"Logged {Minutes(end - start)} on \"{item.Title}\"", actor);
        return entry;
    }

    public void UpdateTime(Guid id, Guid entryId, DateTimeOffset start, DateTimeOffset end, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var entry = FindEntry(item, entryId);
        ValidateSpan(start, end);
        entry.Start = start;
        entry.End = end;
        SortEntries(item);
        Log(now, item.Id, ActivityKind.TimeLogged, $"Corrected time on \"{item.Title}\" to {Minutes(end - start)}", actor);
    }

    public void RemoveTime(Guid id, Guid entryId, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        var entry = FindEntry(item, entryId);
        item.TimeEntryList.Remove(entry);
        Log(now, item.Id, ActivityKind.TimeLogged, $"Removed {Minutes(entry.Duration(now))} from \"{item.Title}\"", actor);
    }

    /// <summary>
    /// Ends the timers this <paramref name="device"/> left running when its app stopped (or the computer slept) at the
    /// time it was last seen running; other devices' timers are theirs to end. Returns how many ended.
    /// </summary>
    public int CloseAbandonedTimers(string device, DateTimeOffset lastSeen)
    {
        var closed = 0;
        foreach (var entry in AllItems().SelectMany(i => i.TimeEntryList))
        {
            if (entry.IsRunning && entry.Device == device && entry.Start <= lastSeen)
            {
                entry.End = lastSeen;
                closed++;
            }
        }

        return closed;
    }

    internal static TimeEntry AddRunningEntryForTest(WorkItem item, DateTimeOffset start, string? device)
    {
        var entry = new TimeEntry(Guid.NewGuid(), start, null, TimeSource.Manual, device);
        item.TimeEntryList.Add(entry);
        return entry;
    }

    internal static void AddLoadedTimeEntry(WorkItem item, TimeEntry entry)
    {
        if (!item.TimeEntryList.Exists(e => e.Id == entry.Id))
        {
            item.TimeEntryList.Add(entry);
        }
    }

    internal static TimeEntry LoadedTimeEntry(Guid id, DateTimeOffset start, DateTimeOffset? end, TimeSource source, string? device) =>
        new(id, start, end, source, device);

    /// <summary>Ends the running timers among <paramref name="items"/>: each at the start of a newer one, the newest now.</summary>
    private static void StopTimers(IEnumerable<WorkItem> items, DateTimeOffset now)
    {
        var running = items.SelectMany(i => i.TimeEntryList).Where(e => e.IsRunning).OrderBy(e => e.Start).ToList();
        for (var i = 0; i < running.Count; i++)
        {
            var end = i < running.Count - 1 ? running[i + 1].Start : now;
            end = end < running[i].Start ? running[i].Start : end;

            // A forgotten timer ends at the most it counts, not hours (or days) later.
            running[i].End = end - running[i].Start > ForgottenAfter ? running[i].Start + ForgottenAfter : end;
        }
    }

    private void StopFocusTimer(DateTimeOffset at) =>
        StopTimers(AllItems().Where(i => i.TimeEntryList.Exists(e => e.IsRunning && e.Source == TimeSource.Focus)), at);

    private TimeEntry StartTimerCore(WorkItem item, TimeSource source, DateTimeOffset now, string? device)
    {
        StopTimers(AllItems(), now);
        var entry = new TimeEntry(Guid.NewGuid(), now, null, source, device ?? ThisDevice);
        item.TimeEntryList.Add(entry);
        if (item.Root is { IsDone: false, Stage: not Stage.Doing } root)
        {
            root.Stage = Stage.Doing;
        }

        return entry;
    }

    private static TimeEntry FindEntry(WorkItem item, Guid entryId) =>
        item.TimeEntryList.Find(e => e.Id == entryId) ?? throw new TaskNotFoundException($"Time entry {entryId} was not found.");

    private static void ValidateSpan(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new ArgumentException("The end must be after the start.", nameof(end));
        }

        if (end - start > MaxTimeEntry)
        {
            throw new ArgumentException("One entry can be at most 24 hours; add the days separately.", nameof(end));
        }
    }

    private static void SortEntries(WorkItem item) => item.TimeEntryList.Sort((a, b) => a.Start.CompareTo(b.Start));

    private static string Minutes(TimeSpan span) => span.TotalMinutes < 60
        ? $"{Math.Round(span.TotalMinutes)} min"
        : $"{Math.Floor(span.TotalHours)} h {span.Minutes:00} min";

    // ---- Board ---------------------------------------------------------------------------

    /// <summary>Moves a card to another column. A finished card moved back is open again.</summary>
    public void SetStage(Guid id, Stage stage, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (item.Parent is not null)
        {
            throw new InvalidOperationException("Only top-level tasks are cards; subtasks move with their task.");
        }

        if (item.IsDone)
        {
            Reopen(id, actor, now);
        }

        if (item.Stage == stage)
        {
            return;
        }

        item.Stage = stage;
        Log(now, id, ActivityKind.Moved, $"Moved \"{item.Title}\" to {stage}", actor);
    }

    // ---- Archive -------------------------------------------------------------------------

    /// <summary>Puts a finished top-level task away (out of every list; kept, and searchable with <c>is:archived</c>).</summary>
    public void Archive(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (item.Parent is not null)
        {
            throw new InvalidOperationException("Subtasks are archived with their task.");
        }

        if (!item.IsDone)
        {
            throw new InvalidOperationException($"Finish \"{item.Title}\" before archiving it.");
        }

        if (item.ArchivedAt is not null)
        {
            return;
        }

        item.ArchivedAt = now;
        Log(now, id, ActivityKind.Archived, $"Archived \"{item.Title}\"", actor);
    }

    public void Unarchive(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (item.ArchivedAt is null)
        {
            return;
        }

        item.ArchivedAt = null;
        Log(now, id, ActivityKind.Archived, $"Brought \"{item.Title}\" back from the archive", actor);
    }

    /// <summary>Archives every top-level task finished before <paramref name="doneBefore"/> (in a group, or all).</summary>
    public IReadOnlyList<Guid> ArchiveCompleted(DateTimeOffset doneBefore, Guid? groupId, Actor actor, DateTimeOffset now)
    {
        var archived = RootList
            .Where(r => r.CompletedAt is { } done && done < doneBefore && r.ArchivedAt is null && (groupId is null || r.GroupId == groupId))
            .Select(r => r.Id)
            .ToList();
        foreach (var id in archived)
        {
            Archive(id, actor, now);
        }

        return archived;
    }
}
