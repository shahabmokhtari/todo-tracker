using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TodoTracker.Core;

namespace TodoTracker.Server;

public sealed record CreateItemRequest(
    string? Title,
    Guid? ParentId = null,
    Guid? GroupId = null,
    string? Priority = null,
    string? Details = null,
    DateTimeOffset? Deadline = null,
    bool Sequential = false,
    int? StepDelayMinutes = null,
    int? DeferMinutes = null);

public sealed record PatchItemRequest(
    string? Title = null,
    string? Details = null,
    string? Priority = null,
    DateTimeOffset? Deadline = null,
    bool ClearDeadline = false,
    bool? Sequential = null,
    int? StepDelayMinutes = null,
    bool ClearStepDelay = false);

public sealed record StepsRequest(IReadOnlyList<string>? Titles, int? StepDelayMinutes = null);

public sealed record NoteRequest(string? Text, string? SourceUrl = null, string? SourceTitle = null);

public sealed record ScheduleRequest(DateTimeOffset? At = null, int? InMinutes = null, bool Notify = true, string? Message = null, bool Clear = false);

public sealed record ReminderRequest(DateTimeOffset? At = null, int? InMinutes = null, string? Message = null);

public sealed record CaptureRequest(string? Text, Guid? GroupId = null, Guid? ParentId = null);

public sealed record GroupRequest(string? Name, string? Color = null);

public sealed record MoveRequest(Guid GroupId);

public sealed record FocusRequest(Guid? ItemId = null);

public sealed record SettingsRequest(string? TeamsWebhookUrl);

public sealed record LoginRequest(string? Token);

public sealed record ConnectionDto(string BaseUrl, string McpUrl, string Token, object McpConfig);

public sealed record LaunchRequest(string? Return);

public sealed record LaunchDto(string Url);

internal static class ApiEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        app.MapGet("/auth", (string? code, string? @return, HttpResponse response, ApiToken apiToken, LaunchCodes launchCodes) =>
        {
            // A used or expired code grants nothing but still lands on the page: an existing session keeps working,
            // otherwise the sign-in screen appears (better than a bare 401 after a double click or slow browser start).
            if (launchCodes.TryRedeem(code))
            {
                Security.SetSessionCookie(response, apiToken);
            }

            return Results.Redirect(SafeReturnPath(@return));
        });

        var api = app.MapGroup("/api").AddEndpointFilter(MapDomainErrors);

        api.MapPost("/login", (LoginRequest request, HttpResponse response, ApiToken apiToken) =>
        {
            if (!apiToken.Matches(request.Token?.Trim()))
            {
                return Results.Unauthorized();
            }

            Security.SetSessionCookie(response, apiToken);
            return Results.NoContent();
        });

        api.MapGet("/connection", (ApiToken token, TodoTrackerServerOptions options) => Connection(token, options));

        api.MapPost("/launch", (LaunchRequest? request, LaunchCodes codes, TodoTrackerServerOptions options) =>
            new LaunchDto(LaunchUrl(codes, options, request?.Return, null)));

        api.MapGet("/dashboard", (Guid? group, int? notes, IBoardStore store, TimeProvider time) =>
            store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), group, notes ?? 5)));

        api.MapGet("/items", (bool? includeDone, IBoardStore store, TimeProvider time) =>
            store.ReadAsync(b => b.Items.Where(i => includeDone == true || !i.IsDone).Select(i => Wire.Item(i, time.GetUtcNow())).ToList()));

        api.MapGet("/items/{id:guid}", (Guid id, IBoardStore store, TimeProvider time) =>
            store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow())));

        api.MapPost("/items", async (CreateItemRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
        {
            var item = await Mutate(store, time, http, (b, now, actor) =>
            {
                var created = b.AddTask(
                    new NewTask(request.Title ?? string.Empty)
                    {
                        ParentId = request.ParentId,
                        GroupId = request.GroupId,
                        Priority = Wire.ParsePriority(request.Priority),
                        Details = request.Details,
                        Deadline = request.Deadline,
                        Sequential = request.Sequential,
                        StepDelay = Minutes(request.StepDelayMinutes),
                    },
                    actor,
                    now);
                if (request.DeferMinutes is > 0 and var m)
                {
                    b.ScheduleNextAction(created.Id, now.AddMinutes(m), actor, now, notify: true);
                }

                return created;
            }).ConfigureAwait(false);
            return Results.Created($"/api/items/{item.Id}", item);
        });

        api.MapPost("/capture", async (CaptureRequest request, HttpContext http, IBoardStore store, TimeProvider time, TodoTrackerServerOptions options) =>
        {
            var item = await Mutate(store, time, http, (b, now, actor) =>
            {
                var capture = QuickCaptureParser.Parse(request.Text ?? string.Empty, now, options.TimeZone);
                var created = b.AddTask(new NewTask(capture.Title) { ParentId = request.ParentId, GroupId = request.GroupId, Priority = capture.Priority, Deadline = capture.Deadline }, actor, now);
                if (capture.NextActionAt is { } at)
                {
                    b.ScheduleNextAction(created.Id, at, actor, now, notify: true);
                }

                return created;
            }).ConfigureAwait(false);
            return Results.Created($"/api/items/{item.Id}", item);
        });

        api.MapPatch("/items/{id:guid}", (Guid id, PatchItemRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                b.Update(
                    id,
                    new TaskChanges
                    {
                        Title = request.Title,
                        Details = request.Details,
                        Priority = request.Priority is null ? null : Wire.ParsePriority(request.Priority),
                        Deadline = request.Deadline,
                        ClearDeadline = request.ClearDeadline,
                        Sequential = request.Sequential,
                        StepDelay = Minutes(request.StepDelayMinutes),
                        ClearStepDelay = request.ClearStepDelay,
                    },
                    actor,
                    now);
                return b.Get(id);
            }));

        api.MapDelete("/items/{id:guid}", async (Guid id, HttpContext http, IBoardStore store, TimeProvider time) =>
        {
            await store.UpdateAsync(b => b.Delete(id, Security.ActorOf(http), time.GetUtcNow())).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapPost("/items/{id:guid}/steps", (Guid id, StepsRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                return b.AddSteps(id, request.Titles ?? [], Minutes(request.StepDelayMinutes), Security.ActorOf(http), now).Select(s => Wire.Item(s, now)).ToList();
            }));

        api.MapPost("/items/{id:guid}/complete", (Guid id, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                b.Complete(id, actor, now);
                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/reopen", (Guid id, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                b.Reopen(id, actor, now);
                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/notes", (Guid id, NoteRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var note = b.AddNote(id, request.Text ?? string.Empty, Security.ActorOf(http), time.GetUtcNow(), NullIfBlank(request.SourceUrl), request.SourceTitle);
                return Wire.Note(b.Get(id), note);
            }));

        api.MapPost("/items/{id:guid}/schedule", (Guid id, ScheduleRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                if (request.Clear)
                {
                    b.ClearNextAction(id, actor, now);
                }
                else
                {
                    b.ScheduleNextAction(id, ResolveTime(request.At, request.InMinutes, now), actor, now, request.Notify, request.Message);
                }

                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/reminders", (Guid id, ReminderRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                var r = b.AddReminder(id, ResolveTime(request.At, request.InMinutes, now), request.Message, Security.ActorOf(http), now);
                return new ReminderDto(r.Id, r.DueAt, r.Message, Wire.Of(r.Kind), r.NotifiedAt, r.DismissedAt);
            }));

        api.MapPost("/items/{id:guid}/reminders/{reminderId:guid}/dismiss", (Guid id, Guid reminderId, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                b.DismissReminder(id, reminderId, actor, now);
                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/move", (Guid id, MoveRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                b.MoveToGroup(id, request.GroupId, actor, now);
                return b.Get(id);
            }));

        api.MapGet("/items/{id:guid}/timeline", (Guid id, int? limit, IBoardStore store) =>
            store.ReadAsync(b => Wire.Timeline(b, id, limit ?? 500)));

        api.MapGet("/timeline", (int? limit, IBoardStore store) =>
            store.ReadAsync(b => Wire.Timeline(b, null, limit ?? 200)));

        api.MapGet("/groups", (IBoardStore store, TimeProvider time) =>
            store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), null, 0).Groups));

        api.MapPost("/groups", (GroupRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var g = b.AddGroup(request.Name ?? string.Empty, NullIfBlank(request.Color), Security.ActorOf(http), time.GetUtcNow());
                return new GroupDto(g.Id, g.Name, g.Color, 0, 0, 0);
            }));

        api.MapPatch("/groups/{id:guid}", (Guid id, GroupRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                b.UpdateGroup(id, request.Name ?? string.Empty, NullIfBlank(request.Color), Security.ActorOf(http), time.GetUtcNow());
                var g = b.GetGroup(id);
                return new GroupDto(g.Id, g.Name, g.Color, 0, 0, 0);
            }));

        api.MapDelete("/groups/{id:guid}", async (Guid id, Guid moveTo, HttpContext http, IBoardStore store, TimeProvider time) =>
        {
            await store.UpdateAsync(b => b.DeleteGroup(id, moveTo, Security.ActorOf(http), time.GetUtcNow())).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapPost("/pomodoro/{action}", (string action, FocusRequest? request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var now = time.GetUtcNow();
                switch (action.ToLowerInvariant())
                {
                    case "start":
                        b.StartFocus(request?.ItemId, Security.ActorOf(http), now);
                        break;
                    case "pause":
                        b.Pomodoro.Pause(now);
                        break;
                    case "resume":
                        b.Pomodoro.Resume(now);
                        break;
                    case "skip":
                        b.Pomodoro.Skip(now);
                        break;
                    case "reset":
                        b.Pomodoro.Reset();
                        break;
                    default:
                        throw new ArgumentException($"Unknown pomodoro action \"{action}\".", nameof(action));
                }

                return Wire.Pomodoro(b, now);
            }));

        api.MapGet("/export", async (IBoardStore store) =>
            Results.Text(await store.ReadAsync(BoardSerializer.Serialize).ConfigureAwait(false), "application/json"));

        api.MapGet("/settings", (SettingsStore settings) => settings.ToDto());

        api.MapPut("/settings", (SettingsRequest request, SettingsStore settings) =>
        {
            settings.SetTeamsWebhook(request.TeamsWebhookUrl);
            return settings.ToDto();
        });
    }

    internal static ConnectionDto Connection(ApiToken token, TodoTrackerServerOptions options)
    {
        var baseUrl = options.BaseUrl;
        var mcpUrl = baseUrl + "/mcp";
        var config = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                ["todo-tracker"] = new Dictionary<string, object>
                {
                    ["type"] = "http",
                    ["url"] = mcpUrl,
                    ["headers"] = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.Value },
                    ["tools"] = new[] { "*" },
                },
            },
        };
        return new ConnectionDto(baseUrl, mcpUrl, token.Value, config);
    }

    internal static string LaunchUrl(LaunchCodes codes, TodoTrackerServerOptions options, string? returnPath, TimeSpan? lifetime) =>
        $"{options.BaseUrl}/auth?code={Uri.EscapeDataString(codes.Create(lifetime))}&return={Uri.EscapeDataString(SafeReturnPath(returnPath))}";

    /// <summary>Only same-origin absolute paths, so a launch link can never become an open redirect.</summary>
    internal static string SafeReturnPath(string? path) =>
        path is { Length: > 0 } p
        && p[0] == '/'
        && !p.StartsWith("//", StringComparison.Ordinal)
        && !p.Contains('\\', StringComparison.Ordinal)
        && !p.Any(char.IsControl)
            ? p
            : "/";

    private static Task<ItemDto> Mutate(IBoardStore store, TimeProvider time, HttpContext http, Func<TaskBoard, DateTimeOffset, Actor, WorkItem> mutate) =>
        store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            return Wire.Item(mutate(b, now, Security.ActorOf(http)), now);
        });

    internal static DateTimeOffset ResolveTime(DateTimeOffset? at, int? inMinutes, DateTimeOffset now)
    {
        if (at is { } exact)
        {
            return exact;
        }

        if (inMinutes is { } minutes)
        {
            return minutes is >= 0 and <= 525_600 ? now.AddMinutes(minutes) : throw new ArgumentException("inMinutes must be between 0 and 525600.", nameof(inMinutes));
        }

        throw new ArgumentException("Provide either 'at' or 'inMinutes'.", nameof(at));
    }

    internal static TimeSpan? Minutes(int? minutes) => minutes is { } m ? TimeSpan.FromMinutes(m) : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async ValueTask<object?> MapDomainErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (TaskNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Results.Problem(ErrorText.Friendly(ex), statusCode: StatusCodes.Status400BadRequest);
        }
    }
}

