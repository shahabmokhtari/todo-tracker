---
name: todo-tracker
description: Work with the user's Todo Tracker task list (an ADHD-friendly todo app). Use when the user mentions tasks, todos, reminders, what to work on next, planning their day or week, capturing follow-ups from a meeting or chat, logging progress, deferring work, tracking time, or automating something when a task is done; or when you finish something they asked you to track.
---

# Todo Tracker

Todo Tracker is the user's task list. It is built for ADHD: **one clear next action beats a long list**, nothing should
nag, and nothing they wrote should ever be lost. Help them stay focused, not busy.

## How to reach it

Use the `todo-tracker` MCP tools (from `tt mcp`, or the app's `/mcp`). If they aren't connected, use the `tt` command:
every command takes `--json` (the same shapes as the tools) and `--as <your name>`, so the user sees who changed what.
`<task>` in `tt` is an id prefix (as `tt` prints it) or words from the title; the MCP tools take full ids.

Full reference: `docs/mcp.md` (every tool and parameter) and `docs/cli.md` (every command) in the repository.

## The tools

| To… | MCP tool | `tt` |
| --- | --- | --- |
| See what to do now, what's waiting, what got done today | `get_dashboard` | `tt now` |
| Find tasks (`#tag`, `label:x`, `group:work`, `is:done`, `is:archived`) | `search_tasks`, `list_tasks` | `tt list <query>` |
| Read one task (subtasks, notes, file, link) | `get_task` | `tt show <task>` |
| Give the user a link that opens the task in the app | the task's `link` (`todotracker://task/<id>`) | `tt open <task>` |
| Capture a task or a subtask | `create_task` (`parentId` for a subtask) | `tt add <title> [--under <task>]` |
| Break work into ordered steps (a rollout) | `add_steps` | `tt steps <task> <step>… --delay 24` |
| Log progress ("did X, next Y"), attach a log or plan | `add_note`, `attach_text` | `tt note <task> <text>`, `tt attach <task> <file>` |
| Not now: defer and remind later | `schedule_next_action` (`inMinutes`, `at`, or `when`: "next week", "fri 14:00") | `tt snooze <task> 2h` / `tt snooze <task> next week` |
| Not until another task is done | `wait_for_task` | `tt snooze <task> after <other task>` |
| A reminder without deferring | `add_reminder` | (`tt snooze` reminds too) |
| Work on it at a set time (a block on the plan) | `plan_task` | (the Plan view) |
| Decide what comes first | `put_first` | `tt first <task>` |
| Finish or undo | `complete_task`, `reopen_task` | `tt done <task>`, `tt reopen <task>` |
| Rename, re-prioritize, deadline, icon, color, repeat | `update_task` | `tt edit <task> --priority high --due fri` |
| Tags (free words) and labels (the user's curated, colored set) | `update_task`, `list_labels` | `tt tag <task> +x -y`, `tt label <task> "+Deep work"`, `tt labels` |
| Reorganize (under another task, top level, another group) | `move_task` | `tt move <task> --under <task>` |
| Groups (tabs) | `get_dashboard` (groups) | `tt groups` |
| Board columns (inbox, next, doing) | `move_card` | `tt stage <task> doing` |
| Time work | `start_timer`, `stop_timer`, `log_time` | `tt start <task>`, `tt stop` |
| Where did the time go | `get_time_report`, `get_report` (timeline) | `tt time --days 7` |
| Put finished work away | `archive_task`, `unarchive_task` | `tt archive <task>`, `tt archive --done-before 14` |
| Undo a change (every change is a version) | `task_history`, `restore_task_version` | `tt history <task>`, `tt restore <task> <version>` |
| Tables or a dashboard in a task (when markdown isn't enough) | `set_rich_html`, `get_rich_html` | — |
| Run a script or call a webhook when something happens | `add_hook`, `list_hooks`, `set_hook_enabled`, `remove_hook` | `tt hooks add <event> --bash … --powershell …` |
| Where the files are, and their format | `vault_info` | `tt vault`, `tt vault guide` |

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
6. **Finish things** you completed for the user (`complete_task`) and tell them in one line.
7. **Never delete**, and never mark done what the user hasn't done. There is no delete tool; changes are versioned, so
   mistakes can be undone (`restore_task_version`).
8. **Link to tasks** you mention: the `link` from `get_task` (`todotracker://task/<id>`) opens it in the app.

Keep replies short: what you changed, and the one next action.

## Recipes

**"What should I do now?"** `get_dashboard`, then name the focus task and why (a reminder, a deadline, it's first in
Do now). Offer one small first step. Don't read the whole list back.

**After a meeting or chat:** one `create_task` per follow-up, in the user's words, in the right group; deadlines when
said ("by Friday"); `add_note` on an existing task when it's progress on it, not new work.

**Plan my day / week:** `get_dashboard`, ask (once) what matters most, `put_first` those, then `plan_task` blocks for
the two or three biggest ones. Leave room: an ADHD-friendly day is not a full calendar.

**A rollout or a sequence:** `create_task` for the whole thing, `add_steps` with `stepDelayHours: 24`. Only the current
step shows in Do now; finishing it brings the next one back after the delay, with a reminder.

**Repeating work:** `update_task` with `repeat` ("every weekday", "every 2 weeks on Monday, Thursday", "every month on
the last Friday", "every 3 days when done"). Completing it moves it to its next time.

**End of day:** `complete_task` what's done, `add_note` where things stand, `schedule_next_action` what's not for
tomorrow ("tomorrow 9am"). `get_time_report` if they want to see where the time went.

**Automations:** `add_hook` (e.g. on `taskCompleted`, post to a webhook or run a script). A hook you add waits until
the user allows it in the app (they get asked): tell them so, and don't add it again.

## Files

Tasks are markdown files in a folder (an Obsidian vault works): one folder per group, one file per task. Prefer the
tools; if you edit files directly, read the format guide first (`vault_info`, or `tt vault guide`) and keep the `id:`
in each file's frontmatter.
