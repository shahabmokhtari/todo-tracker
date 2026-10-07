using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TodoTracker.Core;

namespace TodoTracker.Server;

public sealed record StageRequest(string? Stage);

/// <summary>Archive every finished top-level task done at least <c>OlderThanDays</c> ago (in a group, or all).</summary>
public sealed record ArchiveRequest(int OlderThanDays = 0, Guid? GroupId = null);

public sealed record ArchiveResult(IReadOnlyList<Guid> Archived);

public sealed record TimerRequest(Guid? ItemId);

public sealed record TimeEntryRequest(DateTimeOffset? Start, DateTimeOffset? End);

/// <summary>The board (columns), the archive, the timer, and reports.</summary>
internal static class BoardTimeEndpoints
{
    /// <summary>The longest report asked for without dates: the last four weeks.</summary>
    private const int DefaultReportDays = 28;

    public static Stage ParseStage(string? value) =>
        Enum.TryParse<Stage>(value?.Trim(), ignoreCase: true, out var stage) && Enum.IsDefined(stage) && !int.TryParse(value, out _)
            ? stage
            : throw new ArgumentException($"\"{value}\" isn't a column; use inbox, next or doing (finish a task to move it to done).", nameof(value));

    public static void Map(RouteGroupBuilder api)
    {
        // The board and the outline: top-level tasks of a group (or all), with their subtasks; archived ones on request.
        api.MapGet("/tree", (Guid? group, bool? archived, IBoardStore store, TimeProvider time) =>
            store.ReadAsync(b =>
            {
                if (group is { } g)
                {
                    b.GetGroup(g);
                }

                var now = time.GetUtcNow();
                var timing = b.RunningTimer(now)?.Item.Id;
                return b.Items
                    .Where(r => (group is null || r.GroupId == group) && (r.ArchivedAt is not null) == (archived == true))
                    .Select(r => Wire.TreeNode(r, now, b, timing))
                    .ToList();
            }));

        api.MapPost("/items/{id:guid}/stage", (Guid id, StageRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            ApiEndpoints.Mutate(store, time, http, (b, now, actor) =>
            {
                b.SetStage(id, ParseStage(request.Stage), actor, now);
                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/archive", (Guid id, HttpContext http, IBoardStore store, TimeProvider time) =>
            ApiEndpoints.Mutate(store, time, http, (b, now, actor) =>
            {
                b.Archive(id, actor, now);
                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/unarchive", (Guid id, HttpContext http, IBoardStore store, TimeProvider time) =>
            ApiEndpoints.Mutate(store, time, http, (b, now, actor) =>
            {
                b.Unarchive(id, actor, now);
                return b.Get(id);
            }));

        api.MapPost("/archive", (ArchiveRequest? request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                var days = Math.Max(0, request?.OlderThanDays ?? 0);
                return new ArchiveResult(b.ArchiveCompleted(now.AddDays(-days).AddTicks(1), request?.GroupId, Security.ActorOf(http), now));
            }));

        api.MapGet("/timer", (IBoardStore store, TimeProvider time) => store.ReadAsync(b => Wire.Timer(b, time.GetUtcNow())));

        api.MapPost("/timer/start", (TimerRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                b.StartTimer(request.ItemId ?? throw new ArgumentException("Which task?", nameof(request)), Security.ActorOf(http), now);
                return Wire.Timer(b, now);
            }));

        api.MapPost("/timer/stop", (IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                b.StopTimer(now);
                return Wire.Timer(b, now);
            }));

        api.MapPost("/items/{id:guid}/time", (Guid id, TimeEntryRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                var (start, end) = Span(request);
                return Wire.TimeEntry(b.Get(id), b.AddTime(id, start, end, Security.ActorOf(http), now), now);
            }));

        api.MapPut("/items/{id:guid}/time/{entryId:guid}", (Guid id, Guid entryId, TimeEntryRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                var (start, end) = Span(request);
                b.UpdateTime(id, entryId, start, end, Security.ActorOf(http), now);
                var item = b.Get(id);
                return Wire.TimeEntry(item, item.TimeEntries.First(e => e.Id == entryId), now);
            }));

        api.MapDelete("/items/{id:guid}/time/{entryId:guid}", async (Guid id, Guid entryId, HttpContext http, IBoardStore store, TimeProvider time) =>
        {
            await store.UpdateAsync(b => b.RemoveTime(id, entryId, Security.ActorOf(http), time.GetUtcNow())).ConfigureAwait(false);
            return Results.NoContent();
        });

        // Reports between two local dates (inclusive); without dates, the last four weeks up to today.
        api.MapGet("/reports", (DateOnly? from, DateOnly? to, Guid? group, IBoardStore store, TimeProvider time, TodoTrackerServerOptions options) =>
            store.ReadAsync(b =>
            {
                var now = time.GetUtcNow();
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, options.TimeZone).DateTime);
                var end = to ?? today;
                if (group is { } g)
                {
                    b.GetGroup(g);
                }

                return TimeReport.Build(b, from ?? end.AddDays(-(DefaultReportDays - 1)), end, options.TimeZone, now, group);
            }));
    }

    private static (DateTimeOffset Start, DateTimeOffset End) Span(TimeEntryRequest request) =>
        (request.Start ?? throw new ArgumentException("When did it start?", nameof(request)),
         request.End ?? throw new ArgumentException("When did it end?", nameof(request)));
}
