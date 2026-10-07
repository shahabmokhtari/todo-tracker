namespace TodoTracker.Core;

/// <summary>One day in a report: time tracked (and how much of it was focus sessions), tasks finished and created.</summary>
public sealed record ReportDay(DateOnly Date, long TrackedSeconds, long FocusSeconds, int Completed, int Created);

public sealed record ReportGroup(Guid GroupId, string Name, string? Color, long TrackedSeconds);

public sealed record ReportTask(Guid Id, string Title, Guid GroupId, bool Done, long TrackedSeconds);

public sealed record ReportSpan(DateTimeOffset Start, DateTimeOffset End, TimeSource Source);

/// <summary>A task on the timeline: when it was created and finished (or is due), and when it was worked on.</summary>
public sealed record ReportRow(Guid Id, string Title, Guid GroupId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? Deadline, IReadOnlyList<ReportSpan> Work);

/// <summary>
/// Where the time went between two local dates (inclusive): per day, group, task, and weekday-hour, what got done,
/// and a timeline of the tasks worked on. Time is the tracked time entries (a running timer counts until now),
/// split at local midnights; archived tasks count too (the work happened).
/// </summary>
public sealed record TimeReport(
    DateOnly From,
    DateOnly To,
    long TrackedSeconds,
    int FocusSessions,
    int Completed,
    int Created,
    int ActiveDays,
    int LongestStreak,
    IReadOnlyList<ReportDay> Days,
    IReadOnlyList<ReportGroup> Groups,
    IReadOnlyList<ReportTask> Tasks,
    IReadOnlyList<IReadOnlyList<long>> HourGrid,
    IReadOnlyList<ReportRow> Timeline)
{
    public const int MaxDays = 732;
    public const int MaxTasks = 15;
    public const int MaxTimelineRows = 40;

    public static TimeReport Build(TaskBoard board, DateOnly from, DateOnly to, TimeZoneInfo tz, DateTimeOffset now, Guid? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(tz);
        if (to < from)
        {
            throw new ArgumentException("The end date must be on or after the start date.", nameof(to));
        }

        var dayCount = to.DayNumber - from.DayNumber + 1;
        if (dayCount > MaxDays)
        {
            throw new ArgumentException("A report can cover at most two years.", nameof(to));
        }

        var rangeStart = Local(from, tz);
        var rangeEnd = Local(to.AddDays(1), tz);
        var days = new (long Tracked, long Focus, int Completed, int Created)[dayCount];
        var grid = Enumerable.Range(0, 7).Select(_ => new long[24]).ToArray();
        var byGroup = new Dictionary<Guid, long>();
        var byTask = new Dictionary<Guid, long>();
        var rows = new List<ReportRow>();
        var roots = board.Items.Where(r => groupId is null || r.GroupId == groupId).ToList();

        int DayIndex(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime).DayNumber - from.DayNumber;
        bool InRange(DateTimeOffset at) => at >= rangeStart && at < rangeEnd;

        foreach (var root in roots)
        {
            var work = new List<ReportSpan>();
            foreach (var item in root.SelfAndDescendants())
            {
                if (item.CompletedAt is { } done && InRange(done))
                {
                    days[DayIndex(done)].Completed++;
                }

                if (InRange(item.CreatedAt))
                {
                    days[DayIndex(item.CreatedAt)].Created++;
                }

                foreach (var entry in item.TimeEntries)
                {
                    var start = entry.Start < rangeStart ? rangeStart : entry.Start;
                    var end = entry.End ?? now;
                    end = end > rangeEnd ? rangeEnd : end;
                    if (end <= start)
                    {
                        continue;
                    }

                    work.Add(new ReportSpan(start, end, entry.Source));
                    var ticks = (end - start).Ticks;
                    byGroup[root.GroupId] = byGroup.GetValueOrDefault(root.GroupId) + ticks;
                    byTask[root.Id] = byTask.GetValueOrDefault(root.Id) + ticks;
                    Spread(start, end, entry.Source, tz, days, grid, DayIndex);
                }
            }

            var touched = work.Count > 0 || (root.CompletedAt is { } c && InRange(c)) || InRange(root.CreatedAt);
            if (touched)
            {
                rows.Add(new ReportRow(root.Id, root.Title, root.GroupId, root.CreatedAt, root.CompletedAt, root.Deadline, [.. work.OrderBy(w => w.Start)]));
            }
        }

        // Summed in ticks, whole seconds at the end: the days add up to the total.
        static long Seconds(long ticks) => ticks / TimeSpan.TicksPerSecond;
        var reportDays = days.Select((d, i) => new ReportDay(from.AddDays(i), Seconds(d.Tracked), Seconds(d.Focus), d.Completed, d.Created)).ToList();
        var active = reportDays.Select(d => d.TrackedSeconds > 0 || d.Completed > 0).ToList();
        var streak = 0;
        var longest = 0;
        foreach (var on in active)
        {
            streak = on ? streak + 1 : 0;
            longest = Math.Max(longest, streak);
        }

        var focusSessions = board.Activity.Count(a => a.Kind == ActivityKind.FocusCompleted && InRange(a.At)
            && (groupId is null || board.Find(a.ItemId)?.GroupId == groupId));

        return new TimeReport(
            from,
            to,
            Seconds(byTask.Values.Sum()),
            focusSessions,
            reportDays.Sum(d => d.Completed),
            reportDays.Sum(d => d.Created),
            active.Count(a => a),
            longest,
            reportDays,
            [.. board.Groups.Where(g => byGroup.ContainsKey(g.Id)).Select(g => new ReportGroup(g.Id, g.Name, g.Color, Seconds(byGroup[g.Id]))).OrderByDescending(g => g.TrackedSeconds)],
            [.. byTask.OrderByDescending(t => t.Value).Take(MaxTasks).Select(t => board.Get(t.Key)).Select(r => new ReportTask(r.Id, r.Title, r.GroupId, r.IsDone, Seconds(byTask[r.Id])))],
            [.. grid.Select(h => (IReadOnlyList<long>)[.. h.Select(Seconds)])],
            [.. rows.OrderByDescending(r => r.Work.Sum(w => (w.End - w.Start).Ticks)).ThenByDescending(r => r.CompletedAt ?? r.CreatedAt).Take(MaxTimelineRows).OrderBy(r => r.Work.Count > 0 ? r.Work[0].Start : r.CompletedAt ?? r.CreatedAt)]);
    }

    private static DateTimeOffset Local(DateOnly date, TimeZoneInfo tz)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>Adds a stretch of time to the days and weekday-hours it covers (split at local hours).</summary>
    private static void Spread(DateTimeOffset start, DateTimeOffset end, TimeSource source, TimeZoneInfo tz, (long Tracked, long Focus, int Completed, int Created)[] days, long[][] grid, Func<DateTimeOffset, int> dayIndex)
    {
        var cursor = start;
        while (cursor < end)
        {
            var local = TimeZoneInfo.ConvertTime(cursor, tz);
            var nextHour = cursor.AddMinutes(60 - local.Minute).AddSeconds(-local.Second).AddMilliseconds(-local.Millisecond);
            var stop = nextHour < end ? nextHour : end;
            var ticks = (stop - cursor).Ticks;
            var index = dayIndex(cursor);
            if (index >= 0 && index < days.Length)
            {
                days[index].Tracked += ticks;
                if (source == TimeSource.Focus)
                {
                    days[index].Focus += ticks;
                }
            }

            grid[(int)local.DayOfWeek][local.Hour] += ticks;
            cursor = stop;
        }
    }
}
