using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

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
    int? DeferMinutes = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Labels = null);

public sealed record PatchItemRequest(
    string? Title = null,
    string? Details = null,
    string? Priority = null,
    DateTimeOffset? Deadline = null,
    bool ClearDeadline = false,
    bool? Sequential = null,
    int? StepDelayMinutes = null,
    bool ClearStepDelay = false,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Labels = null);

public sealed record StepsRequest(IReadOnlyList<string>? Titles, int? StepDelayMinutes = null);

public sealed record NoteRequest(string? Text, string? SourceUrl = null, string? SourceTitle = null);

public sealed record ScheduleRequest(DateTimeOffset? At = null, int? InMinutes = null, bool Notify = true, string? Message = null, bool Clear = false);

public sealed record ReminderRequest(DateTimeOffset? At = null, int? InMinutes = null, string? Message = null);

public sealed record CaptureRequest(string? Text, Guid? GroupId = null, Guid? ParentId = null);

public sealed record GroupRequest(string? Name, string? Color = null);

/// <summary>Move to another group (top-level tasks), under <c>ParentId</c>, or back to the top level, at <c>Index</c>.</summary>
public sealed record MoveRequest(Guid? GroupId = null, Guid? ParentId = null, int? Index = null, bool ToTopLevel = false);

/// <summary>Task ids in the order wanted (first = top of Do now).</summary>
public sealed record OrderRequest(IReadOnlyList<Guid>? Ids);

/// <summary>Put the task before this sibling (null: last).</summary>
public sealed record ReorderRequest(Guid? Before = null);

public sealed record LabelRequest(string? Name, string? Color = null);

public sealed record RichRequest(string? Html);

public sealed record RichDto(bool HasRich);

public sealed record VaultDto(string Path, IReadOnlyList<VaultProblem> Problems, string Guide, string ObsidianUrl);

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

        api.MapGet("/dashboard", (Guid? group, int? notes, string? q, IBoardStore store, TimeProvider time, VaultLinks links) =>
            store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), group, notes ?? 5, links, q)));

        api.MapGet("/search", (string? q, IBoardStore store, TimeProvider time, VaultLinks links) =>
            store.ReadAsync(b => Search(b, q, time.GetUtcNow(), links)));

        api.MapGet("/items", (bool? includeDone, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.ReadAsync(b => b.Items.Where(i => includeDone == true || !i.IsDone).Select(i => Wire.Item(i, time.GetUtcNow(), b, http.RequestServices.GetRequiredService<VaultLinks>())).ToList()));

        api.MapGet("/items/{id:guid}", (Guid id, IBoardStore store, TimeProvider time, VaultLinks links) =>
            store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow(), b, links)));

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
                        Tags = request.Tags,
                        Labels = request.Labels,
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
                var created = b.AddTask(new NewTask(capture.Title) { ParentId = request.ParentId, GroupId = request.GroupId, Priority = capture.Priority, Deadline = capture.Deadline, Tags = capture.Tags }, actor, now);
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
                if (request.Tags is { } tags)
                {
                    b.SetTags(id, tags, actor, now);
                }

                if (request.Labels is { } labels)
                {
                    b.SetLabels(id, labels, actor, now);
                }

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
                return b.AddSteps(id, request.Titles ?? [], Minutes(request.StepDelayMinutes), Security.ActorOf(http), now).Select(s => Wire.Item(s, now, b)).ToList();
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

        api.MapPatch("/items/{id:guid}/notes/{noteId:guid}", (Guid id, Guid noteId, NoteRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var note = b.UpdateNote(id, noteId, request.Text ?? string.Empty, Security.ActorOf(http), time.GetUtcNow());
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

        // Manual order: the Do now list as someone arranged it (drag, arrows), "put first", and sibling order.
        api.MapPost("/now/order", async (OrderRequest request, IBoardStore store) =>
        {
            await store.UpdateAsync(b =>
            {
                b.ArrangeNow(request.Ids ?? []);
                return true;
            }).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapPost("/now/first", async (OrderRequest request, IBoardStore store, TimeProvider time) =>
        {
            await store.UpdateAsync(b =>
            {
                b.PutFirst(request.Ids ?? [], time.GetUtcNow());
                return true;
            }).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapPost("/items/{id:guid}/reorder", (Guid id, ReorderRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, _, _) =>
            {
                b.Reorder(id, request.Before);
                return b.Get(id);
            }));

        api.MapPost("/items/{id:guid}/move", (Guid id, MoveRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            Mutate(store, time, http, (b, now, actor) =>
            {
                if (request.ParentId is { } parent)
                {
                    b.Move(id, parent, request.Index, null, actor, now);
                }
                else if (request.ToTopLevel)
                {
                    b.Move(id, null, request.Index, request.GroupId, actor, now);
                }
                else if (request.GroupId is { } group)
                {
                    b.MoveToGroup(id, group, actor, now);
                }
                else
                {
                    throw new ArgumentException("Say where to move the task: groupId, parentId, or toTopLevel.", nameof(request));
                }

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

        MapLabels(api);
        MapFiles(api);
        MapHistory(api);

        api.MapGet("/vault", async (VaultLinks links) =>
        {
            await links.Vault.ReadAsync(_ => 0).ConfigureAwait(false);
            return new VaultDto(links.Vault.RootPath, links.Vault.Problems, VaultBoardStore.Guide, ObsidianVaults.OpenUrl(links.Vault.RootPath));
        });

        api.MapGet("/obsidian/vaults", () => ObsidianVaults.Discover());

        api.MapPut("/settings/vault", (VaultSettingRequest request, SettingsStore settings, TodoTrackerServerOptions options) =>
        {
            var saved = settings.SetVaultPath(request.Path);
            return new VaultSettingDto(options.ResolveVaultPath(saved.VaultPath), RestartRequired: true);
        });

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

    /// <summary>Applies the change, then reads the task back (so file links reflect where it was saved).</summary>
    private static async Task<ItemDto> Mutate(IBoardStore store, TimeProvider time, HttpContext http, Func<TaskBoard, DateTimeOffset, Actor, WorkItem> mutate)
    {
        var id = await store.UpdateAsync(b => mutate(b, time.GetUtcNow(), Security.ActorOf(http)).Id).ConfigureAwait(false);
        var links = http.RequestServices.GetRequiredService<VaultLinks>();
        return await store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow(), b, links)).ConfigureAwait(false);
    }

    /// <summary>Open tasks matching <paramref name="query"/> (add <c>is:done</c> for finished ones).</summary>
    internal static List<SearchHitDto> Search(TaskBoard board, string? query, DateTimeOffset now, VaultLinks? links)
    {
        var parsed = TaskQuery.Parse(query);
        return parsed.Apply(board).Where(i => parsed.HasState || !i.IsDone).Take(200).Select(i => Wire.SearchHit(i, now, board, links)).ToList();
    }

    private static void MapLabels(RouteGroupBuilder api)
    {
        api.MapGet("/labels", (IBoardStore store) => store.ReadAsync(Wire.Labels));

        api.MapPost("/labels", (LabelRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var label = b.DefineLabel(request.Name ?? string.Empty, request.Color, Security.ActorOf(http), time.GetUtcNow());
                return new LabelDto(label.Name, label.Color);
            }));

        api.MapPatch("/labels/{name}", (string name, LabelRequest request, HttpContext http, IBoardStore store, TimeProvider time) =>
            store.UpdateAsync(b =>
            {
                var current = b.FindLabel(name) ?? throw new TaskNotFoundException($"Label \"{name}\" was not found.");
                b.UpdateLabel(name, request.Name ?? current.Name, request.Color ?? current.Color, Security.ActorOf(http), time.GetUtcNow());
                var label = b.FindLabel(request.Name ?? current.Name)!;
                return new LabelDto(label.Name, label.Color);
            }));

        api.MapDelete("/labels/{name}", async (string name, HttpContext http, IBoardStore store, TimeProvider time) =>
        {
            await store.UpdateAsync(b => b.DeleteLabel(name, Security.ActorOf(http), time.GetUtcNow())).ConfigureAwait(false);
            return Results.NoContent();
        });
    }

    private static void MapHistory(RouteGroupBuilder api)
    {
        api.MapGet("/history", async (HistoryService history) =>
            history.History is { } versions ? await versions.LogAsync(null).ConfigureAwait(false) : []);

        api.MapPost("/history/commit", async (HistoryService history) =>
            new { committed = await history.Required.CommitAsync().ConfigureAwait(false) });

        api.MapGet("/items/{id:guid}/history", async (Guid id, HistoryService history, IBoardStore store) =>
        {
            await store.ReadAsync(b => b.Get(id)).ConfigureAwait(false);
            return history.History is { } versions ? await versions.TaskHistoryAsync(id).ConfigureAwait(false) : [];
        });

        api.MapGet("/items/{id:guid}/history/{version}", async (Guid id, string version, HistoryService history) =>
            await history.Required.TaskVersionAsync(id, version).ConfigureAwait(false) is { } text
                ? Results.Text(text, "text/markdown; charset=utf-8")
                : Results.Problem("This task didn't exist in that version.", statusCode: StatusCodes.Status404NotFound));

        api.MapPost("/items/{id:guid}/history/{version}/restore", async (Guid id, string version, HttpContext http, HistoryService history, IBoardStore store, TimeProvider time, VaultLinks links) =>
        {
            await history.Required.RestoreTaskAsync(id, version, Security.ActorOf(http)).ConfigureAwait(false);
            return await store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow(), b, links)).ConfigureAwait(false);
        });
    }

    private static readonly HashSet<string> InlineImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "image/avif",
    };

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static void MapFiles(RouteGroupBuilder api)
    {
        api.MapPost("/items/{id:guid}/attachments", async (Guid id, HttpContext http, VaultLinks links) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted).ConfigureAwait(false);
            var file = form.Files.GetFile("file") ?? (form.Files.Count > 0 ? form.Files[0] : null) ?? throw new ArgumentException("Choose a file to attach.", nameof(http));
            await using var stream = file.OpenReadStream();
            var attachment = await links.Vault.AddAttachmentAsync(id, Path.GetFileName(file.FileName), stream, Security.ActorOf(http), http.RequestAborted).ConfigureAwait(false);
            var item = await links.Vault.ReadAsync(b => b.Get(id)).ConfigureAwait(false);
            return Results.Created(VaultLinks.AttachmentUrl(id, attachment.Id), Wire.Attachment(item, attachment));
        }).DisableAntiforgery();

        api.MapGet("/items/{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, HttpContext http, VaultLinks links) =>
        {
            var (full, name) = await links.Vault.GetAttachmentFileAsync(id, attachmentId, http.RequestAborted).ConfigureAwait(false);
            if (!File.Exists(full))
            {
                return Results.Problem($"\"{name}\" is missing from the vault.", statusCode: StatusCodes.Status404NotFound);
            }

            // Raster images open in the browser; everything else (HTML, SVG, scripts, documents) downloads.
            var type = ContentTypes.TryGetContentType(name, out var t) ? t : "application/octet-stream";
            var inline = InlineImageTypes.Contains(type);
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'";
            if (inline)
            {
                http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileNameStar = name }.ToString();
                return Results.File(full, type, enableRangeProcessing: true);
            }

            return Results.File(full, "application/octet-stream", fileDownloadName: name, enableRangeProcessing: true);
        });

        api.MapDelete("/items/{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, HttpContext http, VaultLinks links) =>
        {
            await links.Vault.RemoveAttachmentAsync(id, attachmentId, Security.ActorOf(http), http.RequestAborted).ConfigureAwait(false);
            return Results.NoContent();
        });

        api.MapGet("/items/{id:guid}/rich", async (Guid id, HttpContext http, VaultLinks links) =>
        {
            var html = await links.Vault.ReadRichAsync(id, http.RequestAborted).ConfigureAwait(false);
            if (html is null)
            {
                return Results.Problem("This task has no rich version.", statusCode: StatusCodes.Status404NotFound);
            }

            // Untrusted HTML (from files or agents): no scripts, an opaque origin, and only framed by this app.
            http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; script-src 'none'; img-src data: https:; style-src 'unsafe-inline' https:; font-src data: https:; frame-ancestors 'self'";
            http.Response.Headers.XFrameOptions = "SAMEORIGIN";
            return Results.Content(html, "text/html; charset=utf-8");
        });

        api.MapPut("/items/{id:guid}/rich", async (Guid id, RichRequest request, HttpContext http, VaultLinks links) =>
        {
            var html = string.IsNullOrWhiteSpace(request.Html) ? null : request.Html;
            await links.Vault.WriteRichAsync(id, html, Security.ActorOf(http), http.RequestAborted).ConfigureAwait(false);
            return new RichDto(html is not null);
        });
    }

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

    internal static async ValueTask<object?> MapDomainErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (TaskNotFoundException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status403Forbidden);
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

