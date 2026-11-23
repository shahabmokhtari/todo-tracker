# The `ot` command

<!-- Made from the code by tests/OneThing.Cli.Tests/ReferenceDocsTests.cs. Don't edit by hand: run `ONETHING_WRITE_DOCS=1 dotnet test tests/OneThing.Cli.Tests --filter ReferenceDocsTests`. -->

`ot` reads and changes your tasks (the markdown files in your tasks folder) from a terminal or a script, without
the app running, and `onething mcp` is the MCP server for AI apps. `ot` is the short name of `onething`: both work.
They're in the `cli` folder next to the app on Windows, inside the Mac app (One Thing › Install Command Line Tool…
links them into `~/.local/bin`), in `~/.local/bin` with the Linux install, or `dotnet tool install -g OneThing.Cli`
(as `onething`) ([ai-connectors.md](ai-connectors.md#get-ot)).

`<task>` is an id (or its first 6+ characters, as `ot` prints them) or words from the title. Exit codes: `0` done,
`1` couldn't (the reason is on stderr), `2` wrong usage.

## Options for every command

| Option | What it does |
| --- | --- |
| `--json` | print JSON (same shapes as the REST API and MCP tools) |
| `--as <name>` | attribute changes to an agent, e.g. --as claude (or set ONETHING_AGENT) |
| `--vault <dir>` | use another tasks folder (default: the app's, or ONETHING_VAULT) |
| `--data <dir>` | use another app data folder (or ONETHING_DATA) |
| `--no-history` | don't save a version for this change |

## Commands

| Command | What it does |
| --- | --- |
| [`ot now`](#ot-now) | What to do now, and what is waiting |
| [`ot add`](#ot-add) | Add a task |
| [`ot list`](#ot-list) | Find tasks |
| [`ot show`](#ot-show) | A task with its subtasks, notes, file and link |
| [`ot open`](#ot-open) | Open a task in the app (its onething:// link) |
| [`ot done`](#ot-done) | Mark tasks done |
| [`ot reopen`](#ot-reopen) | Undo done |
| [`ot first`](#ot-first) | Put tasks at the top of Do now (the first becomes the focus) |
| [`ot note`](#ot-note) | Log progress ("did X, next Y"); - reads the text from stdin |
| [`ot snooze`](#ot-snooze) | Defer a task until later, or until another task is done (it moves to Waiting and reminds you) |
| [`ot remind`](#ot-remind) | Add a reminder without deferring the task, or dismiss one |
| [`ot edit`](#ot-edit) | Change a task |
| [`ot energy`](#ot-energy) | Energy today: what you have in a day and what today's plan takes |
| [`ot tag`](#ot-tag) | Add or remove tags |
| [`ot label`](#ot-label) | Add or remove labels (quote names with spaces) |
| [`ot move`](#ot-move) | Put a task under another, at the top level, or in another group; or next to a sibling |
| [`ot order`](#ot-order) | Arrange Do now in this order (the first is the focus) |
| [`ot steps`](#ot-steps) | Add ordered steps; finishing one unlocks the next after the delay |
| [`ot attach`](#ot-attach) | Copy a file into the tasks folder and link it |
| [`ot history`](#ot-history) | Saved versions, newest first: of the whole tasks folder, or of one task (--text prints that version) |
| [`ot restore`](#ot-restore) | Put a task back the way it was in a version |
| [`ot groups`](#ot-groups) | Groups (tabs) with counts |
| [`ot group`](#ot-group) | Make a group (tab), rename it or change its color |
| [`ot timeline`](#ot-timeline) | What happened, newest first: on one task (with its subtasks) or the whole board |
| [`ot labels`](#ot-labels) | The curated labels: list, add, rename, recolor |
| [`ot tags`](#ot-tags) | The tags on open tasks, most used first |
| [`ot vault`](#ot-vault) | Where the task files live, their format, or switch folders |
| [`ot mcp`](#ot-mcp) | Run the MCP server over stdio (for Claude, Copilot, VS Code…) |
| [`ot start`](#ot-start) | Start timing a task (any other timer stops) |
| [`ot stop`](#ot-stop) | Stop the timer |
| [`ot stage`](#ot-stage) | Move a task to a board column |
| [`ot archive`](#ot-archive) | Put finished tasks away (ot list is:archived finds them) |
| [`ot unarchive`](#ot-unarchive) | Bring an archived task back to the done list |
| [`ot time`](#ot-time) | Where the time went: tracked per task, focus sessions, tasks done |
| [`ot log`](#ot-log) | Record time already spent on a task, or correct an entry |
| [`ot focus`](#ot-focus) | The focus (Pomodoro) timer: what it's doing, or start, pause, resume, skip, reset |
| [`ot plan`](#ot-plan) | The plan: blocks of time on tasks |
| [`ot hooks`](#ot-hooks) | Run your own scripts or webhooks when something happens (hooks.json) |
| [`ot rich`](#ot-rich) | A task's rich HTML version (tables, colors): print it, save one from a file or stdin, or remove it |
| [`ot attachment`](#ot-attachment) | Save a copy of a task's attachment (default: here, by its name; - prints it) |
| [`ot export`](#ot-export) | The whole board as JSON (the format every app reads) |
| [`ot settings`](#ot-settings) | The app's settings: the theme, and where reminders are posted (Teams) |
| [`ot sync`](#ot-sync) | How syncing with your other computers is going; now syncs (while the app isn't running) |
| [`ot connectors`](#ot-connectors) | Notion, Microsoft To Do and GitHub Issues: their status; sync one now (while the app isn't running) |
| [`ot plugins`](#ot-plugins) | The extras (plugins) and whether each is on; switch one on or off |

### ot now

What to do now, and what is waiting.

```
ot [now] [filter] [-g group]
```

filter uses the list syntax, e.g. ot now #release  

### ot add

Add a task.

```
ot add <title…> [-g group] [--under task] [--details text] [--tag t]… [--label l]… [--due when] [--snooze when] [--energy 1|2|3] [--repeat rule] [--icon emoji] [--color #rrggbb]
```

Quick words in the title: ! high, !! critical, !low, @2h or @tomorrow (defer), due:3d or due:2026-02-01, #tag.  
Quote the title: shells treat # ! @ specially. Example: ot add 'Renew passport !! due:7d #admin'  
Or use options: ot add Renew passport --due 7d --tag admin  
-g takes a group that exists (ot groups); ot group add makes a new one.  

### ot list

Find tasks.

```
ot list [filter] [-g group] [--all]
```

Words match titles and details; #tag, label:name, group:name, is:open, is:done narrow it. Open tasks unless --all or is:.  

### ot show

A task with its subtasks, notes, file and link.

```
ot show <task>
```

### ot open

Open a task in the app (its onething:// link).

```
ot open <task>
```

ot show prints the link, to paste in a note, chat or mail.  

### ot done

Mark tasks done.

```
ot done <task> | ot done <id> <id>…
```

Several tasks at once by id (as ot prints them); words are one task's title.  

### ot reopen

Undo done.

```
ot reopen <task>
```

### ot first

Put tasks at the top of Do now (the first becomes the focus).

```
ot first <task> | ot first <id> <id>…
```

### ot note

Log progress ("did X, next Y"); - reads the text from stdin.

```
ot note <task> <text…|-> [--url https://… [--title page]] | ot note <task> --edit <note|last> <text…|->
```

--url: the web page the note is about. --edit corrects a note (its id from ot show --json, or last); with --as, only a note that agent wrote.  

### ot snooze

Defer a task until later, or until another task is done (it moves to Waiting and reminds you).

```
ot snooze <task> <when | after <task> | clear>
```

when: 45m, 2h, 3d, tomorrow, next week, fri 14:30, weekend, 2026-02-01, 2026-02-01T14:30 — or: after &lt;other task&gt;.  
clear brings it back to Do now.  

### ot remind

Add a reminder without deferring the task, or dismiss one.

```
ot remind <task> <when…> [--message text] | ot remind <task> --dismiss <reminder|all>
```

when: the same words as ot snooze (2h, tomorrow 9:00, fri 14:30). A reminder's id is in ot show --json.  

### ot edit

Change a task.

```
ot edit <task> [--title t] [--details d] [--priority low|normal|high|critical] [--due when|none] [--energy 1|2|3|none] [--repeat rule|none] [--icon emoji|none] [--color #rrggbb|none] [--sequential on|off] [--step-delay hours|none]
```

--energy: how much energy it takes per hour, 1 (light) to 3 (draining).  
--repeat: every weekday, every 2 weeks on Monday, Thursday, every month on the last Friday (+ when done, until 2026-12-31, for 5 times).  
--sequential on: its subtasks are steps, one at a time; --step-delay: hours between finishing one step and the next coming up.  

### ot energy

Energy today: what you have in a day and what today's plan takes.

```
ot energy [per-day | default]
```

A day's plan takes the hours of its blocks times each task's energy (1 to 3 an hour; none counts as 1). ot energy 8 sets 8 a day; ot energy default goes back to 10.  

### ot tag

Add or remove tags.

```
ot tag <task> [+]tag… -tag…
```

### ot label

Add or remove labels (quote names with spaces).

```
ot label <task> [+]label… -label…
```

### ot move

Put a task under another, at the top level, or in another group; or next to a sibling.

```
ot move <task> (--under task | --top | -g group) [--index n] | ot move <task> (--before task | --after task)
```

--before and --after reorder a task among its siblings (subtasks under one parent, or tasks in a column).  

### ot order

Arrange Do now in this order (the first is the focus).

```
ot order <task> <task>…
```

Ids as ot prints them. ot first puts some at the top and leaves the rest as they are.  

### ot steps

Add ordered steps; finishing one unlocks the next after the delay.

```
ot steps <task> <step>… [--delay hours]
```

### ot attach

Copy a file into the tasks folder and link it.

```
ot attach <task> <file>
```

### ot history

Saved versions, newest first: of the whole tasks folder, or of one task (--text prints that version).

```
ot history [<task> [--text version]] [--limit n]
```

Every change is saved as a version (it needs git). ot restore puts a task back.  

### ot restore

Put a task back the way it was in a version.

```
ot restore <task> <version>
```

### ot groups

Groups (tabs) with counts.

```
ot groups
```

ot group adds, renames and recolors them.  

### ot group

Make a group (tab), rename it or change its color.

```
ot group (add <name> [--color #rrggbb] | rename <group> <new name> | color <group> <#rrggbb | none>)
```

Quote a group name with spaces when it comes before more words: ot group rename "Side projects" Hobbies.  
Groups are deleted only in the app (its tasks move to another group there).  

### ot timeline

What happened, newest first: on one task (with its subtasks) or the whole board.

```
ot timeline [<task>] [--limit n]
```

Each entry says who did it: you, an agent (by name), a connector or the tasks folder.  

### ot labels

The curated labels: list, add, rename, recolor.

```
ot labels [add <name> [--color #rrggbb] | rename <label> <new name> | color <label> <#rrggbb>]
```

ot label &lt;task&gt; +name puts one on a task. Labels are deleted only in the app.  

### ot tags

The tags on open tasks, most used first.

```
ot tags
```

ot tag &lt;task&gt; +x -y changes a task's tags.  

### ot vault

Where the task files live, their format, or switch folders.

```
ot vault [guide | obsidian | use <folder|obsidian vault>]
```

use is for you, the person at the computer (not for AI agents); the app switches at its next start.  

### ot mcp

Run the MCP server over stdio (for Claude, Copilot, VS Code…).

```
onething mcp
```

See docs/ai-connectors.md for setup in each app.  

### ot start

Start timing a task (any other timer stops).

```
ot start <task>
```

The task's card moves to Doing. ot stop stops it.  

### ot stop

Stop the timer.

```
ot stop
```

### ot stage

Move a task to a board column.

```
ot stage <task> <inbox|next|doing>
```

Finish a task (ot done) to move it to Done.  

### ot archive

Put finished tasks away (ot list is:archived finds them).

```
ot archive <task> | ot archive --done-before <days>
```

--done-before 14 archives everything finished at least 14 days ago.  
There's no ot delete: archived tasks are kept (ot unarchive brings one back); deleting is in the app, for you.  

### ot unarchive

Bring an archived task back to the done list.

```
ot unarchive <task>
```

### ot time

Where the time went: tracked per task, focus sessions, tasks done.

```
ot time [--days n] [-g group]
```

Default: the last 28 days. --json prints the full report (per day, group, hour).  

### ot log

Record time already spent on a task, or correct an entry.

```
ot log <task> <duration> [--at when] | ot log <task> <duration> --edit <entry|last> [--at when]
```

duration: 45m, 1h30m, 1.5h or minutes (at most 24 hours). --at: when it started (default: it ended now). An entry's id is in ot show --json.  
A time entry is removed only in the app; --edit corrects one.  

### ot focus

The focus (Pomodoro) timer: what it's doing, or start, pause, resume, skip, reset.

```
ot focus [start [<task>] | pause | resume | skip | reset | next [<task>]]
```

start without a task focuses on nothing in particular; next starts the next session (on the last session's task, or today's focus).  

### ot plan

The plan: blocks of time on tasks.

```
ot plan [--from yyyy-mm-dd] [--days n] [-g group] | ot plan <task> <when…> [--minutes n] | ot plan <task> off | ot plan settings [options]
```

when: 2026-01-05T14:00, tomorrow 9:00, fri 14:30. --minutes: 5 to 720 (default what it had, else 30).  
settings [-g group] [--view day|week] [--hours work|full] [--work-hours 9:00-17:00] [--work-days 1-5] [--week-start mon|sun]: how the Plan view looks (days 1 = Monday … 7 = Sunday).  

### ot hooks

Run your own scripts or webhooks when something happens (hooks.json).

```
ot hooks [list | add <event> | edit <id> | enable <id> | disable <id> | approve <id> <version> | reject <id> <version> | remove <id> | test [event] | path]
```

add &lt;event&gt; (--powershell script | --bash script | --url https://…) [--name n] [--timeout seconds] [-g group]… [--tag t]… [--label l]… [--actor user|agent|…]…  
edit &lt;id&gt; [--event e] and any of the add options (only what you give changes).  
&lt;event&gt;: taskCompleted, taskCreated, reminderDue, noteAdded, focusEnded… or * for every event (see docs/hooks.md).  
A command gets the event as JSON on stdin. A hook an AI agent adds or changes (--as or ONETHING_AGENT) waits for your approval:  
approving is for you, the person at the computer: ot hooks approve &lt;id&gt; &lt;version&gt; (ot hooks list shows both), or Settings › Hooks in the app; reject turns it down.  
test runs the hooks for an event now, with an example task.  

### ot rich

A task's rich HTML version (tables, colors): print it, save one from a file or stdin, or remove it.

```
ot rich <task> [<file.html> | - | none]
```

Only top-level tasks have one. Scripts in it never run.  

### ot attachment

Save a copy of a task's attachment (default: here, by its name; - prints it).

```
ot attachment <task> <file name or id> [--to path | --to -]
```

ot attach adds one. Attachments are removed only in the app.  

### ot export

The whole board as JSON (the format every app reads).

```
ot export [--to file]
```

### ot settings

The app's settings: the theme, and where reminders are posted (Teams).

```
ot settings [theme system|light|dark | teams <https://…|off>]
```

teams is for you, the person at the computer (not for AI agents). The app uses a change from its next start.  

### ot sync

How syncing with your other computers is going; now syncs (while the app isn't running).

```
ot sync [now]
```

Setting it up (where, which library, clashes) is in the app: Settings › Sync. While the app runs it syncs by itself, and ot sync now says so.  

### ot connectors

Notion, Microsoft To Do and GitHub Issues: their status; sync one now (while the app isn't running).

```
ot connectors [sync <id> | sync github <group>]
```

Signing in and choosing what to connect are in the app: Settings › Connected apps.  

### ot plugins

The extras (plugins) and whether each is on; switch one on or off.

```
ot plugins [on <id> | off <id>]
```

Switching is for you, the person at the computer (not for AI agents); it applies when the app starts again.  
