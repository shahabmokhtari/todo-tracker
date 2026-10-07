# Vault format (v2)

Todo Tracker stores tasks as plain markdown in a folder (the **vault**). The folder works on its own, inside an
Obsidian vault, in a git repository, or in any sync service. People, the apps, the `tt` CLI, and AI agents all edit
the same files. A vault the app creates gets a copy of this guide for agents in `AGENTS.md` at the vault root (a folder
of existing notes is left as it is).

## Layout

```
Todo Tracker/                       ← the vault (default: Documents/Todo Tracker)
  AGENTS.md                         ← format guide for AI tools (not a task)
  Work/                             ← a group (tab). Create a folder to add a group.
    Ship release 2.3.md             ← a top-level task. Create a file to add a task.
    Ship release 2.3.html           ← optional rich version (free-form HTML)
    Clients/Acme onboarding.md      ← subfolders are fine; the task belongs to the top folder's group
  Personal/
  _attachments/7b0c2f9e/plan.pdf    ← files attached in the app
  .todo-tracker/                    ← app data (hidden in Obsidian)
    config.json                     ← group ids/colors/order, label colors, task order
    state.json                      ← focus timer
    activity.jsonl                  ← audit log (append-only)
    trash/                          ← deleted task files (kept 30 days)
```

* Folders starting with `_` or `.` are not groups. Files directly in the vault root are not tasks.
* A task's identity is the `id` in its frontmatter, not its file name. Files are never renamed when a title changes,
  so links to them keep working. Moving a task to another group moves its file and keeps its name and subfolder.
* Renaming a group folder in Obsidian keeps the group (recognized by the tasks inside it).

## A task file

```markdown
---
id: 7b0c2f9e-0000-4000-8000-000000000001
status: open                         # open | done   (other values, e.g. waiting, are kept as typed)
stage: doing                         # board column: inbox | next | doing (omitted = next; done = status)
priority: high                       # low | normal | high | critical (omitted = normal)
created: 2026-01-05T09:00:00.000Z
archived: 2026-02-01T08:00:00.000Z   # put away (a done task): out of every list, found with is:archived
due: 2026-01-07T17:00                # local time; a date alone means 17:00
scheduled: 2026-01-06T09:00          # "not before": waits until then; a date alone means 09:00
sequential: true                     # steps must be done in order
step-delay: 1d                       # minimum wait between steps (90m, 24h, 1d)
tags:
  - release
labels:
  - Deep work
reminders:
  - id: …
    at: 2026-01-06T09:00:00.000Z
    message: "Time to act on: Ship release 2.3"
    kind: next-action                # next-action | manual
---
# Ship release 2.3

Free-form details in markdown. Any other headings, tables, embeds, or sections before "## Steps" are details.

## Steps

1. [x] Roll out A ✅ 2026-01-05 ^t0000002 %%{"id":"…","created":"…","done":"…"}%%
	Indented lines are the step's details.
	- [ ] Check dashboards #risky ⏫ ^t0000003 %%{…}%%
	- 📎 [screenshot.png](../_attachments/7b0c2f9e/screenshot.png) %%{…}%%
2. [ ] Roll out B 📅 2026-01-08 ⏳ 2026-01-06 ^t0000004 %%{…}%%

## Notes

> [!note] 2026-01-05 10:00 · Agent: copilot · [[#^t0000002|Roll out A]] %%{"id":"…","at":"…"}%%
> deployed ring 0
> 🔗 [Release dashboard](https://example.com/rel)

## Time

- 2026-01-05 09:00–09:25 · 25 min · focus · [[#^t0000002|Roll out A]] %%{"id":"…","start":"…","end":"…"}%%
- 2026-01-06 14:00 → running %%{"id":"…","start":"…","device":"…"}%%

## Attachments

- [plan v2.pdf](../_attachments/7b0c2f9e/plan%20v2.pdf) %%{…}%%
```

### Properties

The app owns `id` (or `tt-id`), `status`, `stage`, `priority`, `created`, `completed`, `archived`, `due`,
`scheduled`, `sequential`, `step-delay`, `tags`, `labels`, and `reminders`. Everything else (`aliases`, `cssclasses`, plugin properties,
comments) is kept exactly as written and in place. An owned property whose value hasn't changed is written back
exactly as typed (a date-only `due`, a flow list `[a, b]`, quotes). If `id` already holds the user's own value (for
example a Zettelkasten id), it stays and the app keeps its id in `tt-id`.

### Subtasks

* `## Subtasks` (or `## Steps`, `## Tasks`, `## Checklist`) holds checkbox list items. Nest them with tabs or spaces,
  to any depth. A **numbered** list means the items are steps that must be done in order.
* Line tokens follow the Obsidian Tasks plugin. Priority: `🔺` critical, `⏫` high, `🔼` medium (normal), `🔽` low,
  `⏬` lowest (low). `📅` is the due date, `⏳` the scheduled date, `✅` the done date. `#tags` are tags (trailing tags
  are taken off the title; tags inside the sentence stay in it).
* `[x]` and `[-]` (cancelled) count as done; any other checkbox character (`[/]`, `[>]`, `[!]`…) counts as open and
  is kept as typed.
* `^t…` is an Obsidian block id (for `[[#^id]]` links). `%%{json}%%` is a hidden comment with exact times, ids,
  labels, and reminders. The visible date wins if it was edited (the hidden time is used only when its date matches).
* Indented non-list lines under an item are its details. Code blocks are never parsed for tasks.
* Headings or text between top-level items stay above the item that follows them; text after the list stays after it.

### Board and archive

* `stage` is the task's column on the board: `inbox` (just captured), `next`, or `doing`. A task without one is in
  Next; finished tasks are in Done whatever their stage. Tasks created in the app start in the inbox. Starting a timer
  on a task (or a subtask) moves it to Doing.
* `archived` puts a finished task away: it stays where it is (links to the file keep working), drops out of every
  list and search, and is found again with `is:archived`. Reopening it takes it out of the archive.

### Time

* `## Time` (or `## Time log`) lists the time spent on the task and its subtasks: the local start, the end (with its
  date when it's another day), the length, `focus` for a Pomodoro session, and a link to the subtask it was for.
  `→ running` is a timer that's still running (on the device in the hidden comment).
* Typing a line counts too: `- 2026-01-05 9:00 to 10:30` (or `09:00–10:30`, `09:00 - 10:30`); words after a `·` are
  kept. The visible times win if they were edited. Other text in the section stays as written.
* A timer left running when the app stopped (or the computer slept) ends when the app last saw it running.

### Notes and attachments

* Notes are callouts with the local time, the author (`You`, `Agent: <name>`, `Browser`, `Teams`, `Edited in files`),
  and an optional link to a subtask. The last line `🔗 [title](url)` is the source. Notes are editable;
  `.todo-tracker/activity.jsonl` is the audit trail.
* Attachments are links in `## Attachments` (or `- 📎` items under a subtask). Markdown links, embeds, and Obsidian
  wiki links (`![[file.png|300]]`) all work and keep their form. When the tasks folder is inside an Obsidian vault, wiki
  links also find files elsewhere in that vault (where Obsidian puts pasted images). Links that point outside the vault
  (or the Obsidian vault around it) are refused.

## How the app treats your files

* **Reading never writes.** A file typed by hand gets ids derived from its path, so it works right away and is only
  rewritten when the app changes that task (or reacts to an edit, below). Opening an existing notes folder changes
  nothing.
* **Only touched files change.** A change rewrites just the files of the tasks it touched.
* **Edits outside the app are understood.** Checking a step in Obsidian completes it in the app, unlocks the next step
  after its delay, and is logged as "Edited in files" (once, even with several app processes). The app waits until
  the file has been quiet for a moment before it writes, so it never fights with an editor.
* **Saves are all-or-nothing.** Before writing, every file is compared (by content) with what the app last read; if
  someone saved in between, the app re-reads and re-applies its change. If a save fails part-way, every file is put
  back. A task moved between files is written to its new file first, so a crash can only duplicate, never lose it.
* **Broken files are shown, not guessed.** A file with invalid frontmatter is listed as a problem. Its last good
  version stays visible, and the app never writes to it until it's fixed.
* **Copies get their own identity.** A duplicated task file gets new ids; the original keeps its own.
* **Several processes, one vault.** The app, the CLI, and MCP servers coordinate with a lock in local app data (not in
  the synced folder). Changes from other devices arrive through your sync service and are picked up like any edit.
* **History.** Every change, in the app or outside it, is kept as a version in a private git repository on this device
  (in local app data, never in the synced folder, and separate from any repository you have; attachments aren't
  versioned). Any task can be viewed or restored from its history; a
  restore is itself a version. Needs `git`.
