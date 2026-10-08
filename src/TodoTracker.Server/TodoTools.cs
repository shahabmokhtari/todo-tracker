using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using TodoTracker.Core;
using TodoTracker.Core.Vault;

namespace TodoTracker.Server;

/// <summary>
/// MCP tool surface for Copilot, Claude, ChatGPT and other agents. Every change is attributed to the calling agent and
/// shows up in the sidebar and timeline. There is intentionally no delete tool: agents can complete/reopen but not
/// destroy data. Tasks also live as markdown files (see <c>vault_info</c>), so agents with file tools can work there.
/// </summary>
[McpServerToolType]
public sealed class TodoTools(IBoardStore store, TimeProvider time, VaultLinks links, HistoryService history, TodoTrackerServerOptions options)
{
    private const int MaxTextAttachmentBytes = 1024 * 1024;

    [McpServerTool(Name = "get_dashboard", ReadOnly = true), Description("What the user should do now, what is waiting (with wake times), per-workstream overview, recent notes, and focus timer state.")]
    public Task<DashboardDto> GetDashboard(
        [Description("Optional group (tab) name or id, e.g. 'work' or 'personal'.")] string? group = null,
        [Description("Optional filter in search syntax, e.g. '#release' or 'label:\"deep work\"'.")] string? query = null) =>
        Guard(() => store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), ResolveGroup(b, group), 5, links, query)));

    [McpServerTool(Name = "list_tasks", ReadOnly = true), Description("Full task tree (optionally for one group), including ids needed by the other tools.")]
    public Task<List<ItemDto>> ListTasks(string? group = null, bool includeDone = false) =>
        Guard(() => store.ReadAsync(b =>
        {
            var groupId = ResolveGroup(b, group);
            return b.Items.Where(i => (includeDone || !i.IsDone) && (groupId is null || i.GroupId == groupId)).Select(i => Wire.Item(i, time.GetUtcNow(), b, links)).ToList();
        }));

    [McpServerTool(Name = "search_tasks", ReadOnly = true), Description(
        "Find tasks and subtasks. Words match title or details; #tag (nested tags too), label:name, group:name, is:open|is:done narrow it. " +
        "Tags and labels are inherited by subtasks. Quote multi-word values: label:\"deep work\". Open tasks only unless the query has is:.")]
    public Task<List<SearchHitDto>> SearchTasks(string query) =>
        Guard(() => store.ReadAsync(b => ApiEndpoints.Search(b, query, time.GetUtcNow(), links)));

    [McpServerTool(Name = "get_task", ReadOnly = true), Description("One task with its subtasks, reminders, notes, tags, labels, attachments, and its markdown file.")]
    public Task<ItemDto> GetTask(string taskId) =>
        Guard(() => store.ReadAsync(b => Wire.Item(b.Get(ParseId(taskId)), time.GetUtcNow(), b, links)));

    [McpServerTool(Name = "create_task"), Description("Create a task (or a subtask when parentId is set). Use add_steps for ordered rollout steps.")]
    public Task<ItemDto> CreateTask(
        McpServer server,
        string title,
        [Description("Parent task id to create a subtask.")] string? parentId = null,
        [Description("Group (tab) name or id for top-level tasks. Defaults to the first group.")] string? group = null,
        [Description("low | normal | high | critical")] string? priority = null,
        string? details = null,
        [Description("ISO-8601 deadline.")] DateTimeOffset? deadline = null,
        [Description("Defer the task (and remind) this many minutes from now.")] int? deferMinutes = null,
        [Description("Free-form tags without '#', e.g. ['release', 'infra/k8s'].")] string[]? tags = null,
        [Description("Curated labels, e.g. ['Deep work']. Unknown labels are created.")] string[]? labels = null) =>
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
                    Tags = tags,
                    Labels = labels,
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
            return b.AddSteps(ParseId(parentId), steps, stepDelayHours is { } h ? TimeSpan.FromHours(h) : null, ActorOf(server), now).Select(s => Wire.Item(s, now, b)).ToList();
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
        string? message = null,
        [Description("Or in words, in the user's time zone: 3d, 2 weeks, fri 14:00, next week, weekend, tonight, 9am, 2026-03-01 14:00.")] string? when = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.ScheduleNextAction(id, ApiEndpoints.ResolveSnooze(new ScheduleRequest(at, inMinutes, Rule: when), now, options.TimeZone), actor, now, notify, message);
            return b.Get(id);
        });

    [McpServerTool(Name = "wait_for_task"), Description("Defer a task until another task is done: it waits (drops to Waiting) and comes back with a reminder when that one is completed.")]
    public Task<ItemDto> WaitForTask(McpServer server, string taskId, [Description("The task it waits for.")] string afterTaskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.WaitFor(id, ParseId(afterTaskId), actor, now);
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

    [McpServerTool(Name = "update_task"), Description("Change title, details, priority, deadline, tags, or labels (tags/labels replace the current ones when given).")]
    public Task<ItemDto> UpdateTask(
        McpServer server,
        string taskId,
        string? title = null,
        string? details = null,
        string? priority = null,
        DateTimeOffset? deadline = null,
        string[]? tags = null,
        string[]? labels = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Update(id, new TaskChanges { Title = title, Details = details, Priority = priority is null ? null : Wire.ParsePriority(priority), Deadline = deadline }, actor, now);
            if (tags is not null)
            {
                b.SetTags(id, tags, actor, now);
            }

            if (labels is not null)
            {
                b.SetLabels(id, labels, actor, now);
            }

            return b.Get(id);
        });

    [McpServerTool(Name = "move_task"), Description("Reorganize: put a task under another task (parentId), back at the top level (toTopLevel, optionally in a group), or into another group. index sets the position among siblings.")]
    public Task<ItemDto> MoveTask(McpServer server, string taskId, string? parentId = null, string? group = null, int? index = null, bool toTopLevel = false) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            var groupId = ResolveGroup(b, group);
            if (parentId is not null)
            {
                b.Move(id, ParseId(parentId), index, null, actor, now);
            }
            else if (toTopLevel || b.Get(id).Parent is not null)
            {
                b.Move(id, null, index, groupId, actor, now);
            }
            else if (groupId is { } g)
            {
                b.MoveToGroup(id, g, actor, now);
            }
            else
            {
                throw new ArgumentException("Say where to move the task: parentId, group, or toTopLevel.", nameof(taskId));
            }

            return b.Get(id);
        });

    [McpServerTool(Name = "put_first"), Description("Put tasks at the top of Do now, in this order; the first becomes the focus (only a task whose reminder is due stays above it). Use it when the user says what matters most (\"do X first\").")]
    public Task<DashboardDto> PutFirst(string[] taskIds) =>
        Guard(async () =>
        {
            var ids = taskIds.Select(ParseId).ToList();
            await store.UpdateAsync(b =>
            {
                b.PutFirst(ids, time.GetUtcNow());
                return true;
            }).ConfigureAwait(false);
            return await store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), null, 0, links)).ConfigureAwait(false);
        });

    [McpServerTool(Name = "list_labels", ReadOnly = true), Description("The curated labels (name and color) the user picks from.")]
    public Task<IReadOnlyList<LabelDto>> ListLabels() => Guard(() => store.ReadAsync(Wire.Labels));

    [McpServerTool(Name = "attach_text"), Description("Attach a text file (log excerpt, plan, summary…) to a task. It is saved in the vault and linked from the task.")]
    public Task<AttachmentDto> AttachText(McpServer server, string taskId, [Description("File name with extension, e.g. 'timeline.md'.")] string fileName, string text) =>
        Guard(async () =>
        {
            var bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            if (bytes.Length > MaxTextAttachmentBytes)
            {
                throw new ArgumentException("Text attachments can be at most 1 MB.", nameof(text));
            }

            var id = ParseId(taskId);
            using var stream = new MemoryStream(bytes);
            var attachment = await links.Vault.AddAttachmentAsync(id, fileName, stream, ActorOf(server)).ConfigureAwait(false);
            var item = await store.ReadAsync(b => b.Get(id)).ConfigureAwait(false);
            return Wire.Attachment(item, attachment);
        });

    [McpServerTool(Name = "get_rich_html", ReadOnly = true), Description("The task's rich HTML version (tables, layouts, colors), if it has one. Only top-level tasks have one.")]
    public Task<string> GetRichHtml(string taskId) =>
        Guard(async () => await links.Vault.ReadRichAsync(ParseId(taskId)).ConfigureAwait(false) ?? "(this task has no rich version)");

    [McpServerTool(Name = "set_rich_html"), Description("Save a rich HTML version of a top-level task when markdown isn't enough (tables, dashboards, colors). Scripts never run. Empty html removes it.")]
    public Task<RichDto> SetRichHtml(McpServer server, string taskId, string html) =>
        Guard(async () =>
        {
            var clean = string.IsNullOrWhiteSpace(html) ? null : html;
            await links.Vault.WriteRichAsync(ParseId(taskId), clean, ActorOf(server)).ConfigureAwait(false);
            return new RichDto(clean is not null);
        });

    [McpServerTool(Name = "vault_info", ReadOnly = true), Description("Where the markdown files live, files that need fixing, and the file format guide (for agents that edit the files directly).")]
    public async Task<VaultDto> VaultInfo()
    {
        await store.ReadAsync(_ => 0).ConfigureAwait(false);
        return new VaultDto(links.Vault.RootPath, links.Vault.Problems, VaultBoardStore.Guide, ObsidianVaults.OpenUrl(links.Vault.RootPath), ObsidianVaults.IsInstalled());
    }

    [McpServerTool(Name = "task_history", ReadOnly = true), Description("Saved versions of a task's file, newest first (every change is versioned). Use restore_task_version to go back.")]
    public Task<IReadOnlyList<VaultVersion>> TaskHistory(string taskId) =>
        Guard(() => history.Required.TaskHistoryAsync(ParseId(taskId)));

    [McpServerTool(Name = "restore_task_version"), Description("Put a task back the way it was in an earlier version (from task_history). The current state stays in the history, so this can be undone.")]
    public Task<ItemDto> RestoreTaskVersion(McpServer server, string taskId, string versionId) =>
        Guard(async () =>
        {
            var id = ParseId(taskId);
            await history.Required.RestoreTaskAsync(id, versionId, ActorOf(server)).ConfigureAwait(false);
            return await store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow(), b, links)).ConfigureAwait(false);
        });

    [McpServerTool(Name = "get_report", ReadOnly = true), Description("Timeline report (newest first) for one task or the whole board.")]
    public Task<ReportDto> GetReport(string? taskId = null, int limit = 100) =>
        Guard(() => store.ReadAsync(b =>
        {
            var id = taskId is null ? (Guid?)null : ParseId(taskId);
            return new ReportDto(id is { } i ? Wire.Item(b.Get(i), time.GetUtcNow(), b, links) : null, Wire.Timeline(b, id, limit));
        }));

    [McpServerTool(Name = "move_card"), Description("Move a top-level task to a board column: inbox (just captured), next (ready), or doing (being worked on). Finish a task with complete_task to move it to done.")]
    public Task<ItemDto> MoveCard(McpServer server, string taskId, [Description("inbox, next or doing")] string stage) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.SetStage(id, BoardTimeEndpoints.ParseStage(stage), actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "archive_task", Idempotent = true), Description("Put a finished top-level task away: it leaves every list but is kept (search is:archived). Only done tasks can be archived.")]
    public Task<ItemDto> ArchiveTask(McpServer server, string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Archive(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "unarchive_task", Idempotent = true), Description("Bring an archived task back to the done list.")]
    public Task<ItemDto> UnarchiveTask(McpServer server, string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Unarchive(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "start_timer"), Description("Start timing work on a task (any other timer stops; one runs at a time). The task's card moves to doing.")]
    public Task<TimerDto> StartTimer(McpServer server, string taskId) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            b.StartTimer(ParseId(taskId), ActorOf(server), now);
            return Wire.Timer(b, now);
        }));

    [McpServerTool(Name = "stop_timer", Idempotent = true), Description("Stop the timer (whatever task it is on).")]
    public Task<TimerDto> StopTimer() =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            b.StopTimer(now);
            return Wire.Timer(b, now);
        }));

    [McpServerTool(Name = "log_time"), Description("Record time already spent on a task (e.g. a meeting): when it started and for how many minutes (at most 24 hours).")]
    public Task<TimeEntryDto> LogTime(McpServer server, string taskId, [Description("When it started (ISO-8601).")] DateTimeOffset start, int minutes) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            var id = ParseId(taskId);
            return Wire.TimeEntry(b.Get(id), b.AddTime(id, start, start.AddMinutes(minutes), ActorOf(server), now), now);
        }));

    [McpServerTool(Name = "get_time_report", ReadOnly = true), Description("Where the time went between two local dates (yyyy-MM-dd, inclusive; default the last 4 weeks): seconds tracked per day, group and task, focus sessions, tasks done, streaks, and a timeline.")]
    public Task<TimeReport> GetTimeReport(string? from = null, string? to = null, string? group = null) =>
        Guard(() => store.ReadAsync(b =>
        {
            var now = time.GetUtcNow();
            var end = to is null ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, options.TimeZone).DateTime) : ParseDate(to);
            return TimeReport.Build(b, from is null ? end.AddDays(-27) : ParseDate(from), end, options.TimeZone, now, ResolveGroup(b, group));
        }));

    private static DateOnly ParseDate(string text) =>
        DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException($"\"{text}\" isn't a date; use yyyy-MM-dd.", nameof(text));

    private Task<ItemDto> Mutate(McpServer server, Func<TaskBoard, DateTimeOffset, Actor, WorkItem> mutate) =>
        Guard(async () =>
        {
            var id = await store.UpdateAsync(b => mutate(b, time.GetUtcNow(), ActorOf(server)).Id).ConfigureAwait(false);
            return await store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow(), b, links)).ConfigureAwait(false);
        });

    private static Actor ActorOf(McpServer server) => Actor.Agent(server.ClientInfo?.Name ?? "mcp");

    private static Guid ParseId(string id) =>
        Guid.TryParse(id, out var guid) ? guid : throw new ArgumentException($"\"{id}\" is not a task id. Use search_tasks or list_tasks to find ids.", nameof(id));

    internal static Guid? ResolveGroup(TaskBoard board, string? group)
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
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new McpException(ErrorText.Friendly(ex), ex);
        }
    }
}
