# The `tt` command

<!-- Made from the code by tests/TodoTracker.Cli.Tests/ReferenceDocsTests.cs. Don't edit by hand: run `TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests`. -->

`tt` reads and changes your tasks (the markdown files in your tasks folder) from a terminal or a script, without
the app running, and `tt mcp` is the MCP server for AI apps. It's next to the app on Windows, in `~/.local/bin`
with the Linux install, or `dotnet tool install -g TodoTracker.Cli` ([ai-connectors.md](ai-connectors.md#get-tt)).

`<task>` is an id (or its first 6+ characters, as `tt` prints them) or words from the title. Exit codes: `0` done,
`1` couldn't (the reason is on stderr), `2` wrong usage.

## Options for every command

| Option | What it does |
| --- | --- |
| `--json` | print JSON (same shapes as the REST API and MCP tools) |
| `--as <name>` | attribute changes to an agent, e.g. --as claude (or set TT_AGENT) |
| `--vault <dir>` | use another tasks folder (default: the app's, or TODOTRACKER_VAULT) |
| `--data <dir>` | use another app data folder (or TODOTRACKER_DATA) |
| `--no-history` | don't save a version for this change |

## Commands

| Command | What it does |
| --- | --- |
| [`tt now`](#tt-now) | What to do now, and what is waiting |
| [`tt add`](#tt-add) | Add a task |
| [`tt list`](#tt-list) | Find tasks |
| [`tt show`](#tt-show) | A task with its subtasks, notes, file and link |
| [`tt open`](#tt-open) | Open a task in the app (its todotracker:// link) |
| [`tt done`](#tt-done) | Mark tasks done |
| [`tt reopen`](#tt-reopen) | Undo done |
| [`tt first`](#tt-first) | Put tasks at the top of Do now (the first becomes the focus) |
| [`tt note`](#tt-note) | Log progress ("did X, next Y"); - reads the text from stdin |
| [`tt snooze`](#tt-snooze) | Defer a task until later, or until another task is done (it moves to Waiting and reminds you) |
| [`tt edit`](#tt-edit) | Change a task |
| [`tt tag`](#tt-tag) | Add or remove tags |
| [`tt label`](#tt-label) | Add or remove labels (quote names with spaces) |
| [`tt move`](#tt-move) | Put a task under another, at the top level, or in another group |
| [`tt steps`](#tt-steps) | Add ordered steps; finishing one unlocks the next after the delay |
| [`tt attach`](#tt-attach) | Copy a file into the tasks folder and link it |
| [`tt history`](#tt-history) | Saved versions of a task, newest first |
| [`tt restore`](#tt-restore) | Put a task back the way it was in a version |
| [`tt groups`](#tt-groups) | Groups (tabs) with counts |
| [`tt labels`](#tt-labels) | The curated labels |
| [`tt vault`](#tt-vault) | Where the task files live, their format, or switch folders |
| [`tt mcp`](#tt-mcp) | Run the MCP server over stdio (for Claude, Copilot, VS Code…) |
| [`tt start`](#tt-start) | Start timing a task (any other timer stops) |
| [`tt stop`](#tt-stop) | Stop the timer |
| [`tt stage`](#tt-stage) | Move a task to a board column |
| [`tt archive`](#tt-archive) | Put finished tasks away (tt list is:archived finds them) |
| [`tt time`](#tt-time) | Where the time went: tracked per task, focus sessions, tasks done |
| [`tt hooks`](#tt-hooks) | Run your own scripts or webhooks when something happens (hooks.json) |

### tt now

What to do now, and what is waiting.

```
tt [now] [filter] [-g group]
```

filter uses the list syntax, e.g. tt now #release  

### tt add

Add a task.

```
tt add <title…> [-g group] [--under task] [--details text] [--tag t]… [--label l]… [--due when] [--snooze when]
```

Quick words in the title: ! high, !! critical, !low, @2h or @tomorrow (defer), due:3d or due:2026-02-01, #tag.  
Quote the title: shells treat # ! @ specially. Example: tt add 'Renew passport !! due:7d #admin'  
Or use options: tt add Renew passport --due 7d --tag admin  

### tt list

Find tasks.

```
tt list [filter] [-g group] [--all]
```

Words match titles and details; #tag, label:name, group:name, is:open, is:done narrow it. Open tasks unless --all or is:.  

### tt show

A task with its subtasks, notes, file and link.

```
tt show <task>
```

### tt open

Open a task in the app (its todotracker:// link).

```
tt open <task>
```

tt show prints the link, to paste in a note, chat or mail.  

### tt done

Mark tasks done.

```
tt done <task> | tt done <id> <id>…
```

Several tasks at once by id (as tt prints them); words are one task's title.  

### tt reopen

Undo done.

```
tt reopen <task>
```

### tt first

Put tasks at the top of Do now (the first becomes the focus).

```
tt first <task> | tt first <id> <id>…
```

### tt note

Log progress ("did X, next Y"); - reads the text from stdin.

```
tt note <task> <text…|->
```

### tt snooze

Defer a task until later, or until another task is done (it moves to Waiting and reminds you).

```
tt snooze <task> <when | after <task>>
```

when: 45m, 2h, 3d, tomorrow, next week, fri 14:30, weekend, 2026-02-01, 2026-02-01T14:30 — or: after &lt;other task&gt;  

### tt edit

Change a task.

```
tt edit <task> [--title t] [--details d] [--priority low|normal|high|critical] [--due when|none]
```

### tt tag

Add or remove tags.

```
tt tag <task> [+]tag… -tag…
```

### tt label

Add or remove labels (quote names with spaces).

```
tt label <task> [+]label… -label…
```

### tt move

Put a task under another, at the top level, or in another group.

```
tt move <task> (--under task | --top | -g group) [--index n]
```

### tt steps

Add ordered steps; finishing one unlocks the next after the delay.

```
tt steps <task> <step>… [--delay hours]
```

### tt attach

Copy a file into the tasks folder and link it.

```
tt attach <task> <file>
```

### tt history

Saved versions of a task, newest first.

```
tt history <task>
```

### tt restore

Put a task back the way it was in a version.

```
tt restore <task> <version>
```

### tt groups

Groups (tabs) with counts.

```
tt groups
```

### tt labels

The curated labels.

```
tt labels
```

### tt vault

Where the task files live, their format, or switch folders.

```
tt vault [guide | obsidian | use <folder|obsidian vault>]
```

### tt mcp

Run the MCP server over stdio (for Claude, Copilot, VS Code…).

```
tt mcp
```

See docs/ai-connectors.md for setup in each app.  

### tt start

Start timing a task (any other timer stops).

```
tt start <task>
```

The task's card moves to Doing. tt stop stops it.  

### tt stop

Stop the timer.

```
tt stop
```

### tt stage

Move a task to a board column.

```
tt stage <task> <inbox|next|doing>
```

Finish a task (tt done) to move it to Done.  

### tt archive

Put finished tasks away (tt list is:archived finds them).

```
tt archive <task> | tt archive --done-before <days>
```

--done-before 14 archives everything finished at least 14 days ago.  

### tt time

Where the time went: tracked per task, focus sessions, tasks done.

```
tt time [--days n] [-g group]
```

Default: the last 28 days. --json prints the full report (per day, group, hour).  

### tt hooks

Run your own scripts or webhooks when something happens (hooks.json).

```
tt hooks [list | add <event> | edit <id> | enable <id> | disable <id> | approve <id> <version> | remove <id> | test [event] | path]
```

add &lt;event&gt; (--powershell script | --bash script | --url https://…) [--name n] [--timeout seconds] [-g group]… [--tag t]… [--label l]… [--actor user|agent|…]…  
edit &lt;id&gt; [--event e] and any of the add options (only what you give changes).  
&lt;event&gt;: taskCompleted, taskCreated, reminderDue, noteAdded, focusEnded… or * for every event (see docs/hooks.md).  
A command gets the event as JSON on stdin. With --as (or TT_AGENT) a hook you add or change waits for approval:  
tt hooks approve &lt;id&gt; &lt;version&gt; (without --as; tt hooks list shows both), or Settings › Hooks in the app. test runs the hooks for an event now, with an example task.  
