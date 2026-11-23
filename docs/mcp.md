# MCP tools

<!-- Made from the code by tests/OneThing.Cli.Tests/ReferenceDocsTests.cs. Don't edit by hand: run `ONETHING_WRITE_DOCS=1 dotnet test tests/OneThing.Cli.Tests --filter ReferenceDocsTests`. -->

One Thing's MCP server is `onething mcp` (stdio; works without the app running) or `http://127.0.0.1:5317/mcp` while
the app runs (with the API token). Setup for each AI app: [ai-connectors.md](ai-connectors.md). Changes are
attributed to the agent and versioned; there is deliberately no tool that deletes a task.

## What the server tells agents when they connect

> One Thing is the user's ADHD-friendly task list. Keep them focused: one clear next action beats a long list.
> - Start with get_dashboard (what to do now, what's waiting). Find tasks with search_tasks (#tag, label:x, group:work, is:done).
> - Respect the order of Do now (the user arranged it); when they say what matters most, put_first (arrange_now sets the whole order). plan_task blocks time for a task; get_plan shows the plan.
> - Capture work as tasks with create_task (capture_task takes the user's quick-add words: !!, due:fri, #tag, @tomorrow); break big work into subtasks (parentId) or ordered steps (add_steps).
> - Log progress with add_note ("did X, next Y"); defer with schedule_next_action (or wait_for_task) instead of leaving stale tasks in Now; clear_snooze brings one back.
> - Groups are tabs such as Work and Personal (list_groups); make one with create_group only when the user asks (an unknown group name is an error, never a new group). Tags are free-form; labels are a curated colored set (list_labels).
> - Never delete (there is no tool for it): complete_task, archive_task or move_task instead. Every change is versioned (task_history, board_history, restore_task_version). Settings and extras are the user's, in the app.
> - Top-level tasks are cards on a board (inbox, next, doing; move_card). Time work with start_timer/stop_timer or log_time (focus_timer runs a Pomodoro session, set_focus_lengths sets how long); get_time_report says where the time went. Finished tasks can be put away with archive_task.
> - Every task has a link (onething://task/&lt;id&gt;, in get_task): give it to the user; clicking it opens the task in the app.
> - Hooks (add_hook) run commands when things happen; one you add waits until the user allows it: tell them so.
> - Tasks are markdown files in a folder the user may also open in Obsidian; vault_info explains the format.

## Tools

| Tool | What it does |
| --- | --- |
| [`get_dashboard`](#get_dashboard) | What the user should do now, what is waiting (with wake times), per-workstream overview, recent notes, what got done today, focus timer state, and energy (what there is in a day and what today's plan takes). |
| [`list_tasks`](#list_tasks) | Full task tree (optionally for one group), including ids needed by the other tools. |
| [`get_tree`](#get_tree) | The task tree as the user sees it in Tasks, in one call: rows in order with their depth (under all groups, a row per group first), each parent's progress (doneCount/totalCount count the tasks without subtasks under it), and titles as shown (fullTitle has the whole title). |
| [`search_tasks`](#search_tasks) | Find tasks and subtasks. |
| [`get_task`](#get_task) | One task with its subtasks, reminders, notes, tags, labels, attachments, its markdown file, and its link (onething://task/&lt;id&gt;: give it to the user, clicking it opens the task in the app). |
| [`create_task`](#create_task) | Create a task (or a subtask when parentId is set). |
| [`add_steps`](#add_steps) | Add ordered steps under a task. |
| [`add_note`](#add_note) | Log progress on a task ("did X, next Y"). |
| [`schedule_next_action`](#schedule_next_action) | Defer a task until later (it drops to Waiting) and, by default, remind the user when it is time. |
| [`plan_task`](#plan_task) | Plan a block of time to work on a task (it shows in the Plan view, and becomes the thing to do now while the block is on); without a start, takes it off the plan. |
| [`set_daily_energy`](#set_daily_energy) | How much energy the user has in a day (1 to 100 ⚡; 10 unless set). |
| [`wait_for_task`](#wait_for_task) | Defer a task until another task is done: it waits (drops to Waiting) and comes back with a reminder when that one is completed. |
| [`add_reminder`](#add_reminder) | Add a reminder without deferring the task. |
| [`complete_task`](#complete_task) | Mark a task done (subtasks too). |
| [`reopen_task`](#reopen_task) | Undo a completion. |
| [`update_task`](#update_task) | Change title, details, priority, deadline, tags, labels, icon, color, how it repeats or its energy (tags/labels replace the current ones when given; an empty icon, color or repeat, or energy 0, removes it). |
| [`move_task`](#move_task) | Reorganize: put a task under another task (parentId), back at the top level (toTopLevel, optionally in a group), or into another group. index alone reorders a subtask among its siblings. |
| [`put_first`](#put_first) | Put tasks at the top of Do now, in this order; the first becomes the focus (only a reminder going off later comes above them). |
| [`list_labels`](#list_labels) | The curated labels (name and color) the user picks from. |
| [`attach_text`](#attach_text) | Attach a text file (log excerpt, plan, summary…) to a task. |
| [`get_rich_html`](#get_rich_html) | The task's rich HTML version (tables, layouts, colors), if it has one. |
| [`set_rich_html`](#set_rich_html) | Save a rich HTML version of a top-level task when markdown isn't enough (tables, dashboards, colors). |
| [`vault_info`](#vault_info) | Where the markdown files live, files that need fixing, and the file format guide (for agents that edit the files directly). |
| [`task_history`](#task_history) | Saved versions of a task's file, newest first (every change is versioned). |
| [`restore_task_version`](#restore_task_version) | Put a task back the way it was in an earlier version (from task_history). |
| [`get_report`](#get_report) | Timeline report (newest first) for one task or the whole board. |
| [`move_card`](#move_card) | Move a top-level task to a board column: inbox (just captured), next (ready), or doing (being worked on). |
| [`archive_task`](#archive_task) | Put a finished top-level task away: it leaves every list but is kept (search is:archived). |
| [`unarchive_task`](#unarchive_task) | Bring an archived task back to the done list. |
| [`start_timer`](#start_timer) | Start timing work on a task (any other timer stops; one runs at a time). |
| [`stop_timer`](#stop_timer) | Stop the timer (whatever task it is on). |
| [`log_time`](#log_time) | Record time already spent on a task (e.g. a meeting): when it started and for how many minutes (at most 24 hours). |
| [`get_time_report`](#get_time_report) | Where the time went between two local dates (yyyy-MM-dd, inclusive; default the last 4 weeks): seconds tracked per day, group and task, focus sessions, tasks done, streaks, and a timeline. |
| [`list_groups`](#list_groups) | The groups (tabs, e.g. |
| [`create_group`](#create_group) | Make a new group (a tab, e.g. 'Garden'), for tasks that belong together. |
| [`update_group`](#update_group) | Rename a group or change its color (its tasks stay in it). |
| [`list_hooks`](#list_hooks) | The hooks set up on this computer: what runs (a command or a web address) when something happens in One Thing, and whether each is on, off or waiting for approval. |
| [`add_hook`](#add_hook) | Add a hook: run a command (PowerShell on Windows, bash on Linux/macOS; the event comes as JSON on stdin) or post the event to a URL when something happens. |
| [`set_hook_enabled`](#set_hook_enabled) | Switch a hook on or off (by its id from list_hooks). |
| [`remove_hook`](#remove_hook) | Remove a hook you added (by its id from list_hooks). |
| [`capture_task`](#capture_task) | Capture a task the way the user would type it in quick add: "Renew passport !! due:7d #admin @tomorrow" (! high, !! critical, !low; @2h or @tomorrow defers it; due:3d or due:2026-02-01; #tag). |
| [`dismiss_reminder`](#dismiss_reminder) | Dismiss one of a task's reminders (its id is in get_task's reminders): it won't go off. |
| [`clear_snooze`](#clear_snooze) | Bring a deferred (snoozed or waiting) task back to Do now: the opposite of schedule_next_action and wait_for_task. |
| [`update_note`](#update_note) | Correct a progress note you wrote on a task (its id is in get_task's notes). |
| [`arrange_now`](#arrange_now) | Set the whole order of Do now (the first becomes the focus), as the user arranged it. |
| [`reorder_task`](#reorder_task) | Move a task next to a sibling (a subtask under the same parent, or a task in the same board column): right before beforeTaskId, or right after afterTaskId. |
| [`update_time_entry`](#update_time_entry) | Correct time logged on a task (an entry's id is in get_task's timeEntries): when it started and how long (at most 24 hours). |
| [`focus_timer`](#focus_timer) | The focus (Pomodoro) timer: start a focus session (on a task, timed as focus time on it), pause, resume, skip to the next phase, reset, or next (start the next session). |
| [`set_focus_lengths`](#set_focus_lengths) | How long the focus timer's sessions and breaks last, in minutes, and how many sessions come before a long break (25, 5, 15 and 4 unless set). |
| [`get_plan`](#get_plan) | The plan: blocks of time planned on tasks in the days shown, deadlines in them, the tasks to do that aren't planned yet (and those put off), energy per day, and the Plan view's settings (work hours, work days). |
| [`create_label`](#create_label) | Add a label to the user's curated set (name and color), e.g. 'Deep work'. |
| [`update_label`](#update_label) | Rename a label or change its color (every task with it follows). |
| [`list_tags`](#list_tags) | The tags on open tasks, most used first, with how many tasks have each: reuse them instead of inventing near-duplicates. |
| [`board_history`](#board_history) | Saved versions of the whole tasks folder, newest first (every change is versioned). task_history narrows it to one task. |
| [`get_task_version`](#get_task_version) | A task's markdown file as it was in a version (from task_history or board_history), to see what changed before restore_task_version. |
| [`export_board`](#export_board) | The whole board as JSON (every group, task, note, reminder and time entry; the format every One Thing app reads). |
| [`read_attachment`](#read_attachment) | The text of a task's attachment (its id is in get_task's attachments), e.g. a log or a plan attached earlier. |
| [`update_hook`](#update_hook) | Change a hook you added (by its id from list_hooks): only what you give changes. |

### `get_dashboard`

What the user should do now, what is waiting (with wake times), per-workstream overview, recent notes, what got done today, focus timer state, and energy (what there is in a day and what today's plan takes). *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `group` | text | optional | Optional group (tab) name or id, e.g. 'work' or 'personal'. |
| `query` | text | optional | Optional filter in search syntax, e.g. '#release' or 'label:"deep work"'. |

### `list_tasks`

Full task tree (optionally for one group), including ids needed by the other tools. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `group` | text | optional | Group (tab) name or id, e.g. 'Work'. Omit for every group. |
| `includeDone` | true/false | optional | Include finished tasks. |

### `get_tree`

The task tree as the user sees it in Tasks, in one call: rows in order with their depth (under all groups, a row per group first), each parent's progress (doneCount/totalCount count the tasks without subtasks under it), and titles as shown (fullTitle has the whole title). Narrow it to one group, one branch (underTaskId), what can be done now or what's waiting, or a few levels. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `group` | text | optional | Group (tab) name or id, e.g. 'Work'. Omit for every group (one group row each). |
| `underTaskId` | text | optional | Only what's under this task (a full id); the path then says where it is. |
| `show` | text | optional | todo (default: everything not finished), now (what can be done now, with its parents), waiting (snoozed, waiting for another task, or after an unfinished step, with their parents), or all (finished tasks too). |
| `depth` | integer | optional | How many levels of tasks (1: only the top level). Omit for all. |

### `search_tasks`

Find tasks and subtasks. Words match title or details; #tag (nested tags too), label:name, group:name, is:open|is:done narrow it. Tags and labels are inherited by subtasks. Quote multi-word values: label:"deep work". Open tasks only unless the query has is:. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `query` | text | required | Search syntax: words, #tag, label:"deep work", group:work, is:open, is:done, is:archived. |

### `get_task`

One task with its subtasks, reminders, notes, tags, labels, attachments, its markdown file, and its link (onething://task/&lt;id&gt;: give it to the user, clicking it opens the task in the app). *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `create_task`

Create a task (or a subtask when parentId is set). Use add_steps for ordered rollout steps.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `title` | text | required | What to do, in the user's words. |
| `parentId` | text | optional | Parent task id to create a subtask. |
| `group` | text | optional | Group (tab) name or id for top-level tasks. Defaults to the first group. |
| `priority` | text | optional | low \| normal \| high \| critical |
| `details` | text | optional | Longer notes about the task (markdown). |
| `deadline` | date-time (ISO 8601) | optional | ISO-8601 deadline. |
| `deferMinutes` | integer | optional | Defer the task (and remind) this many minutes from now. |
| `tags` | list of text | optional | Free-form tags without '#', e.g. ['release', 'infra/k8s']. |
| `labels` | list of text | optional | Curated labels, e.g. ['Deep work']. Unknown labels are created. |
| `repeat` | text | optional | How it repeats, e.g. "every weekday", "every 2 weeks on Monday, Thursday", "every month on the last Friday" (+ "when done", "until 2026-12-31", "for 5 times"). Completing it then moves it to its next time. |
| `energy` | integer | optional | How much energy it takes per hour: 1 (light), 2 or 3 (draining). Omit when you don't know. |

### `add_steps`

Add ordered steps under a task. Only the first open step is actionable; finishing one unlocks the next after stepDelayHours.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `parentId` | text | required | The task the steps go under (a full id). |
| `steps` | list of text | required | The steps, in order, e.g. ['Ring 0', 'Ring 1']. |
| `stepDelayHours` | number | optional | Hours between finishing one step and the next one coming up (e.g. 24 for a rollout); default: the delay the task already has (none for a new one). |

### `add_note`

Log progress on a task ("did X, next Y"). Shows in recent notes and the report timeline.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `text` | text | required | What happened, short and in past tense, with the next step: "Deployed ring 0, next: check dashboards tomorrow". |
| `sourceUrl` | text | optional | A web page the note is about (http/https). |
| `sourceTitle` | text | optional | That page's title. |

### `schedule_next_action`

Defer a task until later (it drops to Waiting) and, by default, remind the user when it is time.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `inMinutes` | integer | optional | Minutes from now. |
| `at` | date-time (ISO 8601) | optional | Exact ISO-8601 time (alternative to inMinutes). |
| `notify` | true/false | optional | Remind the user when it is time (default true). |
| `message` | text | optional | The reminder's text (default: the task's title). |
| `when` | text | optional | Or in words, in the user's time zone: 3d, 2 weeks, fri 14:00, next week, weekend, tonight, 9am, 2026-03-01 14:00. |

### `plan_task`

Plan a block of time to work on a task (it shows in the Plan view, and becomes the thing to do now while the block is on); without a start, takes it off the plan.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `start` | date-time (ISO 8601) | optional | ISO-8601 start of the block; omit to take it off the plan. |
| `minutes` | integer | optional | How long, in minutes (5 to 720; default: what it had, else 30). |

### `set_daily_energy`

How much energy the user has in a day (1 to 100 ⚡; 10 unless set). A day's plan takes the hours of its blocks times each task's energy (1 to 3 an hour; a task without one counts as 1): use it to warn about a day that's too full. Omit perDay to go back to 10. Returns what there is and what today's plan takes.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `perDay` | integer | optional | Energy in a day, 1 to 100; omit for the default (10). |

### `wait_for_task`

Defer a task until another task is done: it waits (drops to Waiting) and comes back with a reminder when that one is completed.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `afterTaskId` | text | required | The task it waits for. |

### `add_reminder`

Add a reminder without deferring the task.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `message` | text | required | The reminder's text. |
| `inMinutes` | integer | optional | Minutes from now. |
| `at` | date-time (ISO 8601) | optional | Exact ISO-8601 time (alternative to inMinutes). |

### `complete_task`

Mark a task done (subtasks too). Completing a rollout step schedules the next step; a repeating task moves on to its next time instead (reopen_task undoes that), so don't call it twice for one repeating task.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `reopen_task`

Undo a completion. *(idempotent)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `update_task`

Change title, details, priority, deadline, tags, labels, icon, color, how it repeats or its energy (tags/labels replace the current ones when given; an empty icon, color or repeat, or energy 0, removes it).

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `title` | text | optional | New title. |
| `details` | text | optional | New details (markdown); replaces what it had. |
| `priority` | text | optional | low \| normal \| high \| critical |
| `deadline` | date-time (ISO 8601) | optional | New ISO-8601 deadline. |
| `tags` | list of text | optional | Tags without '#': replace the current ones. |
| `labels` | list of text | optional | Curated labels: replace the current ones (unknown labels are created). |
| `icon` | text | optional | An emoji shown before the title, e.g. 🚀; empty removes it. |
| `color` | text | optional | A color as #rrggbb, e.g. #3b82f6; empty removes it (the first label's color shows then). |
| `repeat` | text | optional | How it repeats, e.g. "every weekday", "every 2 weeks on Monday, Thursday", "every month on the last Friday" (+ "when done", "until 2026-12-31", "for 5 times"). Empty: stops repeating. |
| `energy` | integer | optional | Energy it takes per hour: 1 (light), 2 or 3 (draining); 0 removes it. |

### `move_task`

Reorganize: put a task under another task (parentId), back at the top level (toTopLevel, optionally in a group), or into another group. index alone reorders a subtask among its siblings.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `parentId` | text | optional | Put it under this task (a full id). |
| `group` | text | optional | Group (tab) name or id, e.g. 'Work': moves it to that group (a subtask becomes a top-level task there). |
| `index` | integer | optional | Position among its siblings (0: first). Alone, it reorders a subtask under its parent; with parentId or toTopLevel, among its new siblings. |
| `toTopLevel` | true/false | optional | Take it out from under its parent, to the top level. |

### `put_first`

Put tasks at the top of Do now, in this order; the first becomes the focus (only a reminder going off later comes above them). Use it when the user says what matters most ("do X first").

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskIds` | list of text | required | Task ids, most important first. |

### `list_labels`

The curated labels (name and color) the user picks from. *(read-only)*

### `attach_text`

Attach a text file (log excerpt, plan, summary…) to a task. It is saved in the vault and linked from the task.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `fileName` | text | required | File name with extension, e.g. 'timeline.md'. |
| `text` | text | required | The file's text (up to 1 MB). |

### `get_rich_html`

The task's rich HTML version (tables, layouts, colors), if it has one. Only top-level tasks have one. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `set_rich_html`

Save a rich HTML version of a top-level task when markdown isn't enough (tables, dashboards, colors). Scripts never run. Empty html removes it.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `html` | text | required | The HTML (scripts never run); empty removes it. |

### `vault_info`

Where the markdown files live, files that need fixing, and the file format guide (for agents that edit the files directly). *(read-only)*

### `task_history`

Saved versions of a task's file, newest first (every change is versioned). Use restore_task_version to go back. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `restore_task_version`

Put a task back the way it was in an earlier version (from task_history). The current state stays in the history, so this can be undone.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `versionId` | text | required | A version id from task_history. |

### `get_report`

Timeline report (newest first) for one task or the whole board. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | optional | Only this task (a full id); omit for the whole board. |
| `limit` | integer | optional | At most this many entries (default 100). |

### `move_card`

Move a top-level task to a board column: inbox (just captured), next (ready), or doing (being worked on). Finish a task with complete_task to move it to done.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `stage` | text | required | inbox, next or doing |

### `archive_task`

Put a finished top-level task away: it leaves every list but is kept (search is:archived). Only done tasks can be archived. *(idempotent)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `unarchive_task`

Bring an archived task back to the done list. *(idempotent)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `start_timer`

Start timing work on a task (any other timer stops; one runs at a time). The task's card moves to doing.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `stop_timer`

Stop the timer (whatever task it is on). *(idempotent)*

### `log_time`

Record time already spent on a task (e.g. a meeting): when it started and for how many minutes (at most 24 hours).

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `start` | date-time (ISO 8601) | required | When it started (ISO-8601). |
| `minutes` | integer | required | How long, in minutes (at most 24 hours). |

### `get_time_report`

Where the time went between two local dates (yyyy-MM-dd, inclusive; default the last 4 weeks): seconds tracked per day, group and task, focus sessions, tasks done, streaks, and a timeline. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `from` | text | optional | First day, yyyy-MM-dd (default: 4 weeks ago). |
| `to` | text | optional | Last day, yyyy-MM-dd, inclusive (default: today). |
| `group` | text | optional | Group (tab) name or id, e.g. 'Work'. Omit for every group. |

### `list_groups`

The groups (tabs, e.g. Work and Personal) with their ids, colors and how many tasks are to do now and waiting in each. *(read-only)*

### `create_group`

Make a new group (a tab, e.g. 'Garden'), for tasks that belong together. Only when the user asks for one: an unknown group name elsewhere is an error, never a new group.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | text | required | The group's name (up to 60 characters), e.g. 'Garden'. |
| `color` | text | optional | A color as #rrggbb, e.g. #22c55e (optional). |

### `update_group`

Rename a group or change its color (its tasks stay in it). Groups can't be deleted by an agent: the user does that in the app.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `group` | text | required | The group's name or id (from list_groups). |
| `name` | text | optional | The new name; omit to keep it. |
| `color` | text | optional | A color as #rrggbb; empty removes it; omit to keep it. |

### `list_hooks`

The hooks set up on this computer: what runs (a command or a web address) when something happens in One Thing, and whether each is on, off or waiting for approval. *(read-only)*

### `add_hook`

Add a hook: run a command (PowerShell on Windows, bash on Linux/macOS; the event comes as JSON on stdin) or post the event to a URL when something happens. It doesn't run until the user allows it: One Thing asks them (a notification or a prompt in the app; also Settings › Hooks, or ot hooks approve); tell them so. Events: taskCreated, taskUpdated, taskCompleted, taskReopened, taskDeleted, taskScheduled, taskMoved, taskArchived, noteAdded, reminderAdded, reminderDue, reminderDismissed, attachmentAdded, attachmentRemoved, labelChanged, groupChanged, timeLogged, focusStarted, focusEnded, breakEnded, or * for every event.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `event` | text | required | When it runs: an event name, or * for every event. |
| `powershell` | text | optional | What to run on Windows (PowerShell). Read the event with [Console]::In.ReadToEnd() \| ConvertFrom-Json. |
| `bash` | text | optional | What to run on Linux and macOS (bash). The event is on stdin, e.g. jq -r .task.title. |
| `url` | text | optional | Instead of a command: post the event as JSON to this http(s) URL. |
| `name` | text | optional | A short name for it. |
| `timeoutSec` | integer | optional | Stop it after this many seconds (default 30, at most 600). |
| `groups` | list of text | optional | Only for tasks in these groups. |
| `tags` | list of text | optional | Only for tasks with one of these tags. |
| `labels` | list of text | optional | Only for tasks with one of these labels. |
| `actors` | list of text | optional | Only when done by these: user, agent, vault, system, connector, or an agent's name. |

### `set_hook_enabled`

Switch a hook on or off (by its id from list_hooks). You can only switch hooks you added yourself; the user's own hooks are theirs to switch. A hook waiting for approval still needs the user's approval to run.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `hookId` | text | required | The hook's id, from list_hooks (one you added). |
| `enabled` | true/false | required | On (true) or off (false). |

### `remove_hook`

Remove a hook you added (by its id from list_hooks). The user's own hooks can only be removed by them, in Settings › Hooks. *(removes something)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `hookId` | text | required | The hook's id, from list_hooks (one you added). |

### `capture_task`

Capture a task the way the user would type it in quick add: "Renew passport !! due:7d #admin @tomorrow" (! high, !! critical, !low; @2h or @tomorrow defers it; due:3d or due:2026-02-01; #tag). Use create_task when you already have the fields.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `text` | text | required | The words, quick-capture syntax included, e.g. 'Call the bank !! due:fri #money'. |
| `group` | text | optional | Group (tab) name or id for a top-level task. Defaults to the first group. |
| `parentId` | text | optional | Parent task id to capture a subtask. |

### `dismiss_reminder`

Dismiss one of a task's reminders (its id is in get_task's reminders): it won't go off. The task stays as it is. *(idempotent)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `reminderId` | text | required | The reminder's id, from get_task. |

### `clear_snooze`

Bring a deferred (snoozed or waiting) task back to Do now: the opposite of schedule_next_action and wait_for_task. *(idempotent)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |

### `update_note`

Correct a progress note you wrote on a task (its id is in get_task's notes). Notes by the user or another agent are theirs: add a new note instead. The note keeps its time and author; the change is versioned.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `noteId` | text | required | The note's id, from get_task. |
| `text` | text | required | The note's new text. |

### `arrange_now`

Set the whole order of Do now (the first becomes the focus), as the user arranged it. To put a few tasks on top and leave the rest, use put_first.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskIds` | list of text | required | Task ids in the order wanted, first at the top. |

### `reorder_task`

Move a task next to a sibling (a subtask under the same parent, or a task in the same board column): right before beforeTaskId, or right after afterTaskId. Neither: it goes last.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `beforeTaskId` | text | optional | Put it right before this sibling (a full id). |
| `afterTaskId` | text | optional | Or right after this sibling (a full id). |

### `update_time_entry`

Correct time logged on a task (an entry's id is in get_task's timeEntries): when it started and how long (at most 24 hours). Logged time can't be removed by an agent.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `entryId` | text | required | The time entry's id, from get_task. |
| `start` | date-time (ISO 8601) | required | When it started (ISO-8601). |
| `minutes` | integer | required | How long, in minutes (at most 24 hours). |

### `focus_timer`

The focus (Pomodoro) timer: start a focus session (on a task, timed as focus time on it), pause, resume, skip to the next phase, reset, or next (start the next session). Its state is also in get_dashboard (pomodoro).

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `action` | text | required | start, pause, resume, skip, reset or next. |
| `taskId` | text | optional | For start and next: the task to focus on (a full id). Omit to focus on nothing in particular (next: the last session's task, else today's focus). |

### `set_focus_lengths`

How long the focus timer's sessions and breaks last, in minutes, and how many sessions come before a long break (25, 5, 15 and 4 unless set). Only the lengths given change; with none it just returns them. A new length counts from the next session. They're also in get_dashboard (pomodoro).

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `focusMinutes` | integer | optional | A focus session, 1 to 180 minutes; omit to leave it. |
| `shortBreakMinutes` | integer | optional | A short break, 1 to 60 minutes; omit to leave it. |
| `longBreakMinutes` | integer | optional | A long break, 1 to 120 minutes; omit to leave it. |
| `focusesBeforeLongBreak` | integer | optional | Focus sessions before a long break, 1 to 12; omit to leave it. |

### `get_plan`

The plan: blocks of time planned on tasks in the days shown, deadlines in them, the tasks to do that aren't planned yet (and those put off), energy per day, and the Plan view's settings (work hours, work days). Use plan_task to plan or unplan a task. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `from` | text | optional | First day, yyyy-MM-dd in the user's time zone (default: today). |
| `days` | integer | optional | How many days (1 to 31; default 7). |
| `group` | text | optional | Group (tab) name or id, e.g. 'Work'. Omit for every group. |

### `create_label`

Add a label to the user's curated set (name and color), e.g. 'Deep work'. Only when they ask for one; update_task's labels also make unknown ones.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | text | required | The label's name, e.g. 'Deep work'. |
| `color` | text | optional | A color as #rrggbb (optional; one is picked). |

### `update_label`

Rename a label or change its color (every task with it follows). Labels can't be deleted by an agent: the user does that in the app.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `name` | text | required | The label's current name (from list_labels). |
| `newName` | text | optional | The new name; omit to keep it. |
| `color` | text | optional | A color as #rrggbb; omit to keep it. |

### `list_tags`

The tags on open tasks, most used first, with how many tasks have each: reuse them instead of inventing near-duplicates. *(read-only)*

### `board_history`

Saved versions of the whole tasks folder, newest first (every change is versioned). task_history narrows it to one task. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `limit` | integer | optional | At most this many versions (default 50). |

### `get_task_version`

A task's markdown file as it was in a version (from task_history or board_history), to see what changed before restore_task_version. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `versionId` | text | required | A version id from task_history or board_history. |

### `export_board`

The whole board as JSON (every group, task, note, reminder and time entry; the format every One Thing app reads). Large: prefer get_dashboard or search_tasks to answer questions. *(read-only)*

### `read_attachment`

The text of a task's attachment (its id is in get_task's attachments), e.g. a log or a plan attached earlier. Text files up to 1 MB; other files: their name and size only. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `taskId` | text | required | The task's id (a full id, from get_dashboard, search_tasks or list_tasks). |
| `attachmentId` | text | required | The attachment's id, from get_task. |

### `update_hook`

Change a hook you added (by its id from list_hooks): only what you give changes. Like add_hook, the changed hook waits until the user allows it again: tell them so. The user's own hooks are theirs to change.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `hookId` | text | required | The hook's id, from list_hooks (one you added). |
| `event` | text | optional | A new event name, or * for every event. |
| `powershell` | text | optional | What to run on Windows (PowerShell). |
| `bash` | text | optional | What to run on Linux and macOS (bash). |
| `url` | text | optional | Instead of a command: post the event as JSON to this http(s) URL. |
| `name` | text | optional | A short name for it. |
| `timeoutSec` | integer | optional | Stop it after this many seconds (at most 600). |
| `groups` | list of text | optional | Only for tasks in these groups. |
| `tags` | list of text | optional | Only for tasks with one of these tags. |
| `labels` | list of text | optional | Only for tasks with one of these labels. |
| `actors` | list of text | optional | Only when done by these: user, agent, vault, system, connector, or an agent's name. |
