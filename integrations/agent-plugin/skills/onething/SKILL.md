---
name: onething
description: Work with the user's One Thing task list (an ADHD-friendly todo app). Use when the user mentions tasks, todos, reminders, what to work on next, planning their day or week, capturing follow-ups from a meeting or chat, logging progress, deferring work, tracking time, or automating something when a task is done; or when you finish something they asked you to track.
---

# One Thing

One Thing is the user's task list. It is built for ADHD: **one clear next action beats a long list**, nothing should
nag, and nothing they wrote should ever be lost. Help them stay focused, not busy.

## How to reach it

Use the `onething` MCP tools (from `onething mcp`, or the app's `/mcp`). If they aren't connected, use the `ot` command
(`onething` is the same command): every command takes `--json` (the same shapes as the tools) and `--as <your name>`, so the user sees who changed what.
`<task>` in `ot` is an id prefix (as `ot` prints it) or words from the title; the MCP tools take full ids.

Full reference: `docs/mcp.md` (every tool and parameter) and `docs/cli.md` (every command) in the repository.

## The tools

| To… | MCP tool | `ot` |
| --- | --- | --- |
| See what to do now, what's waiting, what got done today | `get_dashboard` | `ot now` |
| Find tasks (`#tag`, `label:x`, `group:work`, `is:done`, `is:archived`) | `search_tasks`, `list_tasks` | `ot list <query>` |
| Read one task (subtasks, notes, file, link) | `get_task` | `ot show <task>` |
| Give the user a link that opens the task in the app | the task's `link` (`onething://task/<id>`) | `ot open <task>` |
| Capture a task or a subtask | `create_task` (`parentId` for a subtask), or `capture_task` with the user's quick-add words | `ot add <title> [--under <task>]` |
| Break work into ordered steps (a rollout) | `add_steps` | `ot steps <task> <step>… --delay 24` |
| Log progress ("did X, next Y"), attach a log or plan; correct a note you wrote (someone else's: add a new one) | `add_note`, `attach_text`, `update_note` | `ot note <task> <text> [--url …]`, `ot attach <task> <file>`, `ot note <task> --edit last <text>` |
| Read an attachment | `read_attachment` | `ot attachment <task> <file name> [--to -]` |
| Not now: defer and remind later | `schedule_next_action` (`inMinutes`, `at`, or `when`: "next week", "fri 14:00") | `ot snooze <task> 2h` / `ot snooze <task> next week` (`ot snooze <task> clear` brings it back) |
| Back to Do now after all | `clear_snooze` | `ot snooze <task> clear` |
| Not until another task is done | `wait_for_task` | `ot snooze <task> after <other task>` |
| A reminder without deferring; dismiss one | `add_reminder`, `dismiss_reminder` | `ot remind <task> 2h --message …`, `ot remind <task> --dismiss all` |
| Work on it at a set time (a block on the plan); what's planned | `plan_task`, `get_plan` | `ot plan <task> tomorrow 9:00 --minutes 60`, `ot plan` (what's planned), `ot plan settings` |
| How draining a task is (1 to 3 ⚡ an hour), how much energy a day has | `update_task` (`energy`), `set_daily_energy` | `ot edit <task> --energy 3`, `ot energy 8` |
| Decide what comes first | `put_first`, `arrange_now` (the whole Do now order) | `ot first <task>`, `ot order <task> <task>…` |
| Finish or undo | `complete_task`, `reopen_task` | `ot done <task>`, `ot reopen <task>` |
| Rename, re-prioritize, deadline, icon, color, repeat, steps one at a time | `update_task` | `ot edit <task> --priority high --due fri --repeat "every weekday" --icon 🚀` |
| Tags (free words) and labels (the user's curated, colored set) | `update_task`, `list_tags`, `list_labels`, `create_label`, `update_label` | `ot tag <task> +x -y`, `ot tags`, `ot label <task> "+Deep work"`, `ot labels`, `ot labels add <name> --color …` |
| Reorganize (under another task, top level, another group; next to a sibling) | `move_task`, `reorder_task` | `ot move <task> --under <task>`, `ot move <task> --before <task>` |
| Groups (tabs): list, make one, rename, recolor (only when the user asks: an unknown group is an error, never a new group) | `list_groups`, `create_group`, `update_group` | `ot groups`, `ot group add <name>`, `ot group rename <group> <new name>`, `ot group color <group> #22c55e` |
| Board columns (inbox, next, doing) | `move_card` | `ot stage <task> doing` |
| Time work; correct logged time | `start_timer`, `stop_timer`, `log_time`, `update_time_entry` | `ot start <task>`, `ot stop`, `ot log <task> 45m` |
| A focus (Pomodoro) session | `focus_timer` (state: `get_dashboard`'s pomodoro) | `ot focus start <task>`, `ot focus pause`, `ot focus` |
| Where did the time go | `get_time_report`, `get_report` (timeline) | `ot time --days 7`, `ot timeline [<task>]` |
| Put finished work away | `archive_task`, `unarchive_task` | `ot archive <task>`, `ot archive --done-before 14`, `ot unarchive <task>` |
| Undo a change (every change is a version) | `task_history`, `board_history`, `get_task_version`, `restore_task_version` | `ot history [<task>]`, `ot history <task> --text <version>`, `ot restore <task> <version>` |
| Tables or a dashboard in a task (when markdown isn't enough) | `set_rich_html`, `get_rich_html` | `ot rich <task> [file.html]` |
| Run a script or call a webhook when something happens | `add_hook`, `update_hook`, `list_hooks`, `set_hook_enabled`, `remove_hook` | `ot hooks add <event> --bash … --powershell …` |
| Everything, as JSON (a backup, another tool) | `export_board` | `ot export` |
| The user's settings and extras (they switch them; you can read them) | — | `ot settings`, `ot plugins` |
| Sync with the user's other computers, connectors (Notion, Microsoft To Do, GitHub): status (syncing now is the user's: `ot sync now` and `ot connectors sync` refuse `--as`) | — | `ot sync`, `ot connectors` |
| Where the files are, and their format | `vault_info` | `ot vault`, `ot vault guide` |

## How to help

1. **Start with the dashboard** (`get_dashboard`) before suggesting what to work on. Respect the order of Do now: the
   user arranged it. When they say what matters most, put it first (`put_first`).
2. **Capture what the user says they need to do**, right away, in their words. Don't ask for details you can infer.
   Put it in the right group (tab), e.g. Work or Personal. Use `#tags` for topics and the user's labels (`list_labels`)
   only when they clearly fit.
3. **Make big things small.** If a task is vague or large, add 2–5 concrete subtasks with a visible first step. Use
   ordered steps (`add_steps`) for rollouts and anything that must happen in sequence.
4. **Log progress as notes** when work happens: short, past tense, with the next step ("Deployed ring 0, next: check
   dashboards tomorrow").
5. **Defer instead of piling up.** If something can't be done now, schedule it (`schedule_next_action`) or make it wait
   for another task (`wait_for_task`), so it leaves Do now and comes back with a reminder.
6. **Finish things** you completed for the user (`complete_task`, once: a repeating task moves on to its next time
   each time) and tell them in one line.
7. **Never delete**, and never mark done what the user hasn't done. There is no delete tool and no `ot` command that
   deletes (tasks, groups, labels, attachments, time); archive or finish instead. Changes are versioned, so mistakes can
   be undone (`restore_task_version`). Settings, extras (plugins) and turning down hooks are the user's: `ot` refuses
   them with `--as`.
8. **Link to tasks** you mention: the `link` from `get_task` (`onething://task/<id>`) opens it in the app.

Keep replies short: what you changed, and the one next action.

## Recipes

**"What should I do now?"** `get_dashboard`, then name the focus task and why (a reminder, a deadline, it's first in
Do now). Offer one small first step. Don't read the whole list back.

**After a meeting or chat:** one `create_task` per follow-up, in the user's words, in the right group; deadlines when
said ("by Friday"); `add_note` on an existing task when it's progress on it, not new work.

**Plan my day / week:** `get_dashboard`, ask (once) what matters most, `put_first` those, then `plan_task` blocks for
the two or three biggest ones. Leave room: an ADHD-friendly day is not a full calendar. Watch `energy` in the dashboard:
a day's plan takes its blocks' hours times each task's energy (1 to 3 an hour; set it with `update_task` when the user
says something is draining or easy). Past `perDay`, say so and offer to move a block to another day.

**A rollout or a sequence:** `create_task` for the whole thing, `add_steps` with `stepDelayHours: 24`. Only the current
step shows in Do now; finishing it brings the next one back after the delay, with a reminder.

**Repeating work:** `update_task` with `repeat` ("every weekday", "every 2 weeks on Monday, Thursday", "every month on
the last Friday", "every 3 days when done"). Completing it moves it to its next time.

**End of day:** `complete_task` what's done, `add_note` where things stand, `schedule_next_action` what's not for
tomorrow ("tomorrow 9am"). `get_time_report` if they want to see where the time went.

**Automations:** `add_hook` (e.g. on `taskCompleted`, post to a webhook or run a script). A hook you add waits until
the user allows it in the app (they get asked): tell them so, and don't add it again. You can switch off or remove
only hooks you added; the user's own are theirs to change.

## Files

Tasks are markdown files in a folder (an Obsidian vault works): one folder per group, one file per task. Prefer the
tools; if you edit files directly, read the format guide first (`vault_info`, or `ot vault guide`) and keep the `id:`
in each file's frontmatter.
