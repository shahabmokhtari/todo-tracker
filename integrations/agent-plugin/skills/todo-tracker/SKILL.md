---
name: todo-tracker
description: Work with the user's Todo Tracker task list (an ADHD-friendly todo app). Use when the user mentions tasks, todos, reminders, what to work on next, planning their day, capturing follow-ups from a meeting or chat, logging progress, or deferring work; or when you finish something they asked you to track.
---

# Todo Tracker

Todo Tracker is the user's task list. It is built for ADHD: one clear next action beats a long list, nothing should
nag, and nothing they wrote should ever be lost. Help them stay focused, not busy.

## Tools

Use the `todo-tracker` MCP tools. If they aren't available, use the `tt` command (every command takes `--json`, and
`--as <your name>` so the user sees who changed what).

| To… | MCP tool | CLI |
| --- | --- | --- |
| See what to do now and what is waiting | `get_dashboard` | `tt now` |
| Find tasks (`#tag`, `label:x`, `group:work`, `is:done`) | `search_tasks` | `tt list <query>` |
| Read one task (subtasks, notes, file) | `get_task` | `tt show <task>` |
| Capture a task or a subtask | `create_task` (`parentId` for a subtask) | `tt add <title> [--under <task>]` |
| Break work into ordered steps | `add_steps` | `tt steps <task> <step>…` |
| Log progress ("did X, next Y") | `add_note` | `tt note <task> <text>` |
| Not now: defer and remind later | `schedule_next_action` | `tt snooze <task> 2h` |
| Finish or undo | `complete_task`, `reopen_task` | `tt done <task>`, `tt reopen <task>` |
| Rename, re-prioritize, tag, label | `update_task` | `tt edit`, `tt tag`, `tt label` |
| Reorganize | `move_task` | `tt move <task> --under <task>` |
| Decide what comes first | `put_first` | `tt first <task>` |
| Undo a change | `task_history`, `restore_task_version` | `tt history <task>`, `tt restore <task> <version>` |

`<task>` in the CLI is an id prefix (as `tt` prints it) or words from the title.

## How to help

1. **Start with the dashboard** before suggesting what to work on. Respect the order of Do now: the user arranged it.
   When they say what matters most, put it first (`put_first`).
2. **Capture what the user says they need to do**, right away, in their words. Don't ask for details you can infer.
   Put it in the right group (tab), e.g. Work or Personal. Use `#tags` for topics and the user's curated labels
   (`list_labels`) only when they clearly fit.
3. **Make big things small.** If a task is vague or large, add 2–5 concrete subtasks with a visible first step.
   Use ordered steps (`add_steps`) for rollouts and anything that must happen in sequence.
4. **Log progress as notes** when work happens ("Deployed ring 0, next: check dashboards tomorrow"). Short, past tense,
   with the next step.
5. **Defer instead of piling up.** If something can't be done now, schedule it (`schedule_next_action`) so it leaves
   the Now list and comes back with a reminder.
6. **Finish things** you completed for the user (`complete_task`) and tell them in one line.
7. **Never delete** and never mark done what the user hasn't done. Changes are versioned, so mistakes can be undone.

Keep replies short: what you changed, and the one next action.

## Files

Tasks are markdown files in a folder (an Obsidian vault works). Prefer the tools; if you edit files directly, read the
format guide first (`vault_info`, or `tt vault guide`).
