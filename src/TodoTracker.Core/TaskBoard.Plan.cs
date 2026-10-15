namespace TodoTracker.Core;

/// <summary>Planning: a block of time to work on a task (the Plan view's day and week grid).</summary>
public sealed partial class TaskBoard
{
    public const int DefaultPlanMinutes = 30;
    public const int MinPlanMinutes = 5;
    public const int MaxPlanMinutes = 12 * 60;

    /// <summary>Plans <paramref name="minutes"/> (default: what it had, else 30) from <paramref name="start"/> to work on a task.</summary>
    public void Plan(Guid id, DateTimeOffset start, int? minutes, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (item.IsDone)
        {
            throw new InvalidOperationException($"\"{item.Title}\" is already done.");
        }

        var length = minutes ?? item.PlannedMinutes ?? DefaultPlanMinutes;
        if (length is < MinPlanMinutes or > MaxPlanMinutes)
        {
            throw new ArgumentOutOfRangeException(nameof(minutes), $"A planned block is {MinPlanMinutes} minutes to {MaxPlanMinutes / 60} hours.");
        }

        item.PlannedAt = start;
        item.PlannedMinutes = length == DefaultPlanMinutes ? null : length;
        var local = TimeZoneInfo.ConvertTime(start, TimeZone);
        Log(now, id, ActivityKind.Scheduled, $"Planned \"{item.Title}\" {local.ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture)}, {length} min", actor);
    }

    public void Unplan(Guid id, Actor actor, DateTimeOffset now)
    {
        var item = Get(id);
        if (item.PlannedAt is null)
        {
            return;
        }

        item.PlannedAt = null;
        item.PlannedMinutes = null;
        Log(now, id, ActivityKind.Scheduled, $"Took \"{item.Title}\" off the plan", actor);
    }

    /// <summary>A planned length written by hand (or another app) that's out of range is left out.</summary>
    internal static int? ReadPlanMinutes(long? minutes) => minutes is >= MinPlanMinutes and <= MaxPlanMinutes && minutes != DefaultPlanMinutes ? (int)minutes : null;
}
