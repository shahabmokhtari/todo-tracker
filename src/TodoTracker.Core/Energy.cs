namespace TodoTracker.Core;

/// <summary>
/// Energy: how much a task takes out of you per hour (1 to 3 ⚡; a task that doesn't say counts as 1), and how much you
/// have in a day (<see cref="TaskBoard.EnergyPerDay"/>). A day's energy is what its planned blocks take: each block's
/// hours on that day times its task's energy, done ones included (the work was done). Same rule in every core, checked
/// against tests/fixtures/energy.json.
/// </summary>
public static class Energy
{
    public const int Min = 1;
    public const int Max = 3;

    /// <summary>What a task that doesn't say takes per hour.</summary>
    public const int Unset = 1;

    /// <summary>A day's energy when it isn't set.</summary>
    public const int DefaultPerDay = 10;

    public const int MaxPerDay = 100;

    /// <summary>What a task takes per hour.</summary>
    public static int Of(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Energy ?? Unset;
    }

    /// <summary>An energy level: 1 to 3, or null.</summary>
    public static int? Clean(int? level) => level is null or (>= Min and <= Max)
        ? level
        : throw new ArgumentOutOfRangeException(nameof(level), $"Energy is {Min} to {Max} (⚡ to ⚡⚡⚡ an hour).");

    /// <summary>A level written by hand (or another app) that isn't one is left out.</summary>
    internal static int? Read(long? level) => level is >= Min and <= Max ? (int)level : null;

    /// <summary>What the blocks planned on <paramref name="day"/> (a local date in <paramref name="zone"/>) take, rounded to 0.01.</summary>
    public static double PlannedOn(TaskBoard board, DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(zone);
        var start = Midnight(day, zone);
        var end = Midnight(day.AddDays(1), zone);
        var total = 0.0;
        foreach (var item in board.AllItems())
        {
            if (item.IsArchived || item.PlannedAt is not { } at)
            {
                continue;
            }

            var from = at > start ? at : start;
            var to = at + item.PlannedLength < end ? at + item.PlannedLength : end;
            if (to > from)
            {
                total += (to - from).TotalHours * Of(item);
            }
        }

        return Math.Round(total, 2);
    }

    private static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo zone)
    {
        var wall = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}
