namespace TodoTracker.Core;

/// <summary>
/// What the Plan view shows for some days (from a local date, in the user's time zone): the blocks planned in them,
/// the deadlines on them, the tasks to do that aren't planned yet (in Do now order), the ones put off (Later), and what
/// each day's blocks take in energy. The same in every app (tests/fixtures/plan.json: Swift and Kotlin agree).
/// </summary>
public sealed record PlanDays(
    DateOnly From,
    int Days,
    IReadOnlyList<WorkItem> Blocks,
    IReadOnlyList<WorkItem> Deadlines,
    IReadOnlyList<AgendaEntry> Unplanned,
    IReadOnlyList<AgendaEntry> Later,
    IReadOnlyDictionary<DateOnly, double> Energy)
{
    public const int MaxDays = 31;

    public static PlanDays Build(TaskBoard board, DateOnly first, int days, TimeZoneInfo zone, DateTimeOffset now, Guid? groupId = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(zone);
        var count = Math.Clamp(days, 1, MaxDays);
        var start = Midnight(first, zone);
        var end = Midnight(first.AddDays(count), zone);
        var items = board.AllItems().Where(i => !i.IsArchived && (groupId is null || i.GroupId == groupId)).ToList();

        // Blocks that overlap the days (one crossing midnight shows on both), and deadlines in them.
        var blocks = items.Where(i => i.PlannedAt is { } at && at < end && at + i.PlannedLength > start).OrderBy(i => i.PlannedAt).ToList();
        var deadlines = items.Where(i => i.Deadline is { } dl && dl >= start && dl < end).OrderBy(i => i.Deadline).ToList();

        // Not planned: no block, or its block is over (planned again from here).
        var agenda = Agenda.Build(board, now, 0, groupId);
        bool NotPlanned(AgendaEntry e) => e.Item.PlannedAt is null || e.Item.PlannedAt + e.Item.PlannedLength <= now;
        var energy = Enumerable.Range(0, count).Select(first.AddDays).ToDictionary(d => d, d => TodoTracker.Core.Energy.PlannedOn(board, d, zone));
        return new PlanDays(first, count, blocks, deadlines, [.. agenda.Now.Where(NotPlanned)], [.. agenda.Waiting.Where(NotPlanned)], energy);
    }

    /// <summary>Local midnight of <paramref name="day"/> in <paramref name="zone"/>.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var wall = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}
