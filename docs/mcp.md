# MCP tools

<!-- Made from the code by tests/TodoTracker.Cli.Tests/ReferenceDocsTests.cs. Don't edit by hand: run `TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests`. -->

Todo Tracker's MCP server is `tt mcp` (stdio; works without the app running) or `http://127.0.0.1:5317/mcp` while
the app runs (with the API token). Setup for each AI app: [ai-connectors.md](ai-connectors.md). Changes are
attributed to the agent and versioned; there is deliberately no tool that deletes a task.

## What the server tells agents when they connect

> Todo Tracker is the user's ADHD-friendly task list. Keep them focused: one clear next action beats a long list.
> - Start with get_dashboard (what to do now, what's waiting). Find tasks with search_tasks (#tag, label:x, group:work, is:done).
> - Respect the order of Do now (the user arranged it); when they say what matters most, put_first. plan_task blocks time for a task.
> - Capture work as tasks with create_task; break big work into subtasks (parentId) or ordered steps (add_steps).
> - Log progress with add_note ("did X, next Y"); defer with schedule_next_action (or wait_for_task) instead of leaving stale tasks in Now.
> - Groups are tabs such as Work and Personal. Tags are free-form; labels are a curated colored set (list_labels).
> - Never delete: complete_task or move_task instead. Every change is versioned (task_history, restore_task_version).
> - Top-level tasks are cards on a board (inbox, next, doing; move_card). Time work with start_timer/stop_timer or log_time; get_time_report says where the time went. Finished tasks can be put away with archive_task.
> - Every task has a link (todotracker://task/&lt;id&gt;, in get_task): give it to the user; clicking it opens the task in the app.
> - Hooks (add_hook) run commands when things happen; one you add waits until the user allows it: tell them so.
> - Tasks are markdown files in a folder the user may also open in Obsidian; vault_info explains the format.

## Tools

| Tool | What it does |
| --- | --- |
| [`get_dashboard`](#get_dashboard) | What the user should do now, what is waiting (with wake times), per-workstream overview, recent notes, what got done today, focus timer state, and energy (what there is in a day and what today's plan takes). |
| [`list_tasks`](#list_tasks) | Full task tree (optionally for one group), including ids needed by the other tools. |
| [`search_tasks`](#search_tasks) | Find tasks and subtasks. |
| [`get_task`](#get_task) | One task with its subtasks, reminders, notes, tags, labels, attachments, its markdown file, and its link (todotracker://task/&lt;id&gt;: give it to the user, clicking it opens the task in the app). |
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
| [`list_hooks`](#list_hooks) | The hooks set up on this computer: what runs (a command or a web address) when something happens in Todo Tracker, and whether each is on, off or waiting for approval. |
| [`add_hook`](#add_hook) | Add a hook: run a command (PowerShell on Windows, bash on Linux/macOS; the event comes as JSON on stdin) or post the event to a URL when something happens. |
| [`set_hook_enabled`](#set_hook_enabled) | Switch a hook on or off (by its id from list_hooks). |
| [`remove_hook`](#remove_hook) | Remove a hook (by its id from list_hooks). |

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

### `search_tasks`

Find tasks and subtasks. Words match title or details; #tag (nested tags too), label:name, group:name, is:open|is:done narrow it. Tags and labels are inherited by subtasks. Quote multi-word values: label:"deep work". Open tasks only unless the query has is:. *(read-only)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `query` | text | required | Search syntax: words, #tag, label:"deep work", group:work, is:open, is:done, is:archived. |

### `get_task`

One task with its subtasks, reminders, notes, tags, labels, attachments, its markdown file, and its link (todotracker://task/&lt;id&gt;: give it to the user, clicking it opens the task in the app). *(read-only)*

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

Mark a task done (subtasks too). Completing a rollout step schedules the next step; a repeating task moves on to its next time instead (reopen_task undoes that). *(idempotent)*

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

### `list_hooks`

The hooks set up on this computer: what runs (a command or a web address) when something happens in Todo Tracker, and whether each is on, off or waiting for approval. *(read-only)*

### `add_hook`

Add a hook: run a command (PowerShell on Windows, bash on Linux/macOS; the event comes as JSON on stdin) or post the event to a URL when something happens. It doesn't run until the user allows it: Todo Tracker asks them (a notification or a prompt in the app; also Settings › Hooks, or tt hooks approve); tell them so. Events: taskCreated, taskUpdated, taskCompleted, taskReopened, taskDeleted, taskScheduled, taskMoved, taskArchived, noteAdded, reminderAdded, reminderDue, reminderDismissed, attachmentAdded, attachmentRemoved, labelChanged, groupChanged, timeLogged, focusStarted, focusEnded, breakEnded, or * for every event.

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

Switch a hook on or off (by its id from list_hooks). A hook waiting for approval still needs the user's approval to run.

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `hookId` | text | required | The hook's id, from list_hooks. |
| `enabled` | true/false | required | On (true) or off (false). |

### `remove_hook`

Remove a hook (by its id from list_hooks). *(removes something)*

| Parameter | Type | | Description |
| --- | --- | --- | --- |
| `hookId` | text | required | The hook's id, from list_hooks. |
