using System.ComponentModel;
using ModelContextProtocol.Server;
using OneThing.Core;
using OneThing.Core.Vault;

namespace OneThing.Server;

/// <summary>
/// The rest of what the app does that's safe for an agent: quick capture, reminders, notes, Do now order, time entries,
/// the focus timer, the plan, labels, tags, history, export, attachments and hooks. Still no deletes, no settings and
/// no switching extras: those are the person's, in the app.
/// </summary>
public sealed partial class TodoTools
{
    private const int MaxReadAttachmentBytes = 1024 * 1024;

    [McpServerTool(Name = "capture_task"), Description("Capture a task the way the user would type it in quick add: \"Renew passport !! due:7d #admin @tomorrow\" (! high, !! critical, !low; @2h or @tomorrow defers it; due:3d or due:2026-02-01; #tag). Use create_task when you already have the fields.")]
    public Task<ItemDto> CaptureTask(
        McpServer server,
        [Description("The words, quick-capture syntax included, e.g. 'Call the bank !! due:fri #money'.")] string text,
        [Description("Group (tab) name or id for a top-level task. Defaults to the first group.")] string? group = null,
        [Description("Parent task id to capture a subtask.")] string? parentId = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var capture = QuickCaptureParser.Parse(text ?? string.Empty, now, Options.TimeZone);
            var item = b.AddTask(new NewTask(capture.Title) { ParentId = parentId is null ? null : ParseId(parentId), GroupId = ResolveGroup(b, group), Priority = capture.Priority, Deadline = capture.Deadline, Tags = capture.Tags }, actor, now);
            if (capture.NextActionAt is { } at)
            {
                b.ScheduleNextAction(item.Id, at, actor, now, notify: true);
            }

            return item;
        });

    [McpServerTool(Name = "dismiss_reminder", Idempotent = true), Description("Dismiss one of a task's reminders (its id is in get_task's reminders): it won't go off. The task stays as it is.")]
    public Task<ItemDto> DismissReminder(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("The reminder's id, from get_task.")] string reminderId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.DismissReminder(id, ParseId(reminderId), actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "clear_snooze", Idempotent = true), Description("Bring a deferred (snoozed or waiting) task back to Do now: the opposite of schedule_next_action and wait_for_task.")]
    public Task<ItemDto> ClearSnooze(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            b.ClearNextAction(id, actor, now);
            return b.Get(id);
        });

    [McpServerTool(Name = "update_note"), Description("Correct a progress note you wrote on a task (its id is in get_task's notes). Notes by the user or another agent are theirs: add a new note instead. The note keeps its time and author; the change is versioned.")]
    public Task<NoteDto> UpdateNote(McpServer server, [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("The note's id, from get_task.")] string noteId, [Description("The note's new text.")] string text) =>
        Guard(() => Store.UpdateAsync(b =>
        {
            var id = ParseId(taskId);
            return Wire.Note(b.Get(id), b.UpdateNote(id, ParseId(noteId), text, ActorOf(server), Time.GetUtcNow()));
        }));

    [McpServerTool(Name = "arrange_now"), Description("Set the whole order of Do now (the first becomes the focus), as the user arranged it. To put a few tasks on top and leave the rest, use put_first.")]
    public Task<DashboardDto> ArrangeNow(McpServer server, [Description("Task ids in the order wanted, first at the top.")] string[] taskIds) =>
        Guard(async () =>
        {
            var ids = taskIds.Select(ParseId).ToList();
            await Store.UpdateAsync(b =>
            {
                b.ArrangeNow(ids, ActorOf(server), Time.GetUtcNow());
                return true;
            }).ConfigureAwait(false);
            return await Store.ReadAsync(b => Wire.Dashboard(b, Time.GetUtcNow(), null, 0, Links)).ConfigureAwait(false);
        });

    [McpServerTool(Name = "reorder_task"), Description("Move a task next to a sibling (a subtask under the same parent, or a task in the same board column): right before beforeTaskId, or right after afterTaskId. Neither: it goes last.")]
    public Task<ItemDto> ReorderTask(
        McpServer server,
        [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId,
        [Description("Put it right before this sibling (a full id).")] string? beforeTaskId = null,
        [Description("Or right after this sibling (a full id).")] string? afterTaskId = null) =>
        Mutate(server, (b, now, actor) =>
        {
            var id = ParseId(taskId);
            if (afterTaskId is not null)
            {
                b.ReorderAfter(id, ParseId(afterTaskId), actor, now);
            }
            else
            {
                b.Reorder(id, beforeTaskId is null ? null : ParseId(beforeTaskId), actor, now);
            }

            return b.Get(id);
        });

    [McpServerTool(Name = "update_time_entry"), Description("Correct time logged on a task (an entry's id is in get_task's timeEntries): when it started and how long (at most 24 hours). Logged time can't be removed by an agent.")]
    public Task<TimeEntryDto> UpdateTimeEntry(
        McpServer server,
        [Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId,
        [Description("The time entry's id, from get_task.")] string entryId,
        [Description("When it started (ISO-8601).")] DateTimeOffset start,
        [Description("How long, in minutes (at most 24 hours).")] int minutes) =>
        Guard(() => Store.UpdateAsync(b =>
        {
            var now = Time.GetUtcNow();
            var id = ParseId(taskId);
            var entry = ParseId(entryId);
            b.UpdateTime(id, entry, start, start.AddMinutes(minutes), ActorOf(server), now);
            var item = b.Get(id);
            return Wire.TimeEntry(item, item.TimeEntries.First(e => e.Id == entry), now);
        }));

    [McpServerTool(Name = "focus_timer"), Description("The focus (Pomodoro) timer: start a focus session (on a task, timed as focus time on it), pause, resume, skip to the next phase, reset, or next (start the next session). Its state is also in get_dashboard (pomodoro).")]
    public Task<PomodoroDto> FocusTimer(
        McpServer server,
        [Description("start, pause, resume, skip, reset or next.")] string action,
        [Description("For start and next: the task to focus on (a full id). Omit to focus on nothing in particular (next: the last session's task, else today's focus).")] string? taskId = null) =>
        Guard(() => Store.UpdateAsync(b =>
        {
            var now = Time.GetUtcNow();
            var actor = ActorOf(server);
            Guid? id = taskId is null ? null : ParseId(taskId);
            switch ((action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "start":
                    b.StartFocus(id, actor, now);
                    break;
                case "pause":
                    b.PauseFocus(actor, now);
                    break;
                case "resume":
                    b.ResumeFocus(actor, now);
                    break;
                case "skip":
                    b.SkipFocus(actor, now);
                    break;
                case "reset":
                    b.ResetFocus(actor, now);
                    break;
                case "next":
                    b.StartNextFocus(id ?? Agenda.Build(b, now, recentNoteCount: 0).Focus?.Item.Id, actor, now);
                    break;
                default:
                    throw new ArgumentException("action is start, pause, resume, skip, reset or next.", nameof(action));
            }

            return Wire.Pomodoro(b, now);
        }));

    [McpServerTool(Name = "get_plan", ReadOnly = true), Description("The plan: blocks of time planned on tasks in the days shown, deadlines in them, the tasks to do that aren't planned yet (and those put off), energy per day, and the Plan view's settings (work hours, work days). Use plan_task to plan or unplan a task.")]
    public Task<PlanDto> GetPlan(
        [Description("First day, yyyy-MM-dd in the user's time zone (default: today).")] string? from = null,
        [Description("How many days (1 to 31; default 7).")] int? days = null,
        [Description("Group (tab) name or id, e.g. 'Work'. Omit for every group.")] string? group = null) =>
        Guard(() => Store.ReadAsync(b => PlanEndpoints.Build(b, from, days, ResolveGroup(b, group), Time.GetUtcNow(), Options.TimeZone, Settings)));

    [McpServerTool(Name = "create_label"), Description("Add a label to the user's curated set (name and color), e.g. 'Deep work'. Only when they ask for one; update_task's labels also make unknown ones.")]
    public Task<LabelDto> CreateLabel(McpServer server, [Description("The label's name, e.g. 'Deep work'.")] string name, [Description("A color as #rrggbb (optional; one is picked).")] string? color = null) =>
        Guard(() => Store.UpdateAsync(b =>
        {
            var label = b.DefineLabel(name, string.IsNullOrWhiteSpace(color) ? null : color.Trim(), ActorOf(server), Time.GetUtcNow());
            return new LabelDto(label.Name, label.Color);
        }));

    [McpServerTool(Name = "update_label"), Description("Rename a label or change its color (every task with it follows). Labels can't be deleted by an agent: the user does that in the app.")]
    public Task<LabelDto> UpdateLabel(
        McpServer server,
        [Description("The label's current name (from list_labels).")] string name,
        [Description("The new name; omit to keep it.")] string? newName = null,
        [Description("A color as #rrggbb; omit to keep it.")] string? color = null) =>
        Guard(() => Store.UpdateAsync(b =>
        {
            var current = b.FindLabel(name) ?? throw new ArgumentException($"There's no label \"{name}\". Labels: {string.Join(", ", b.Labels.Select(l => l.Name))}.", nameof(name));
            var renamed = string.IsNullOrWhiteSpace(newName) ? current.Name : newName.Trim();
            b.UpdateLabel(current.Name, renamed, string.IsNullOrWhiteSpace(color) ? current.Color : color.Trim(), ActorOf(server), Time.GetUtcNow());
            var label = b.FindLabel(renamed)!;
            return new LabelDto(label.Name, label.Color);
        }));

    [McpServerTool(Name = "list_tags", ReadOnly = true), Description("The tags on open tasks, most used first, with how many tasks have each: reuse them instead of inventing near-duplicates.")]
    public Task<List<TagDto>> ListTags() => Guard(() => Store.ReadAsync(ApiEndpoints.Tags));

    [McpServerTool(Name = "board_history", ReadOnly = true), Description("Saved versions of the whole tasks folder, newest first (every change is versioned). task_history narrows it to one task.")]
    public Task<IReadOnlyList<VaultVersion>> BoardHistory([Description("At most this many versions (default 50).")] int limit = 50) =>
        Guard(async () =>
        {
            await Store.ReadAsync(_ => 0).ConfigureAwait(false);
            return await History.Required.LogAsync(null, Math.Clamp(limit, 1, 500)).ConfigureAwait(false);
        });

    [McpServerTool(Name = "get_task_version", ReadOnly = true), Description("A task's markdown file as it was in a version (from task_history or board_history), to see what changed before restore_task_version.")]
    public Task<string> GetTaskVersion([Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("A version id from task_history or board_history.")] string versionId) =>
        Guard(async () => await History.Required.TaskVersionAsync(ParseId(taskId), versionId).ConfigureAwait(false)
            ?? throw new ArgumentException("This task didn't exist in that version.", nameof(versionId)));

    [McpServerTool(Name = "export_board", ReadOnly = true), Description("The whole board as JSON (every group, task, note, reminder and time entry; the format every One Thing app reads). Large: prefer get_dashboard or search_tasks to answer questions.")]
    public Task<string> ExportBoard() => Guard(() => Store.ReadAsync(BoardSerializer.Serialize));

    [McpServerTool(Name = "read_attachment", ReadOnly = true), Description("The text of a task's attachment (its id is in get_task's attachments), e.g. a log or a plan attached earlier. Text files up to 1 MB; other files: their name and size only.")]
    public Task<string> ReadAttachment([Description("The task's id (a full id, from get_dashboard, search_tasks or list_tasks).")] string taskId, [Description("The attachment's id, from get_task.")] string attachmentId) =>
        Guard(async () =>
        {
            var (full, name) = await Links.Vault.GetAttachmentFileAsync(ParseId(taskId), ParseId(attachmentId)).ConfigureAwait(false);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new ArgumentException($"\"{name}\" is missing from the tasks folder.", nameof(attachmentId));
            }

            var bytes = info.Length <= MaxReadAttachmentBytes ? await File.ReadAllBytesAsync(full).ConfigureAwait(false) : null;
            return bytes is not null && LooksLikeText(bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : $"({name} is {(bytes is null ? "larger than 1 MB" : "not a text file")}: {info.Length} bytes. The user can open it from the task in the app.)";
        });

    /// <summary>UTF-8 text without NUL bytes (a binary file has them).</summary>
    private static bool LooksLikeText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    [McpServerTool(Name = "update_hook"), Description("Change a hook you added (by its id from list_hooks): only what you give changes. Like add_hook, the changed hook waits until the user allows it again: tell them so. The user's own hooks are theirs to change.")]
    public Task<Hooks.HookEntry> UpdateHook(
        McpServer server,
        [Description("The hook's id, from list_hooks (one you added).")] string hookId,
        [Description("A new event name, or * for every event.")] string? @event = null,
        [Description("What to run on Windows (PowerShell).")] string? powershell = null,
        [Description("What to run on Linux and macOS (bash).")] string? bash = null,
        [Description("Instead of a command: post the event as JSON to this http(s) URL.")] string? url = null,
        [Description("A short name for it.")] string? name = null,
        [Description("Stop it after this many seconds (at most 600).")] int? timeoutSec = null,
        [Description("Only for tasks in these groups.")] string[]? groups = null,
        [Description("Only for tasks with one of these tags.")] string[]? tags = null,
        [Description("Only for tasks with one of these labels.")] string[]? labels = null,
        [Description("Only when done by these: user, agent, vault, system, connector, or an agent's name.")] string[]? actors = null) =>
        Guard(() =>
        {
            var current = HookFile.Get(hookId);
            var hook = current.Hook;
            var match = hook.Match ?? new Hooks.HookMatch();
            var changed = new Hooks.HookSpec
            {
                Type = url is not null ? "http" : powershell is not null || bash is not null ? "command" : hook.Type,
                Powershell = powershell ?? hook.Powershell,
                Bash = bash ?? hook.Bash,
                Url = url ?? hook.Url,
                Headers = hook.Headers,
                Cwd = hook.Cwd,
                Env = hook.Env,
                Name = name ?? hook.Name,
                TimeoutSec = timeoutSec ?? hook.TimeoutSec,
                Match = new Hooks.HookMatch
                {
                    Groups = groups?.ToList() ?? match.Groups,
                    Tags = tags?.ToList() ?? match.Tags,
                    Labels = labels?.ToList() ?? match.Labels,
                    Actors = actors?.ToList() ?? match.Actors,
                },
            };
            return Task.FromResult(HookFile.Update(current.Id, @event, changed, AuthorOf(server), Time.GetUtcNow()).Redacted());
        });
}
