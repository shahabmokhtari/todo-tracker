using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace TodoTracker.Server;

/// <summary>
/// OpenAPI descriptions of the REST API for HTTP-based AI tools (GPT Actions, Copilot Studio, Power Platform):
/// <c>/openapi/v1.json</c> (OpenAPI 3.1) and <c>/openapi/swagger2.json</c> (Swagger 2.0, which Copilot Studio needs).
/// Only the curated operations below are described: reading and organizing tasks, nothing destructive or about sign-in.
/// Every <c>/api</c> route must be listed as described or hidden, so new endpoints get a deliberate decision.
/// </summary>
internal static partial class OpenApiSetup
{
    private static readonly Dictionary<string, (string Id, string Summary)> Described = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GET api/dashboard"] = ("getDashboard", "What to do now, what is waiting, and an overview per group"),
        ["GET api/search"] = ("searchTasks", "Find tasks (#tag, label:name, group:name, is:done)"),
        ["GET api/items"] = ("listTasks", "All tasks as a tree"),
        ["GET api/items/{id}"] = ("getTask", "One task with subtasks, notes, tags, labels and attachments"),
        ["POST api/items"] = ("createTask", "Create a task or a subtask"),
        ["POST api/capture"] = ("captureTask", "Add a task from one line (! priority, @2h defer, due:3d, #tag)"),
        ["PATCH api/items/{id}"] = ("updateTask", "Change title, details, priority, deadline, tags or labels"),
        ["POST api/items/{id}/steps"] = ("addSteps", "Add ordered steps under a task"),
        ["POST api/items/{id}/complete"] = ("completeTask", "Mark a task done"),
        ["POST api/items/{id}/reopen"] = ("reopenTask", "Undo a completion"),
        ["POST api/items/{id}/notes"] = ("addNote", "Log progress on a task"),
        ["PATCH api/items/{id}/notes/{noteId}"] = ("updateNote", "Edit a note"),
        ["POST api/items/{id}/schedule"] = ("scheduleNextAction", "Defer a task until later and remind the user"),
        ["POST api/items/{id}/reminders"] = ("addReminder", "Add a reminder"),
        ["POST api/items/{id}/reminders/{reminderId}/dismiss"] = ("dismissReminder", "Dismiss a reminder"),
        ["POST api/items/{id}/move"] = ("moveTask", "Move a task under another task, to the top level, or to another group"),
        ["POST api/items/{id}/reorder"] = ("reorderTask", "Put a task before one of its siblings (or last)"),
        ["POST api/now/order"] = ("arrangeNow", "Set the order of tasks in Do now (first = focus)"),
        ["POST api/now/first"] = ("putFirst", "Put tasks at the top of Do now, in this order"),
        ["GET api/items/{id}/timeline"] = ("getTaskTimeline", "Activity of one task, newest first"),
        ["GET api/timeline"] = ("getTimeline", "Activity of all tasks, newest first"),
        ["GET api/groups"] = ("listGroups", "Groups (tabs such as Work and Personal)"),
        ["POST api/groups"] = ("createGroup", "Add a group"),
        ["GET api/labels"] = ("listLabels", "The curated labels"),
        ["GET api/tags"] = ("listTags", "The tags in use on open tasks, most used first"),
        ["GET api/vault"] = ("getVaultInfo", "Where the markdown files live and the file format"),
        ["GET api/items/{id}/history"] = ("getTaskHistory", "Saved versions of a task, newest first"),
        ["GET api/items/{id}/history/{version}"] = ("getTaskVersion", "A task's markdown file as it was in a version"),
        ["POST api/items/{id}/history/{version}/restore"] = ("restoreTaskVersion", "Put a task back the way it was in a version"),
        ["GET api/items/{id}/rich"] = ("getRichHtml", "A task's rich HTML version"),
        ["PUT api/items/{id}/rich"] = ("setRichHtml", "Save a rich HTML version of a top-level task"),
        ["POST api/pomodoro/{action}"] = ("controlFocusTimer", "Start, pause, resume, skip or stop the focus timer"),
        ["GET api/tree"] = ("getBoard", "Top-level tasks with their board column (inbox, next, doing), progress, time spent and subtasks"),
        ["POST api/items/{id}/stage"] = ("moveCard", "Move a top-level task to a board column (inbox, next, doing)"),
        ["POST api/items/{id}/archive"] = ("archiveTask", "Put a finished top-level task away (out of every list)"),
        ["POST api/items/{id}/unarchive"] = ("unarchiveTask", "Bring an archived task back"),
        ["GET api/timer"] = ("getTimer", "What is being timed and for how long"),
        ["POST api/timer/start"] = ("startTimer", "Start timing a task (stops any other timer)"),
        ["POST api/timer/stop"] = ("stopTimer", "Stop the timer"),
        ["POST api/items/{id}/time"] = ("logTime", "Add time spent on a task (start and end)"),
        ["GET api/reports"] = ("getReport", "Time tracked per day, group and task, tasks done, and a timeline (from/to local dates)"),
    };

    // Sign-in plumbing, settings, deletes, binary uploads/downloads and the event stream stay out of the AI surface.
    private static readonly HashSet<string> Hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET api/health", "GET api/auth", "POST api/login", "GET api/connection", "POST api/launch", "GET api/events",
        "DELETE api/items/{id}", "PATCH api/groups/{id}", "DELETE api/groups/{id}", "GET api/export",
        "GET api/obsidian/vaults", "PUT api/settings/vault", "GET api/settings", "PUT api/settings", "PUT api/settings/theme",
        "POST api/labels", "PATCH api/labels/{name}", "DELETE api/labels/{name}",
        "GET api/history", "POST api/history/commit",
        "POST api/items/{id}/attachments", "GET api/items/{id}/attachments/{attachmentId}", "DELETE api/items/{id}/attachments/{attachmentId}", "GET api/items/{id}/embed/{name}",
        "POST api/archive", "PUT api/items/{id}/time/{entryId}", "DELETE api/items/{id}/time/{entryId}",
    };

    public static void AddServices(IServiceCollection services)
    {
        services.AddOpenApi("v1", o => Configure(o, OpenApiSpecVersion.OpenApi3_1));
        services.AddOpenApi("swagger2", o => Configure(o, OpenApiSpecVersion.OpenApi2_0));
    }

    public static void Map(WebApplication app) => app.MapOpenApi("/openapi/{documentName}.json");

    /// <summary><c>/api</c> routes that are neither described nor deliberately hidden.</summary>
    internal static List<string> UnclassifiedRoutes(IEndpointRouteBuilder app) =>
        app.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true && !IsPlugin(e.RoutePattern.RawText))
            .SelectMany(e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"]).Select(m => Key(m, e.RoutePattern.RawText)))
            .Where(k => !Described.ContainsKey(k) && !Hidden.Contains(k))
            .Distinct()
            .ToList();

    private static void Configure(OpenApiOptions options, OpenApiSpecVersion version)
    {
        options.OpenApiVersion = version;
        options.ShouldInclude = d => Described.ContainsKey(Key(d.HttpMethod, d.RelativePath));
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info = new OpenApiInfo
            {
                Title = "Todo Tracker",
                Version = McpInfo.Version,
                Description = "The user's ADHD-friendly task list: see what to do now, capture and organize tasks, log progress, and defer work. " +
                    "Send the API token from the Todo Tracker app as a bearer token.",
            };
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            // Swagger 2.0 has no bearer type: there it is an API key in the Authorization header ("Bearer <token>").
            document.Components.SecuritySchemes["bearer"] = version == OpenApiSpecVersion.OpenApi2_0
                ? new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Header,
                    Name = "Authorization",
                    Description = "\"Bearer \" followed by the API token from Todo Tracker (Connect an AI app).",
                }
                : new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    Description = "The API token from Todo Tracker (Connect an AI app).",
                };
            document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("bearer", document)] = [] }];
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, _) =>
        {
            var (id, summary) = Described[Key(context.Description.HttpMethod, context.Description.RelativePath)];
            operation.OperationId = id;
            operation.Summary = summary;
            return Task.CompletedTask;
        });
    }

    // Plugins (Ask AI, Teams…) are app features with their own UI, not part of the API described for AI tools.
    private static bool IsPlugin(string? route) => (route ?? string.Empty).TrimStart('/').StartsWith("api/plugins", StringComparison.OrdinalIgnoreCase);

    private static string Key(string? method, string? route) =>
        $"{method?.ToUpperInvariant()} {RouteConstraint().Replace(route ?? string.Empty, "{$1}").TrimStart('/')}";

    [GeneratedRegex(@"\{([^}:?=]+)[^}]*\}")]
    private static partial Regex RouteConstraint();
}
