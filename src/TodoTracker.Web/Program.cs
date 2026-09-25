using System.Globalization;
using System.Text.Json.Serialization;
using TodoTracker.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
var app = builder.Build();
var store = new TaskBoardStore(SampleBoard.Create());

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/", () => Results.Content(HomePage.Render(store.Snapshot(DateTimeOffset.Now)), "text/html"));
app.MapGet("/api/agenda", () => store.Agenda(DateTimeOffset.Now));
app.MapGet("/api/waiting", () => store.Waiting(DateTimeOffset.Now));
app.MapGet("/api/tasks/{id:guid}", (Guid id) =>
{
    var task = store.Find(id);
    return task is null ? Results.NotFound() : Results.Ok(task);
});
app.MapPost("/api/tasks", (CreateTaskRequest? request) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Title))
    {
        return Results.BadRequest(new { error = "Task title is required." });
    }

    var created = store.AddTask(request.Title, request.Priority ?? TaskPriority.Normal);
    return Results.Created($"/api/tasks/{created.Id}", created);
});

app.Run();

internal static class SampleBoard
{
    public static TaskBoard Create()
    {
        var now = DateTimeOffset.Now;
        var board = new TaskBoard();
        var rollout = board.AddTask("Roll out feature X", TaskPriority.Critical);
        rollout.SetDetail("Validate staged rollout progress and schedule each follow-up only when ready.");
        rollout.AddNote("Feature A completed; feature B validation is due.", now.AddMinutes(-20));

        var featureB = rollout.AddSubtask("Feature B validation", TaskPriority.High);
        featureB.AddReminder(now.AddMinutes(-5), "Validate telemetry and schedule the next 24-hour rollout gate.", now);

        var nextGate = rollout.AddSubtask("Feature B next gate", TaskPriority.High);
        nextGate.ScheduleNextActionAfter(TimeSpan.FromHours(24), now);

        var notes = board.AddTask("Review recent task notes", TaskPriority.Normal);
        notes.AddNote("Open the full report when the sidebar summary is not enough.", now.AddMinutes(-10));

        return board;
    }
}

internal static class HomePage
{
    public static string Render(BoardSnapshot board)
    {
        var agenda = string.Join("", board.Agenda.Select(RenderTask));
        var waiting = string.Join("", board.Waiting.Select(RenderTask));

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>Todo Tracker</title>
                <style>
                    body { margin: 0; font-family: system-ui, sans-serif; color: #f8fafc; background: #101827; }
                    main { max-width: 920px; margin: 0 auto; padding: 2rem; }
                    section { margin-block: 1rem; padding: 1rem; border-radius: 1rem; background: #17213a; }
                    article { margin-block: .75rem; padding: 1rem; border-radius: .75rem; background: #1e293b; }
                    .Critical { border-left: .5rem solid #be185d; }
                    .High { border-left: .5rem solid #c2410c; }
                    .Normal { border-left: .5rem solid #2563eb; }
                    .Low { border-left: .5rem solid #475569; }
                    p { color: #cbd5e1; }
                </style>
            </head>
            <body>
                <main>
                    <h1>Todo Tracker</h1>
                    <p>ADHD-friendly shared-core web view: actionable work first, waiting work below.</p>
                    <section><h2>Do now</h2>{{agenda}}</section>
                    <section><h2>Waiting</h2>{{waiting}}</section>
                </main>
            </body>
            </html>
            """;
    }

    private static string RenderTask(TaskDto item)
    {
        var dueLabel = item.DueAt is { } dueAt
            ? dueAt.LocalDateTime.ToString("g", CultureInfo.InvariantCulture)
            : "No due time";
        return $$"""
            <article class="{{item.Priority}}">
                <h3>{{Escape(item.Title)}}</h3>
                <p>{{Escape(item.Detail ?? dueLabel)}}</p>
                <small>Priority: {{item.Priority}} · Status: {{item.Status}} · Next: {{dueLabel}}</small>
            </article>
            """;
    }

    private static string Escape(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }
}

internal sealed record TaskDto(
    Guid Id,
    string Title,
    string Priority,
    string Status,
    string? Detail,
    DateTimeOffset? NextActionAt,
    DateTimeOffset? DueAt)
{
    public static TaskDto From(WorkItem item)
    {
        return new TaskDto(
            item.Id,
            item.Title,
            item.Priority.ToString(),
            item.Status.ToString(),
            item.Detail,
            item.NextActionAt,
            item.EffectiveDueAt());
    }
}

internal sealed record CreateTaskRequest(string Title, TaskPriority? Priority);

internal sealed record BoardSnapshot(IReadOnlyList<TaskDto> Agenda, IReadOnlyList<TaskDto> Waiting);

internal sealed class TaskBoardStore
{
    private readonly object _lock = new();
    private readonly TaskBoard _board;

    public TaskBoardStore(TaskBoard board)
    {
        _board = board;
    }

    public IReadOnlyList<TaskDto> Agenda(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _board.Agenda(now).Select(TaskDto.From).ToList();
        }
    }

    public IReadOnlyList<TaskDto> Waiting(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _board.Waiting(now).Select(TaskDto.From).ToList();
        }
    }

    public TaskDto? Find(Guid id)
    {
        lock (_lock)
        {
            return _board.AllTasks()
                .Where(item => item.Id == id)
                .Select(TaskDto.From)
                .FirstOrDefault();
        }
    }

    public BoardSnapshot Snapshot(DateTimeOffset now)
    {
        lock (_lock)
        {
            return new BoardSnapshot(
                _board.Agenda(now).Select(TaskDto.From).ToList(),
                _board.Waiting(now).Select(TaskDto.From).ToList());
        }
    }

    public TaskDto AddTask(string title, TaskPriority priority)
    {
        lock (_lock)
        {
            return TaskDto.From(_board.AddTask(title, priority));
        }
    }

}
