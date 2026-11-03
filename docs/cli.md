# The `ot` command

<!-- Made from the code by tests/OneThing.Cli.Tests/ReferenceDocsTests.cs. Don't edit by hand: run `ONETHING_WRITE_DOCS=1 dotnet test tests/OneThing.Cli.Tests --filter ReferenceDocsTests`. -->

`ot` reads and changes your tasks (the markdown files in your tasks folder) from a terminal or a script, without
the app running, and `onething mcp` is the MCP server for AI apps. `ot` is the short name of `onething`: both work.
They're in the `cli` folder next to the app on Windows, in `~/.local/bin` with the Linux install, or
`dotnet tool install -g OneThing.Cli` (as `onething`) ([ai-connectors.md](ai-connectors.md#get-ot)).

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
| [`ot edit`](#ot-edit) | Change a task |
| [`ot energy`](#ot-energy) | Energy today: what you have in a day and what today's plan takes |
| [`ot tag`](#ot-tag) | Add or remove tags |
| [`ot label`](#ot-label) | Add or remove labels (quote names with spaces) |
| [`ot move`](#ot-move) | Put a task under another, at the top level, or in another group |
| [`ot steps`](#ot-steps) | Add ordered steps; finishing one unlocks the next after the delay |
| [`ot attach`](#ot-attach) | Copy a file into the tasks folder and link it |
| [`ot history`](#ot-history) | Saved versions of a task, newest first |
| [`ot restore`](#ot-restore) | Put a task back the way it was in a version |
| [`ot groups`](#ot-groups) | Groups (tabs) with counts |
| [`ot labels`](#ot-labels) | The curated labels |
| [`ot vault`](#ot-vault) | Where the task files live, their format, or switch folders |
| [`ot mcp`](#ot-mcp) | Run the MCP server over stdio (for Claude, Copilot, VS Code…) |
| [`ot start`](#ot-start) | Start timing a task (any other timer stops) |
| [`ot stop`](#ot-stop) | Stop the timer |
| [`ot stage`](#ot-stage) | Move a task to a board column |
| [`ot archive`](#ot-archive) | Put finished tasks away (ot list is:archived finds them) |
| [`ot time`](#ot-time) | Where the time went: tracked per task, focus sessions, tasks done |
| [`ot hooks`](#ot-hooks) | Run your own scripts or webhooks when something happens (hooks.json) |

### ot now

What to do now, and what is waiting.

```
ot [now] [filter] [-g group]
```

filter uses the list syntax, e.g. ot now #release  

### ot add

Add a task.

```
ot add <title…> [-g group] [--under task] [--details text] [--tag t]… [--label l]… [--due when] [--snooze when] [--energy 1|2|3]
```

Quick words in the title: ! high, !! critical, !low, @2h or @tomorrow (defer), due:3d or due:2026-02-01, #tag.  
Quote the title: shells treat # ! @ specially. Example: ot add 'Renew passport !! due:7d #admin'  
Or use options: ot add Renew passport --due 7d --tag admin  

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
ot note <task> <text…|->
```

### ot snooze

Defer a task until later, or until another task is done (it moves to Waiting and reminds you).

```
ot snooze <task> <when | after <task>>
```

when: 45m, 2h, 3d, tomorrow, next week, fri 14:30, weekend, 2026-02-01, 2026-02-01T14:30 — or: after &lt;other task&gt;  

### ot edit

Change a task.

```
ot edit <task> [--title t] [--details d] [--priority low|normal|high|critical] [--due when|none] [--energy 1|2|3|none]
```

--energy: how much energy it takes per hour, 1 (light) to 3 (draining).  

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

Put a task under another, at the top level, or in another group.

```
ot move <task> (--under task | --top | -g group) [--index n]
```

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

Saved versions of a task, newest first.

```
ot history <task>
```

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

### ot labels

The curated labels.

```
ot labels
```

### ot vault

Where the task files live, their format, or switch folders.

```
ot vault [guide | obsidian | use <folder|obsidian vault>]
```

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

### ot time

Where the time went: tracked per task, focus sessions, tasks done.

```
ot time [--days n] [-g group]
```

Default: the last 28 days. --json prints the full report (per day, group, hour).  

### ot hooks

Run your own scripts or webhooks when something happens (hooks.json).

```
ot hooks [list | add <event> | edit <id> | enable <id> | disable <id> | approve <id> <version> | remove <id> | test [event] | path]
```

add &lt;event&gt; (--powershell script | --bash script | --url https://…) [--name n] [--timeout seconds] [-g group]… [--tag t]… [--label l]… [--actor user|agent|…]…  
edit &lt;id&gt; [--event e] and any of the add options (only what you give changes).  
&lt;event&gt;: taskCompleted, taskCreated, reminderDue, noteAdded, focusEnded… or * for every event (see docs/hooks.md).  
A command gets the event as JSON on stdin. With --as (or ONETHING_AGENT) a hook you add or change waits for approval:  
ot hooks approve &lt;id&gt; &lt;version&gt; (without --as; ot hooks list shows both), or Settings › Hooks in the app. test runs the hooks for an event now, with an example task.  
