namespace OneThing.Core;

/// <summary>A task as a home-screen widget shows it: its title and group, nothing else (no details, notes or tokens).</summary>
/// <param name="OpenSubtasks">It has open subtasks: the widget opens it instead of marking it done.</param>
public sealed record WidgetItem(Guid Id, string Title, string? Group, string? Color, bool OpenSubtasks);

/// <summary>A task coming back later today, and when.</summary>
public sealed record WidgetLater(Guid Id, DateTimeOffset At);

/// <summary>
/// What the widget shows from <see cref="At"/> until the next frame: the Do now list (the first
/// <see cref="WidgetSnapshot.MaxItems"/> of <see cref="NowCount"/>) and what comes back later today.
/// </summary>
public sealed record WidgetFrame(DateTimeOffset At, IReadOnlyList<Guid> Now, int NowCount, IReadOnlyList<WidgetLater> Later, int LaterCount);

/// <summary>What a widget shows at one moment.</summary>
/// <param name="Focus">The one thing to do now (null: nothing).</param>
/// <param name="Next">The next few after it.</param>
/// <param name="More">How many more today after <see cref="Focus"/> and <see cref="Next"/>: the rest to do now and what comes back later today.</param>
/// <param name="Today">The Do now list (as far as the snapshot keeps it).</param>
/// <param name="Later">What comes back later today, soonest first.</param>
public sealed record WidgetView(WidgetItem? Focus, IReadOnlyList<WidgetItem> Next, int More, IReadOnlyList<WidgetItem> Today, IReadOnlyList<(WidgetItem Item, DateTimeOffset At)> Later, int NowCount, int LaterCount)
{
    /// <summary>How many more today after showing the first <paramref name="shown"/> of the Do now list.</summary>
    public int MoreAfter(int shown) => Math.Max(0, NowCount - shown) + LaterCount;
}

/// <summary>
/// What the home-screen widgets (iPhone, iPad, Mac, Android) show, worked out ahead by the app with the agenda's own rules
/// (<see cref="Agenda.Build"/>) at every moment the list can change in the next day (a snooze ends, a reminder or a
/// planned block starts, a deadline passes, midnight), so the widget stays right with the app closed. The Swift and Kotlin
/// ports do the same; tests/fixtures/widget.json checks all three.
/// </summary>
public sealed record WidgetSnapshot(DateTimeOffset At, IReadOnlyList<WidgetItem> Items, IReadOnlyList<WidgetFrame> Frames)
{
    /// <summary>How far ahead frames are worked out.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromHours(24);

    /// <summary>At most this many frames (the first one is now).</summary>
    public const int MaxFrames = 24;

    /// <summary>At most this many tasks of each list in a frame.</summary>
    public const int MaxItems = 8;

    /// <summary>How many tasks after the focus a medium widget lists.</summary>
    public const int NextCount = 3;

    public static WidgetSnapshot Build(TaskBoard board, DateTimeOffset now, TimeZoneInfo zone)
    {
        var until = now + Horizon;
        var points = new SortedSet<DateTimeOffset>();
        void Add(DateTimeOffset? at)
        {
            if (at is { } t && t > now && t <= until)
            {
                points.Add(t);
            }
        }

        foreach (var item in board.AllItems().Where(i => !i.IsDone && !i.IsArchived))
        {
            Add(item.NextActionAt);
            Add(item.Deadline);
            Add(item.PlannedAt);
            Add(item.PlannedAt + item.PlannedLength);
            foreach (var reminder in item.Reminders.Where(r => r.DismissedAt is null))
            {
                Add(reminder.DueAt);
            }
        }

        for (var midnight = NextMidnight(now, zone); midnight <= until; midnight = NextMidnight(midnight, zone))
        {
            Add(midnight);
        }

        var frames = new List<WidgetFrame>();
        foreach (var at in new[] { now }.Concat(points.Take(MaxFrames - 1)))
        {
            var dashboard = Agenda.Build(board, at, recentNoteCount: 0);
            var endOfDay = NextMidnight(at, zone);
            var later = dashboard.Waiting.Where(e => e.WakeAt is { } w && w < endOfDay).ToList();
            var frame = new WidgetFrame(
                at,
                [.. dashboard.Now.Take(MaxItems).Select(e => e.Item.Id)],
                dashboard.Now.Count,
                [.. later.Take(MaxItems).Select(e => new WidgetLater(e.Item.Id, e.WakeAt!.Value))],
                later.Count);
            if (frames.Count == 0 || !SameList(frames[^1], frame))
            {
                frames.Add(frame);
            }
        }

        var items = new List<WidgetItem>();
        var seen = new HashSet<Guid>();
        foreach (var id in frames.SelectMany(f => f.Now.Concat(f.Later.Select(l => l.Id))))
        {
            if (seen.Add(id))
            {
                var item = board.Get(id);
                var group = board.Groups.FirstOrDefault(g => g.Id == item.GroupId);
                items.Add(new WidgetItem(id, item.Title, group?.Name, group?.Color, item.HasOpenChildren));
            }
        }

        return new WidgetSnapshot(now, items, frames);
    }

    /// <summary>What to show at <paramref name="at"/>, leaving out <paramref name="hidden"/> (marked done from the widget, not saved yet).</summary>
    public WidgetView ViewAt(DateTimeOffset at, IReadOnlySet<Guid>? hidden = null)
    {
        var frame = FrameAt(at);
        if (frame is null)
        {
            return new WidgetView(null, [], 0, [], [], 0, 0);
        }

        bool Shown(Guid id) => hidden is null || !hidden.Contains(id);
        var byId = Items.ToDictionary(i => i.Id);
        var today = frame.Now.Where(Shown).Select(id => byId[id]).ToList();
        var later = frame.Later.Where(l => Shown(l.Id)).Select(l => (byId[l.Id], l.At)).ToList();
        var nowCount = frame.NowCount - (frame.Now.Count - today.Count);
        var laterCount = frame.LaterCount - (frame.Later.Count - later.Count);
        var next = today.Skip(1).Take(NextCount).ToList();
        var view = new WidgetView(today.FirstOrDefault(), next, 0, today, later, nowCount, laterCount);
        return view with { More = view.MoreAfter(today.Count == 0 ? 0 : 1 + next.Count) };
    }

    /// <summary>The frame on at <paramref name="at"/>: the last one started by then (the first one before it starts).</summary>
    public WidgetFrame? FrameAt(DateTimeOffset at) => Frames.LastOrDefault(f => f.At <= at) ?? (Frames.Count > 0 ? Frames[0] : null);

    /// <summary>The start of the day after the one <paramref name="at"/> is in, in <paramref name="zone"/>.</summary>
    public static DateTimeOffset NextMidnight(DateTimeOffset at, TimeZoneInfo zone)
    {
        var next = TimeZoneInfo.ConvertTime(at, zone).Date.AddDays(1);
        return new DateTimeOffset(next, zone.GetUtcOffset(next));
    }

    private static bool SameList(WidgetFrame a, WidgetFrame b) =>
        a.NowCount == b.NowCount && a.LaterCount == b.LaterCount && a.Now.SequenceEqual(b.Now) && a.Later.SequenceEqual(b.Later);
}
