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
public sealed class TodoTools(IBoardStore store, TimeProvider time, VaultLinks links, HistoryService history, TodoTrackerServerOptions options, Hooks.HookStore hooks)
{
    private const int MaxTextAttachmentBytes = 1024 * 1024;

    [McpServerTool(Name = "get_dashboard", ReadOnly = true), Description("What the user should do now, what is waiting (with wake times), per-workstream overview, recent notes, what got done today, and focus timer state.")]
    public Task<DashboardDto> GetDashboard(
        [Description("Optional group (tab) name or id, e.g. 'work' or 'personal'.")] string? group = null,
        [Description("Optional filter in search syntax, e.g. '#release' or 'label:\"deep work\"'.")] string? query = null) =>
        Guard(() => store.ReadAsync(b => Wire.Dashboard(b, time.GetUtcNow(), ResolveGroup(b, group), 5, links, query, options.TimeZone)));

    [McpServerTool(Name = "list_tasks", ReadOnly = true), Description("Full task tree (optionally for one group), including ids needed by the other tools.")]
    public Task<List<ItemDto>> ListTasks([Description("Group (tab) name or id, e.g. 'Work'. Omit for every group.")] string? group = null, [Description("Include finished tasks.")] bool includeDone = false) =>
        Guard(() => store.ReadAsync(b =>
        {
            var groupId = ResolveGroup(b, group);
            return b.Items.Where(i => (includeDone || !i.IsDone) && (groupId is null || i.GroupId == groupId)).Select(i => Wire.Item(i, time.GetUtcNow(), b, links)).ToList();
        }));

    [McpServerTool(Name = "search_tasks", ReadOnly = true), Description(
        "Find tasks and subtasks. Words match title or details; #tag (nested tags too), label:name, group:name, is:open|is:done narrow it. " +
        "Tags and labels are inherited by subtasks. Quote multi-word values: label:\"deep work\". Open tasks only unless the query has is:.")]
    public Task<List<SearchHitDto>> SearchTasks([Description("Search syntax: words, #tag, label:\"deep work\", group:work, is:open, is:done, is:archived.")] string query) =>
        Guard(() => store.ReadAsync(b => ApiEndpoints.Search(b, query, time.GetUtcNow(), links)));

    [McpServerTool(Name = "get_task", ReadOnly = true), Description("One task with its subtasks, reminders, notes, tags, labels, attachments, its markdown file, and its link (todotracker://task/<id>: give it to the user, clicking it opens the task in the app).")]
    public Task<ItemDto> GetTask([Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Guard(() => store.ReadAsync(b => Wire.Item(b.Get(ParseId(taskId)), time.GetUtcNow(), b, links)));

    [McpServerTool(Name = "create_task"), Description("Create a task (or a subtask when parentId is set). Use add_steps for ordered rollout steps.")]
    public Task<ItemDto> CreateTask(
        McpServer server,
        [Description("What to do, in the user's words.")] string title,
        [Description("Parent task id to create a subtask.")] string? parentId = null,
        [Description("Group (tab) name or id for top-level tasks. Defaults to the first group.")] string? group = null,
        [Description("low | normal | high | critical")] string? priority = null,
        [Description("Longer notes about the task (markdown).")] string? details = null,
        [Description("ISO-8601 deadline.")] DateTimeOffset? deadline = null,
        [Description("Defer the task (and remind) this many minutes from now.")] int? deferMinutes = null,
        [Description("Free-form tags without '#', e.g. ['release', 'infra/k8s'].")] string[]? tags = null,
        [Description("Curated labels, e.g. ['Deep work']. Unknown labels are created.")] string[]? labels = null,
        [Description("How it repeats, e.g. \"every weekday\", \"every 2 weeks on Monday, Thursday\", \"every month on the last Friday\" (+ \"when done\", \"until 2026-12-31\", \"for 5 times\"). Completing it then moves it to its next time.")] string? repeat = null) =>
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
                    Repeat = repeat,
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
    public Task<List<ItemDto>> AddSteps(McpServer server, [Description("The task the steps go under (a full id).")] string parentId, [Description("The steps, in order, e.g. ['Ring 0', 'Ring 1'].")] string[] steps, [Description("Hours between finishing one step and the next one coming up (e.g. 24 for a rollout); default: the delay the task already has (none for a new one).")] double? stepDelayHours = null) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            return b.AddSteps(ParseId(parentId), steps, stepDelayHours is { } h ? TimeSpan.FromHours(h) : null, ActorOf(server), now).Select(s => Wire.Item(s, now, b)).ToList();
        }));

    [McpServerTool(Name = "add_note"), Description("Log progress on a task (\"did X, next Y\"). Shows in recent notes and the report timeline.")]
    public Task<NoteDto> AddNote(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("What happened, short and in past tense, with the next step: \"Deployed ring 0, next: check dashboards tomorrow\".")] string text, [Description("A web page the note is about (http/https).")] string? sourceUrl = null, [Description("That page's title.")] string? sourceTitle = null) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var id = ParseId(taskId);
            var note = b.AddNote(id, text, ActorOf(server), time.GetUtcNow(), sourceUrl, sourceTitle);
            return Wire.Note(b.Get(id), note);
        }));

    [McpServerTool(Name = "schedule_next_action"), Description("Defer a task until later (it drops to Waiting) and, by default, remind the user when it is time.")]
    public Task<ItemDto> ScheduleNextAction(
        McpServer server,
        [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId,
        [Description("Minutes from now.")] int? inMinutes = null,
        [Description("Exact ISO-8601 time (alternative to inMinutes).")] DateTimeOffset? at = null,
        [Description("Remind the user when it is time (default true).")] bool notify = true,
        [Description("The reminder's text (default: the task's title).")] string? message = null,
        [Description("Or in words, in the user's time zone: 3d, 2 weeks, fri 14:00, next week, weekend, tonight, 9am, 2026-03-01 14:00.")] string? when = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.ScheduleNextAction(id, ApiEndpoints.ResolveSnooze(new ScheduleRequest(at, inMinutes, Rule: when), now, options.TimeZone), actor, now, notify, message);
            return b.Get(id);
        });

    [McpServerTool(Name = "plan_task"), Description("Plan a block of time to work on a task (it shows in the Plan view, and becomes the thing to do now while the block is on); without a start, takes it off the plan.")]
    public Task<ItemDto> PlanTask(
        McpServer server,
        [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId,
        [Description("ISO-8601 start of the block; omit to take it off the plan.")] DateTimeOffset? start = null,
        [Description("How long, in minutes (5 to 720; default: what it had, else 30).")] int? minutes = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            if (start is { } at)
            {
                b.Plan(id, at, minutes, actor, now);
            }
            else
            {
                b.Unplan(id, actor, now);
            }

            return b.Get(id);
        });

    [McpServerTool(Name = "wait_for_task"), Description("Defer a task until another task is done: it waits (drops to Waiting) and comes back with a reminder when that one is completed.")]
    public Task<ItemDto> WaitForTask(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("The task it waits for.")] string afterTaskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.WaitFor(id, ParseId(afterTaskId), actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "add_reminder"), Description("Add a reminder without deferring the task.")]
    public Task<ItemDto> AddReminder(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("The reminder's text.")] string message, [Description("Minutes from now.")] int? inMinutes = null, [Description("Exact ISO-8601 time (alternative to inMinutes).")] DateTimeOffset? at = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.AddReminder(id, ApiEndpoints.ResolveTime(at, inMinutes, now), message, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "complete_task", Idempotent = true), Description("Mark a task done (subtasks too). Completing a rollout step schedules the next step; a repeating task moves on to its next time instead (reopen_task undoes that).")]
    public Task<ItemDto> CompleteTask(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Complete(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "reopen_task", Idempotent = true), Description("Undo a completion.")]
    public Task<ItemDto> ReopenTask(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Reopen(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "update_task"), Description("Change title, details, priority, deadline, tags, labels, icon, color or how it repeats (tags/labels replace the current ones when given; an empty icon, color or repeat removes it).")]
    public Task<ItemDto> UpdateTask(
        McpServer server,
        [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId,
        [Description("New title.")] string? title = null,
        [Description("New details (markdown); replaces what it had.")] string? details = null,
        [Description("low | normal | high | critical")] string? priority = null,
        [Description("New ISO-8601 deadline.")] DateTimeOffset? deadline = null,
        [Description("Tags without '#': replace the current ones.")] string[]? tags = null,
        [Description("Curated labels: replace the current ones (unknown labels are created).")] string[]? labels = null,
        [Description("An emoji shown before the title, e.g. 🚀; empty removes it.")] string? icon = null,
        [Description("A color as #rrggbb, e.g. #3b82f6; empty removes it (the first label's color shows then).")] string? color = null,
        [Description("How it repeats, e.g. \"every weekday\", \"every 2 weeks on Monday, Thursday\", \"every month on the last Friday\" (+ \"when done\", \"until 2026-12-31\", \"for 5 times\"). Empty: stops repeating.")] string? repeat = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Update(id, new TaskChanges
            {
                Title = title,
                Details = details,
                Priority = priority is null ? null : Wire.ParsePriority(priority),
                Deadline = deadline,
                Icon = string.IsNullOrWhiteSpace(icon) ? null : icon,
                ClearIcon = icon is not null && string.IsNullOrWhiteSpace(icon),
                Color = string.IsNullOrWhiteSpace(color) ? null : color,
                ClearColor = color is not null && string.IsNullOrWhiteSpace(color),
                Repeat = string.IsNullOrWhiteSpace(repeat) ? null : repeat,
                ClearRepeat = repeat is not null && string.IsNullOrWhiteSpace(repeat),
            }, actor, now);
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

    [McpServerTool(Name = "move_task"), Description("Reorganize: put a task under another task (parentId), back at the top level (toTopLevel, optionally in a group), or into another group. index alone reorders a subtask among its siblings.")]
    public Task<ItemDto> MoveTask(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("Put it under this task (a full id).")] string? parentId = null, [Description("Group (tab) name or id, e.g. 'Work': moves it to that group (a subtask becomes a top-level task there).")] string? group = null, [Description("Position among its siblings (0: first). Alone, it reorders a subtask under its parent; with parentId or toTopLevel, among its new siblings.")] int? index = null, [Description("Take it out from under its parent, to the top level.")] bool toTopLevel = false) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            var groupId = ResolveGroup(b, group);
            if (parentId is not null)
            {
                b.Move(id, ParseId(parentId), index, null, actor, now);
            }
            else if (!toTopLevel && groupId is null && index is not null && b.Get(id).Parent is { } parent)
            {
                // Only a position: reordered where it is (not taken out from under its parent).
                b.Move(id, parent.Id, index, null, actor, now);
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

    [McpServerTool(Name = "put_first"), Description("Put tasks at the top of Do now, in this order; the first becomes the focus (only a reminder going off later comes above them). Use it when the user says what matters most (\"do X first\").")]
    public Task<DashboardDto> PutFirst([Description("Task ids, most important first.")] string[] taskIds) =>
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
    public Task<AttachmentDto> AttachText(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("File name with extension, e.g. 'timeline.md'.")] string fileName, [Description("The file's text (up to 1 MB).")] string text) =>
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
    public Task<string> GetRichHtml([Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Guard(async () => await links.Vault.ReadRichAsync(ParseId(taskId)).ConfigureAwait(false) ?? "(this task has no rich version)");

    [McpServerTool(Name = "set_rich_html"), Description("Save a rich HTML version of a top-level task when markdown isn't enough (tables, dashboards, colors). Scripts never run. Empty html removes it.")]
    public Task<RichDto> SetRichHtml(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("The HTML (scripts never run); empty removes it.")] string html) =>
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
    public Task<IReadOnlyList<VaultVersion>> TaskHistory([Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Guard(() => history.Required.TaskHistoryAsync(ParseId(taskId)));

    [McpServerTool(Name = "restore_task_version"), Description("Put a task back the way it was in an earlier version (from task_history). The current state stays in the history, so this can be undone.")]
    public Task<ItemDto> RestoreTaskVersion(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("A version id from task_history.")] string versionId) =>
        Guard(async () =>
        {
            var id = ParseId(taskId);
            await history.Required.RestoreTaskAsync(id, versionId, ActorOf(server)).ConfigureAwait(false);
            return await store.ReadAsync(b => Wire.Item(b.Get(id), time.GetUtcNow(), b, links)).ConfigureAwait(false);
        });

    [McpServerTool(Name = "get_report", ReadOnly = true), Description("Timeline report (newest first) for one task or the whole board.")]
    public Task<ReportDto> GetReport([Description("Only this task (a full id); omit for the whole board.")] string? taskId = null, [Description("At most this many entries (default 100).")] int limit = 100) =>
        Guard(() => store.ReadAsync(b =>
        {
            var id = taskId is null ? (Guid?)null : ParseId(taskId);
            return new ReportDto(id is { } i ? Wire.Item(b.Get(i), time.GetUtcNow(), b, links) : null, Wire.Timeline(b, id, limit));
        }));

    [McpServerTool(Name = "move_card"), Description("Move a top-level task to a board column: inbox (just captured), next (ready), or doing (being worked on). Finish a task with complete_task to move it to done.")]
    public Task<ItemDto> MoveCard(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("inbox, next or doing")] string stage) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.SetStage(id, BoardTimeEndpoints.ParseStage(stage), actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "archive_task", Idempotent = true), Description("Put a finished top-level task away: it leaves every list but is kept (search is:archived). Only done tasks can be archived.")]
    public Task<ItemDto> ArchiveTask(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Archive(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "unarchive_task", Idempotent = true), Description("Bring an archived task back to the done list.")]
    public Task<ItemDto> UnarchiveTask(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.Unarchive(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "start_timer"), Description("Start timing work on a task (any other timer stops; one runs at a time). The task's card moves to doing.")]
    public Task<TimerDto> StartTimer(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
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
    public Task<TimeEntryDto> LogTime(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("When it started (ISO-8601).")] DateTimeOffset start, [Description("How long, in minutes (at most 24 hours).")] int minutes) =>
        Guard(() => store.UpdateAsync(b =>
        {
            var now = time.GetUtcNow();
            var id = ParseId(taskId);
            return Wire.TimeEntry(b.Get(id), b.AddTime(id, start, start.AddMinutes(minutes), ActorOf(server), now), now);
        }));

    [McpServerTool(Name = "get_time_report", ReadOnly = true), Description("Where the time went between two local dates (yyyy-MM-dd, inclusive; default the last 4 weeks): seconds tracked per day, group and task, focus sessions, tasks done, streaks, and a timeline.")]
    public Task<TimeReport> GetTimeReport([Description("First day, yyyy-MM-dd (default: 4 weeks ago).")] string? from = null, [Description("Last day, yyyy-MM-dd, inclusive (default: today).")] string? to = null, [Description("Group (tab) name or id, e.g. 'Work'. Omit for every group.")] string? group = null) =>
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

    // ---- Hooks: what runs when something happens. An agent's hook waits for the person's approval. ----------------

    [McpServerTool(Name = "list_hooks", ReadOnly = true), Description("The hooks set up on this computer: what runs (a command or a web address) when something happens in Todo Tracker, and whether each is on, off or waiting for approval.")]
    public Task<IReadOnlyList<Hooks.HookEntry>> ListHooks() => Guard(() => Task.FromResult<IReadOnlyList<Hooks.HookEntry>>(hooks.List().Select(h => h.Redacted()).ToList()));

    [McpServerTool(Name = "add_hook"), Description("Add a hook: run a command (PowerShell on Windows, bash on Linux/macOS; the event comes as JSON on stdin) or post the event to a URL when something happens. It doesn't run until the user allows it: Todo Tracker asks them (a notification or a prompt in the app; also Settings › Hooks, or tt hooks approve); tell them so. Events: taskCreated, taskUpdated, taskCompleted, taskReopened, taskDeleted, taskScheduled, taskMoved, taskArchived, noteAdded, reminderAdded, reminderDue, reminderDismissed, attachmentAdded, attachmentRemoved, labelChanged, groupChanged, timeLogged, focusStarted, focusEnded, breakEnded, or * for every event.")]
    public Task<Hooks.HookEntry> AddHook(
        McpServer server,
        [Description("When it runs: an event name, or * for every event.")] string @event,
        [Description("What to run on Windows (PowerShell). Read the event with [Console]::In.ReadToEnd() | ConvertFrom-Json.")] string? powershell = null,
        [Description("What to run on Linux and macOS (bash). The event is on stdin, e.g. jq -r .task.title.")] string? bash = null,
        [Description("Instead of a command: post the event as JSON to this http(s) URL.")] string? url = null,
        [Description("A short name for it.")] string? name = null,
        [Description("Stop it after this many seconds (default 30, at most 600).")] int? timeoutSec = null,
        [Description("Only for tasks in these groups.")] string[]? groups = null,
        [Description("Only for tasks with one of these tags.")] string[]? tags = null,
        [Description("Only for tasks with one of these labels.")] string[]? labels = null,
        [Description("Only when done by these: user, agent, vault, system, connector, or an agent's name.")] string[]? actors = null) =>
        Guard(() => Task.FromResult(hooks.Add(@event, new Hooks.HookSpec
        {
            Type = url is null ? "command" : "http",
            Powershell = powershell,
            Bash = bash,
            Url = url,
            Name = name,
            TimeoutSec = timeoutSec,
            Match = new Hooks.HookMatch { Groups = groups?.ToList(), Tags = tags?.ToList(), Labels = labels?.ToList(), Actors = actors?.ToList() },
        }, new Hooks.HookAuthor(ActorOf(server).Name ?? "agent", Trusted: false), time.GetUtcNow()).Redacted()));

    [McpServerTool(Name = "set_hook_enabled"), Description("Switch a hook on or off (by its id from list_hooks). A hook waiting for approval still needs the user's approval to run.")]
    public Task<Hooks.HookEntry> SetHookEnabled([Description("The hook's id, from list_hooks.")] string hookId, [Description("On (true) or off (false).")] bool enabled) => Guard(() => Task.FromResult(hooks.SetEnabled(hookId, enabled).Redacted()));

    [McpServerTool(Name = "remove_hook", Destructive = true), Description("Remove a hook (by its id from list_hooks).")]
    public Task<Hooks.HookRemoved> RemoveHook([Description("The hook's id, from list_hooks.")] string hookId) => Guard(() =>
    {
        hooks.Remove(hookId);
        return Task.FromResult(new Hooks.HookRemoved(hookId));
    });

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
