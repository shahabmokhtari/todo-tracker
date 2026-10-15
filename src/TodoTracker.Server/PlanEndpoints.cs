using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TodoTracker.Core;

namespace TodoTracker.Server;

/// <summary>
/// How the Plan view looks for a group: a day or a week, work hours or the whole day, and what work hours and work days
/// are (minutes after midnight; days 1 = Monday … 7 = Sunday).
/// </summary>
public sealed record PlanSettings(
    string View = "week",
    string Hours = "work",
    int WorkStart = 9 * 60,
    int WorkEnd = 17 * 60,
    IReadOnlyList<int>? WorkDays = null,
    int WeekStart = 1)
{
    public static readonly IReadOnlyList<int> DefaultWorkDays = [1, 2, 3, 4, 5];

    internal PlanSettings Validated()
    {
        if (View is not ("day" or "week"))
        {
            throw new ArgumentException("The plan shows a day or a week.", nameof(View));
        }

        if (Hours is not ("work" or "full"))
        {
            throw new ArgumentException("The plan shows work hours or the full day.", nameof(Hours));
        }

        if (WorkStart is < 0 or >= 24 * 60 || WorkEnd is <= 0 or > 24 * 60 || WorkEnd - WorkStart < 60)
        {
            throw new ArgumentException("Work hours are within a day and at least an hour long.", nameof(WorkStart));
        }

        var days = (WorkDays ?? DefaultWorkDays).Distinct().Order().ToList();
        if (days.Count == 0 || days.Exists(d => d is < 1 or > 7))
        {
            throw new ArgumentException("Work days are 1 (Monday) to 7 (Sunday).", nameof(WorkDays));
        }

        return WeekStart is 1 or 7 ? this with { WorkDays = days } : throw new ArgumentException("A week starts on Monday (1) or Sunday (7).", nameof(WeekStart));
    }
}

/// <summary>A block on the plan.</summary>
public sealed record PlanBlockDto(Guid Id, string Title, IReadOnlyList<string> Path, string? Icon, string? Accent, string Priority, DateTimeOffset Start, int Minutes, bool Done, Guid GroupId, string? Repeat);

/// <summary>A deadline that falls in the shown days (a marker on its day).</summary>
public sealed record PlanDeadlineDto(Guid Id, string Title, DateTimeOffset Deadline, bool Done);

/// <summary>Something to do that isn't planned yet (in Do now order), to drag onto the plan.</summary>
public sealed record PlanTaskDto(Guid Id, string Title, IReadOnlyList<string> Breadcrumb, string? Icon, string? Accent, string Priority, int Minutes);

public sealed record PlanDto(string From, int Days, PlanSettings Settings, IReadOnlyList<PlanBlockDto> Blocks, IReadOnlyList<PlanDeadlineDto> Deadlines, IReadOnlyList<PlanTaskDto> Unplanned);

public sealed record PlanRequest(DateTimeOffset? Start = null, int? Minutes = null);

internal static class PlanEndpoints
{
    private const int MaxDays = 31;

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiEndpoints.MapDomainErrors);

        // What's planned in the days shown (from a local date, in the user's time zone), deadlines in them, and the
        // tasks to do that aren't planned yet.
        api.MapGet("/plan", (string? from, int? days, Guid? group, IBoardStore store, TimeProvider time, TodoTrackerServerOptions options, SettingsStore settings) =>
        {
            var zone = options.TimeZone;
            var now = time.GetUtcNow();
            var first = from is null ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime)
                : DateOnly.TryParseExact(from, "yyyy-MM-dd", out var d) ? d : throw new ArgumentException("from is a date like 2026-01-05.", nameof(from));
            var count = Math.Clamp(days ?? 7, 1, MaxDays);
            var start = At(first, zone);
            var end = At(first.AddDays(count), zone);
            return store.ReadAsync(b =>
            {
                bool InGroup(WorkItem i) => group is null || i.GroupId == group;
                var items = b.AllItems().Where(i => !i.IsArchived && InGroup(i)).ToList();
                var blocks = items.Where(i => i.PlannedAt is { } at && at < end && at + i.PlannedLength > start)
                    .OrderBy(i => i.PlannedAt)
                    .Select(i => new PlanBlockDto(i.Id, i.Title, i.Path, i.Icon, b.AccentOf(i), Wire.Of(i.Priority), i.PlannedAt!.Value, (int)i.PlannedLength.TotalMinutes, i.IsDone, i.GroupId, i.Repeat?.ToString()))
                    .ToList();
                var deadlines = items.Where(i => i.Deadline is { } dl && dl >= start && dl < end)
                    .OrderBy(i => i.Deadline)
                    .Select(i => new PlanDeadlineDto(i.Id, i.Title, i.Deadline!.Value, i.IsDone))
                    .ToList();
                var unplanned = Agenda.Build(b, now, 0, group).Now.Where(e => e.Item.PlannedAt is null || e.Item.PlannedAt + e.Item.PlannedLength <= now)
                    .Select(e => new PlanTaskDto(e.Item.Id, e.Item.Title, e.Breadcrumb, e.Item.Icon, b.AccentOf(e.Item), Wire.Of(e.EffectivePriority), (int)e.Item.PlannedLength.TotalMinutes))
                    .ToList();
                return new PlanDto(first.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), count, settings.PlanFor(Key(group)), blocks, deadlines, unplanned);
            });
        });

        api.MapGet("/plan/settings", (Guid? group, SettingsStore settings) => settings.PlanFor(Key(group)));

        api.MapPut("/plan/settings", (Guid? group, PlanSettings request, SettingsStore settings) => settings.SetPlan(Key(group), request));

        // Plan a task (a start; minutes: how long, default what it had or 30), or take it off the plan (no start).
        api.MapPost("/items/{id:guid}/plan", (Guid id, PlanRequest request, HttpContext http, IBoardStore store, TimeProvider time, VaultLinks links) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                var actor = Security.ActorOf(http);
                if (request.Start is { } start)
                {
                    b.Plan(id, start, request.Minutes, actor, now);
                }
                else
                {
                    b.Unplan(id, actor, now);
                }

                return Wire.Item(b.Get(id), now, b, links);
            }));
    }

    private static string Key(Guid? group) => group?.ToString() ?? "all";

    private static DateTimeOffset At(DateOnly day, TimeZoneInfo zone)
    {
        var wall = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }
}
