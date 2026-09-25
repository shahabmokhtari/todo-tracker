using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TodoTracker.Core;

namespace TodoTracker.Server;

/// <summary>
/// MCP tool surface for Copilot and other agents. Every change is attributed to the calling agent and shows up in the
/// sidebar and timeline. There is intentionally no delete tool: agents can complete/reopen but not destroy data.
/// </summary>
[McpServerToolType]
public sealed class TodoTools(IBoardStore store, TimeProvider time)
{
    [McpServerTool(Name = "get_dashboard", ReadOnly = true), Description("What the user should do now, what is waiting (with wake times), per-workstream overview, recent notes, and focus timer state.")]
    public Task<DashboardDto> GetDashboard([Description("Optional group (tab) name or id, e.g. 'work' or 'personal'.")] string? group = null) =>
        Guard(() => store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), ResolveGroup(b, group))));

    [McpServerTool(Name = "list_tasks", ReadOnly = true), Description("Full task tree (optionally for one group), including ids needed by the other tools.")]
    public Task<List<ItemDto>> ListTasks(string? group = null, bool includeDone = false) =>
        Guard(() => store.ReadAsync(b =>
        {
            var groupId = ResolveGroup(b, group);
            return b.Items.Where(i => (includeDone || !i.IsDone) && (groupId is null || i.GroupId == groupId)).Select(i => Wire.Item(i, time.GetUtcNow())).ToList();
        }));

    [McpServerTool(Name = "get_task", ReadOnly = true), Description("One task with its subtasks, reminders, and notes.")]
    public Task<ItemDto> GetTask(string taskId) =>
        Guard(() => store.ReadAsync(b => Wire.Item(b.Get(ParseId(taskId)), time.GetUtcNow())));

    [McpServerTool(Name = "create_task"), Description("Create a task (or a subtask when parentId is set). Use add_steps for gated rollout steps.")]
    public Task<ItemDto> CreateTask(
        McpServer server,
        string title,
        [Description("Parent task id to create a subtask.")] string? parentId = null,
        [Description("Group (tab) name or id for top-level tasks. Defaults to the first group.")] string? group = null,
        [Description("low | normal | high | critical")] string? priority = null,
        string? details = null,
        [Description("ISO-8601 deadline.")] DateTimeOffset? deadline = null,
        [Description("Defer the task (and remind) this many minutes from now.")] int? deferMinutes = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var item = b.AddTask(
                new NewTask(title)
                {
                    ParentId = parentId is null ? null : ParseId(parentId),
                    GroupId = ResolveGroup(b, group),
                    Priority = Wire.ParsePriority(priority),
                    Details = details,
                    Deadline = deadline,
                },
                actor,
                now);
            if (deferMinutes is > 0 and var m)
            {
                b.ScheduleNextAction(item.Id, now.AddMinutes(m), actor, now, notify: true);
            }

            return item;
        });

    [McpServerTool(Name = "add_steps"), Description("Add ordered steps under a task. Only the first open step is actionable; finishing one unlocks the next after stepDelayHours.")]
    public Task<List<ItemDto>> AddSteps(McpServer server, string parentId, string[] steps, double? stepDelayHours = null) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            return b.AddSteps(ParseId(parentId), steps, stepDelayHours is { } h ? TimeSpan.FromHours(h) : null, ActorOf(server), now).Select(s => Wire.Item(s, now)).ToList();
        }));

    [McpServerTool(Name = "add_note"), Description("Log progress on a task (\"did X, next Y\"). Shows in recent notes and the report timeline.")]
    public Task<NoteDto> AddNote(McpServer server, string taskId, string text, string? sourceUrl = null, string? sourceTitle = null) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var id = ParseId(taskId);
            var note = b.AddNote(id, text, ActorOf(server), time.GetUtcNow(), sourceUrl, sourceTitle);
            return Wire.Note(b.Get(id), note);
        }));

    [McpServerTool(Name = "schedule_next_action"), Description("Defer a task until later (it drops to Waiting) and, by default, remind the user when it is time.")]
    public Task<ItemDto> ScheduleNextAction(
        McpServer server,
        string taskId,
        [Description("Minutes from now.")] int? inMinutes = null,
        [Description("Exact ISO-8601 time (alternative to inMinutes).")] DateTimeOffset? at = null,
        bool notify = true,
        string? message = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.ScheduleNextAction(id, ApiEndpoints.ResolveTime(at, inMinutes, now), actor, now, notify, message);
            return b.Get(id);
        });

    [McpServerTool(Name = "add_reminder"), Description("Add a reminder without deferring the task.")]
    public Task<ItemDto> AddReminder(McpServer server, string taskId, string message, int? inMinutes = null, DateTimeOffset? at = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.AddReminder(id, ApiEndpoints.ResolveTime(at, inMinutes, now), message, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "complete_task", Idempotent = true), Description("Mark a task done (subtasks too). Completing a rollout step schedules the next step.")]
    public Task<ItemDto> CompleteTask(McpServer server, string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Complete(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "reopen_task", Idempotent = true), Description("Undo a completion.")]
    public Task<ItemDto> ReopenTask(McpServer server, string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Reopen(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "update_task"), Description("Change title, details, priority, or deadline.")]
    public Task<ItemDto> UpdateTask(McpServer server, string taskId, string? title = null, string? details = null, string? priority = null, DateTimeOffset? deadline = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Update(id, new TaskChanges { Title = title, Details = details, Priority = priority is null ? null : Wire.ParsePriority(priority), Deadline = deadline }, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "get_report", ReadOnly = true), Description("Timeline report (newest first) for one task or the whole board.")]
    public Task<ReportDto> GetReport(string? taskId = null, int limit = 100) =>
        Guard(() => store.ReadAsync(b =>
        {
            var id = taskId is null ? (Guid?)null : ParseId(taskId);
            return new ReportDto(id is { } i ? Wire.Item(b.Get(i), time.GetUtcNow()) : null, Wire.Timeline(b, id, limit));
        }));

    private Task<ItemDto> Mutate(McpServer server, Func<TaskBoard, DateTimeOffset, Actor, WorkItem> mutate) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            return Wire.Item(mutate(b, now, ActorOf(server)), now);
        }));

    private static Actor ActorOf(McpServer server) => Actor.Agent(server.ClientInfo?.Name ?? "mcp");

    private static Guid ParseId(string id) =>
        Guid.TryParse(id, out var guid) ? guid : throw new ArgumentException($"\"{id}\" is not a task id. Use list_tasks to find ids.", nameof(id));

    private static Guid? ResolveGroup(TaskBoard board, string? group)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return null;
        }

        var match = board.Groups.FirstOrDefault(g => string.Equals(g.Name, group.Trim(), StringComparison.OrdinalIgnoreCase) || g.Id.ToString() == group.Trim());
        return match?.Id ?? throw new ArgumentException($"Unknown group \"{group}\". Groups: {string.Join(", ", board.Groups.Select(g => g.Name))}.", nameof(group));
    }

    /// <summary>Turns domain validation errors into MCP tool errors with a readable message for the agent.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
