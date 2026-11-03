# One Thing: product plan, design, and specification

Traces to the original request in [`request.md`](request.md). Section 12 maps each requirement to its status.

## 1. Vision

One Thing is an **ADHD-friendly command center** for people juggling many workstreams, AI agents, chat, and
browser tabs. It answers one question at a glance: *what should I do right now?* Everything that isn't actionable
yet stays out of the way until it is due, and then it comes back by itself.

Product principles:

1. **One thing now.** A single "Do this now" card. Everything else is secondary and collapsible.
2. **Defer, don't forget.** Waiting work leaves the main list and returns on time, with a reminder.
3. **Low friction.** One-line capture, one-click Done/Snooze/Note, a global hotkey, and no required forms.
4. **Calm UI.** Few colors (priority only), no animations, collapsible sections, and remembered section state.
5. **Local-first, AI-native, and yours.** Tasks are plain markdown files in a folder you own (an Obsidian vault works
   as is), readable and writable by people, apps, and AI agents. No account or cloud needed; every change is versioned.
6. **Agents are collaborators.** Copilot and other agents use MCP tools. Their changes are attributed, visible, and non-destructive.
7. **One set of rules.** Scheduling and ordering live in the core. The Swift port is checked against the same fixtures.

## 2. Users and key scenarios

**Primary user:** a power user with ADHD running parallel technical work (rollouts, reviews, AI-assisted coding).

| Scenario | Expected behavior |
|---|---|
| Glance | Sidebar is always visible and never covered by maximized windows. The focus card shows the next action. |
| Rollout gating | *Roll out X* needs A and B. Each has 10 steps, and each step can start only 24h after the previous one. Only the current step of each shows in *Do now*. Finishing a step moves the next one to *Waiting* for 24h, then it returns with a reminder. |
| "Remind me tomorrow" | Snooze sends the task to *Waiting* and creates a reminder. When the time comes it comes back and a toast / Teams card fires. |
| Capture a thought | `Ctrl+Alt+Space` → type `Call vendor !! @2h` → Enter. |
| Log progress | ✎ on a card, type what was done, Enter. Recent notes show in the sidebar. The full timeline opens in the browser. |
| Browser research | The side panel extension attaches the current page's URL and title to a note on the focus task. |
| Agent help | Copilot (MCP) creates a task, adds rollout steps, logs "deployed ring 0", and schedules the next check. The notes show "Agent: copilot". |
| Context switch | Group tabs (Work, Personal, custom) filter everything. Badges show how much is waiting in each tab. |
| Focus | Pomodoro on the current task, timed as focus time on it. A full-screen break follows (take it, skip it, or start the next focus now); when a break the app showed runs out, it asks "Break's over: Start next focus / Not now" for up to 15 minutes (`breakOver`, same rule in web, Windows and Apple; `POST /api/pomodoro/next` never restarts a running session). |

## 3. Architecture

```
┌──────────────────────────── Windows ─────────────────────────────┐
│ OneThing.Windows (WPF, AppBar)                                 │
│   ├─ OneThing.Desktop (MVVM view models, platform-neutral)     │
│   └─ embeds OneThing.Server ── 127.0.0.1:5317 ─┬─ Web UI (SPA) │
│                                                   ├─ REST /api    │
│                                                   └─ MCP  /mcp    │
│ OneThing.Core (domain, agenda, store) ◄── all of the above     │
└───────────────────────────────────────────────────────────────────┘
   ▲ REST (loopback + token)            ▲ MCP (streamable HTTP + token)
   │                                    │
 Edge/Chrome side panel extension    Copilot CLI / VS Code / agents

OneThing.Web  – the same server as a standalone process (no WPF); used by CI e2e tests.
apple/           – OneThingKit (Swift port of Core) + SwiftUI apps for iOS and macOS.
```

* **Markdown vault.** The board lives in a folder of markdown files: one folder per group, one file per top-level
  task, Obsidian-compatible. The full format and the app's rules for touching files are in
  [`vault-format.md`](vault-format.md). In short: reading never writes, a change rewrites only the files it touched,
  saves are verified against the files' content and are all-or-nothing, edits made outside the app (Obsidian,
  editors, agents) are picked up and acted on, and broken files are reported and never overwritten.
* **Vault location.** `Documents/OneThing` by default, or a folder chosen in the app (for example
  `<Obsidian vault>/One Thing`), `ONETHING_VAULT`, or `<data>/vault` with a custom data folder. A `board.json`
  from earlier versions is imported once (and renamed to `board.json.migrated`).
* **Several processes.** The app, the `ot` CLI, and MCP servers can share a vault; a per-vault lock in local app data
  (never in the synced folder) serializes writes. One server runs per data folder (`instance.lock`).
* **History.** Every change is kept as a version in a private git repository on this device (local app data, never in the synced vault), saved a
  few seconds after changes settle and checked every two minutes for edits made outside the app. Any task can be
  viewed or restored from its history. Off when git isn't installed.
* **Data folder.** `%LOCALAPPDATA%\OneThing` (override with `ONETHING_DATA` or `--data`) holds
  `settings.json` (Teams webhook, vault folder), `api-token`, and `desktop.json` (window placement).

## 4. Domain model

| Entity | Fields |
|---|---|
| **Board** | `groups[]`, `labels[]`, `items[]` (roots), `activity[]`, `pomodoro` |
| **Group (tab)** | `id`, `name` (= its folder; unique, case-insensitive), `color` (`#rrggbb`) |
| **Label** | `name` (≤40), `color`. Curated and colored; using a new one defines it with the next palette color. Settings › Labels changes a label's color or name, or removes it from every task. |
| **Task** | `id`, `title` (≤300), `details`, `priority` (low/normal/high/critical), `createdAt`, `completedAt`, `deadline`, `nextActionAt` (defer gate), `sequential`, `stepDelayMinutes`, `groupId` (roots only), `tags[]` (free-form, nested with `/`, ≤20), `labels[]`, `icon` (an emoji or a few characters, ≤16), `color` (`#rrggbb`), `repeat` (a rule, below), `attachments[]`, `reminders[]`, `notes[]`, `children[]` (any depth). Tasks can move anywhere in the hierarchy. A task shows its **accent**: its own color, else its first label's (`TaskBoard.AccentOf`); the icon goes before the title everywhere (web, sidebar, Mac/iOS, Android; the phones keep both when they save). In the markdown: frontmatter `icon:` / `color:` (quoted), subtasks in their hidden data; invalid values written by hand are left out. A top-level task can have a rich HTML version. |
| **Attachment** | `id`, `fileName`, `path` (vault-relative), `size`, `addedAt`, `addedBy` |
| **Reminder** | `id`, `dueAt`, `message`, `kind` (`manual` or `nextAction`), `notifiedAt`, `dismissedAt` |
| **Note** | `id`, `at`, `text`, `author {kind: user/agent/browser/teams/system/vault, name}`, `sourceUrl` (http/https only), `sourceTitle`. Notes save while being typed (edits within 10 minutes of writing are part of writing it). |
| **Activity** | `at`, `itemId`, `kind`, `summary`, `actor`. This is the audit trail and the report timeline. |

**Search.** One query syntax everywhere (dashboard filter, CLI, MCP): words match the title or details; `#tag`
(including nested tags), `label:name` (quote multi-word values), `group:name`, and `is:open`/`is:done` narrow it.
Tags and labels are inherited, so `#release` also finds the steps of a tagged project.

## 5. Scheduling rules

**State** (derived from time, never stored), evaluated in this order:
1. `done`: the item has `completedAt`.
2. `locked`: the item or an ancestor sits behind an unfinished earlier step of a *sequential* parent.
3. `waiting`: the item's or an ancestor's `nextActionAt` is in the future.
4. `container`: the item has open subtasks. Its leaves are the actionable things.
5. `actionable`: anything else.

**Needs attention.** The item has a due, undismissed reminder and is not locked or done. This surfaces waiting and
container items in *Do now*.

**Do now** holds actionable items plus attention items. A task whose reminder just went off comes first: one never
placed, or whose reminder went off after Do now was last arranged (`NowArrangedAt`, kept with the order). After that,
**the person's own order wins**: they arrange Do now by dragging cards, with ↑/↓ buttons, or Alt+↑/↓ (agents use
`put_first`, the CLI `ot first`), anywhere in the list: a task whose reminder was already due when they arranged it
stays where they put it. Tasks they never placed come after
the ones they did, in the automatic order: effective priority (the maximum of the item and its ancestors), then
overdue, then deadline, then age, so capturing something new never takes over the chosen focus. The first one is the
**focus**. The order is shared by every app and process (`.onething/state.json`); arranging one group's tab keeps
the other groups' places. In the web task panel, subtasks are reordered the same way among their siblings (never
changing parent); the API also reorders top-level tasks.

**Waiting** lists only the top-most deferred item, so a snoozed project shows once, not once per subtask. It is
ordered by wake time, then priority.

**Sequences.** Completing step *n* sets step *n+1*'s gate to `completedAt + stepDelay`, or keeps an existing later
gate. Completing a locked step, or anything under one, is rejected with "Finish X first". Completing the last step
completes the sequence. Completing a parent completes all of its open descendants, and the UIs ask first. Reopening
that parent reopens exactly the children that action closed. Reopening a child reopens its ancestors. A reopened task
is back in **Do now**: a snooze or "after another task" wait set before it was done is cleared (and its reminder
dismissed), so Reopen never seems to do nothing.

**Snooze / schedule.** Sets `nextActionAt` and (default) adds a `nextAction` reminder at that time. Rescheduling
dismisses the previous undelivered schedule reminder and any reminder that is already due (even if delivered), so "Later" on a ringing reminder really moves the task to *Waiting* and reminders never stack.

* **Quick choices** (`Snooze.Choices`, the same ids in C#, the web app via `GET /api/snooze`, Swift and Kotlin;
  `tests/fixtures/snooze.json` checks them): `15m`, `1h`, `3h`, `evening` (18:00; offered before 17:00), `tomorrow`
  (next 9:00; before 4:00 that's this morning), `2d`, `monday` (next Monday; a week on when it's Monday), `week`, `month`
  (9:00 local on that day; a month from Jan 31 is the end of February). Each shows when it ends: "today 17:00",
  "tomorrow 9:00", "Fri 14:30", "Mon 12 Jan, 9:00".
* **Typed rules** (`Snooze.Parse`, "Pick a time…" with a live preview, `schedule {rule}`, MCP `when`, `ot snooze`):
  spans count from now, like `@3d` always has (`45m`, `2h`, `3d`, `2 weeks`, `1 month`); day words land at 9:00 (`fri`, `next fri`; a weekday with a time still ahead today means today: `mon 14:00` on a Monday morning), `tonight`,
  `tomorrow 14:00`, `tomorrow at 9` (after `at`, a bare 1 to 7 is the afternoon and `tonight at 9` is 21:00), `weekend` (Saturday), `next week` (Monday), `next month` (the 1st); a time alone (`9am`, `14:30`;
  today, or tomorrow when it's past); dates (`2026-03-01`, with an optional time); an optional leading `in` (`in 3 days`). A rule that isn't
  understood is refused with examples; one already past (`tonight` at 19:00) says so. Phones offer the quick choices and a date picker.
* **After another task** (`after: <id>` in the file; `POST /api/items/{id}/after`, MCP `wait_for_task`,
  `ot snooze <task> after <other>`): the task is *Waiting* ("after “X”") until X is done, then comes back with a
  reminder ("X is done: back to Y"). It replaces a snooze until a time (and snoozing or *Do now* replaces it). Refused:
  itself, a done task (either one), its own subtasks or parents, or a circle (also through a parent that can't finish while its subtask waits); the pickers only offer what's allowed (`GET /api/items/{id}/wait-candidates`). It comes back the same way when X is finished outside the app (a box checked in Obsidian, or by sync), and when X is deleted (\"X\" was deleted). In *Waiting*, tasks
  back at a time come first (soonest first), then those waiting for a task.

**Delivery.** Every 15s the server marks due, undelivered reminders as delivered, at most once and before sending,
so a crash never double-notifies. It then fans them out to desktop toasts and Teams. A failing integration never
blocks the others.

**Quick capture:** `!`/`!high`, `!!`/`!critical`/`!urgent`, `!low`, `@15m`/`@2h`/`@3d`/`@tomorrow` (next 9:00 local; before 4:00 that means this morning; `@3d` is exactly 72h, as before),
and any snooze rule written as one word after `@` (`@fri`, `@weekend`, `@2w`, `@next-week`),
`due:today`/`due:tomorrow`/`due:3d`/`due:YYYY-MM-DD` (17:00 local; `due:today` after 17:00 means 23:59). Malformed tokens (`due:2026-13-01`, `@+5m`) stay in the title on every platform. Unknown tokens stay in the title. Shared cases: `tests/fixtures/quick-capture.json` (generated by the C# core, read by Swift and Kotlin).

## 6. UX specification

### Windows sidebar
* **Placement** (menu › *Position*), remembered across restarts in `desktop.json`:
  * **Dock right** (default) or **Dock left**. The sidebar registers as a Win32 **AppBar**, so the shell shrinks the
    work area and maximized windows stop at the sidebar. It docks on any monitor (menu lists displays when there are
    several), and re-docks when the shell moves bars or the display or DPI changes. A docked sidebar is always on top,
    except next to a full-screen window (below).
  * **Full-screen windows** (Remote Desktop, a video, a slide show) ignore the reserved edge. While one is in front on
    the sidebar's monitor, the docked sidebar goes **behind** it instead of covering part of it, and comes back on top
    when it closes, is minimized, or a big window (a maximized app) is in front of it. Small windows, tool windows and
    always-on-top widgets in front don't count. Brought back on purpose (tray, `Ctrl+Alt+Space`), it stays in front
    until the full-screen window is clicked. Windows' own full-screen signal (`ABN_FULLSCREENAPP`) only triggers a check.
  * **Make room for full-screen Remote Desktop** (*Position* menu, on by default, docked only): a full-screen Remote
    Desktop session (mstsc or Windows App's msrdc, window class `TscShellContainerClass`) that the user is **in**
    (foreground) is resized to the monitor's work area, so it sits next to the sidebar. A session in the background is
    never touched. Each window is resized at most once: a client that is still full screen three seconds later (or a
    user who makes it full screen again) is left alone, and the sidebar goes behind it; while a resize is pending the
    sidebar stays on top. Only a session filling just that monitor is
    fitted (one spanning every monitor isn't squeezed onto one). The resize is asynchronous, so a hung client never
    blocks the sidebar. Fitted sessions follow the room whenever the sidebar re-docks (collapsed, opened, other edge, DPI
    or taskbar change), and go back to full screen (on the monitor they're on now) when the sidebar is hidden, floats,
    moves to another monitor, the option is turned off, or the app quits. The app's own window counts like any other:
    maximized over a full-screen session, the sidebar stays on top next to it. `ONETHING_REMOTE_DESKTOP_CLIENTS` (comma-separated process names)
    replaces the list of clients; system tests use it so a real session is never resized.
  * **Hide the sidebar** (menu, or the tray icon's menu): gives the screen edge back and keeps running in the
    notification area; hidden from its own menu, the tray icon says how to bring it back. It stays hidden after a
    restart (starts straight in the notification area, without a notification, so *Start with Windows* doesn't post
    one at every sign-in) until it's shown again: tray icon, `Ctrl+Alt+Space`, or starting the app again.
  * **Float as a window:** a normal movable, resizable window (drag the header, resize from any edge) with an optional
    **Always on top**. Its bounds are remembered and pulled back on screen if their monitor is gone. Floating gives the
    screen edge back.
  * The collapse chevrons point toward the docked edge.
  * **Displays come and go.** The *Position* menu lists the connected displays (with the model name when the monitor
    reports one) each time it opens. The chosen display is saved by its stable device path, which survives
    reconnects and Windows renumbering `\\.\DISPLAYn`. If that display is unplugged, a docked sidebar moves to the
    primary display and returns to the chosen one when it is plugged back in; a floating window is pulled back on
    screen. The saved choice is never overwritten by the fallback. Placements saved by older versions (by device
    name) are still understood.
* Layout, top to bottom:
  1. Header: collapse to a 56px strip, open dashboard, and a menu.
  2. Group tabs with counts and an attention dot.
  3. Quick capture.
  4. Status line.
  5. **Do this now** card.
  6. Collapsible sections: Do now, Waiting, **Done today**, Workstreams (progress bars; click for the report), and Recent
     notes.
  7. Focus timer bar.
* **Done today** (sidebar, web Today, Mac/iOS, Android): what was finished since local midnight, newest first, with a
  cheer that grows with the count ("First one done. Nice start!" … "12 done today. What a day!"). A subtask shows its
  parent. A parent finished together with its subtasks is one entry (the subtasks it closed aren't repeated); steps done
  on their own count. Archived tasks are left out. Each row can be reopened (a slip of the finger is undone in place).
  Same rule in every core (`Agenda.DoneToday`/`doneToday`), tested in C#, Swift and Kotlin. A repeating task done today
  counts too (it has moved on to its next time); *Reopen* on it means *not done after all*.
* Card actions: ✓ mark done, ↩ bring back, ⏰ snooze menu (the quick choices with when each ends, *Pick a
  time…*, *After another task*), 🗒 inline note (a sticky note: a pen would mean "edit"),
  ▶ focus, and ⋯ (dismiss reminder, add subtask, open in the app, open in the browser, copy link, open report).
  Clicking a title opens the task full size in the app window. The focus card shows every note (newest first, with
  author and when); other cards have a "2 notes ▾" toggle that shows them in place (web, Mac/iPhone and Android too). The focus card says
  **Mark done** (a bare "Done" read as "close this") and **Skip** (do something else first: it moves down one; it
  was an unlabeled arrow that looked like a menu). Same on the web, Mac/iOS and Android.
* Native toasts offer Done and Snooze 1h. `Ctrl+Alt+Space` opens quick capture from any app. There is an optional
  *Start with Windows*.
* The menu has: Position (with *Make room for full-screen Remote Desktop*), Always on top, Hide the sidebar, Connect an
  AI app, copy the MCP config, copy the API token (for the extension), connect Teams,
  and start with Windows.
* **System test:** `tests/system/placement.system-test.ps1` drives the real app with UI Automation through every
  placement, checks the live work area, window bounds, and topmost state, and verifies placement persists across a
  restart. It runs in Windows CI.

### Web dashboard and report (`http://127.0.0.1:5317`)
* The dashboard has the same sections as the sidebar, plus a filter box (`/`) with the search syntax (`TaskQuery`; the
  Android search box uses the same syntax, shared fixture `tests/fixtures/search.json`). Tag and label
  chips on cards filter on click. Files that need fixing are shown in a banner, and the footer shows where the
  markdown lives with an *Open in Obsidian* link.
* **Nothing needs a Save button.** The task drawer saves as you type (debounced; edits made during a save are saved
  after it; failures retry; everything is flushed when the panel closes or the page is hidden). Notes save while
  typed and stay one note until you start a new one.
* The drawer edits the title, labels (toggle chips, add new), tags, priority, deadline and details. It shows the subtask tree, rollout steps (one per line),
  attachments (button or drag and drop; images open inline, everything else downloads), the rich HTML version in a
  sandboxed frame, reminders, notes, and the task's version history with view and restore.
* **Where a task lives** is at the top of its details: its group and its parents as a path (each opens), **Move…** and
  **Show in tree**. A subtask also shows what it's **part of**: the parent (opens), its progress, and the other
  subtasks next to it (the current one highlighted). *Move…* picks a group and where in it: on its own (a top-level task)
  or under any open task of that group (searchable; never itself or its own subtasks). *Show in tree* opens Tasks with
  its parents expanded and the task focused and highlighted. In lists, a subtask's path opens its parent.
* **Quick add picks a group:** the picker by *Add* follows the tab shown (the first group under All); choosing another
  sends the new task there without changing the tab ("Added to Personal").
* **Repeating tasks** (details panel *Repeat*, REST/MCP `repeat`, frontmatter `repeat:`): a rule in words, the way the
  Obsidian Tasks plugin writes 🔁 rules: *every day*, *every 3 days*, *every weekday*, *every 2 weeks on Monday, Thursday*,
  *every month on the 15th* / *the last day* / *the last Friday* / *the 2nd Tuesday*, *every year on March 5*, optionally
  *when done* (counted from when it's done), *until 2026-12-31*, *for 5 times*. It's one task that **moves on** when it's
  done (no copies piling up): the completion is logged (Done today, reports), its deadline moves to the next day it falls
  on (same time), it waits until then (as many days before the deadline, at the same time, as it waited before; else from
  the start of that day), its subtasks open again, and this time's reminders, timer and *Doing* end. Missed days are
  skipped (the next time is after today); done early, the next time is the one after the current. A rule that has ended
  (*until*, the last of *for N times*) completes it for good. Its day is fixed at its first time (*every month* due on
  the 31st becomes *every month on the 31st*: after February's 28th it's back on the 31st, and a snooze doesn't move it).
  *Reopen* right after puts back the deadline and wait it had (kept as `repeat-undo:`), the subtasks done since its
  time before (they're done again), and the reminders moving on put away (pending again). Finishing the last step of a
  repeating in-order task moves the task on (its steps open again); a step of an in-order task doesn't repeat on its own.
  Reopening that step (Undo on "Done: <step>") puts the task back to this time with the other steps still done (shared
  fixture `tests/fixtures/undo.json`, C#, Swift and Kotlin).
  Checking it in Obsidian (`status: done`) moves it on the same way, each time. Cards, board, tree and phones show 🔁.
  Same rules in C#, Swift and Kotlin (`Recurrence`; shared fixture `tests/fixtures/repeat.json`), in the user's time
  zone (calendar days, so clock changes keep the time of day).
* **Plan** (an extra, on by default; web `#/plan`, `Alt+6`): a day or week time grid per group. Drag a task from *Not
  planned* (Do now's tasks without a block still to come) onto a time (15-minute steps) to plan a block of work on it;
  drag a block to move it, its bottom edge to change its length (5 min to 12 h), click it to open the task. Deadlines show
  on their day, a line shows now, done blocks are ticked. Each group keeps its view (day or week, work hours or the whole
  day, work hours and days, week starting Monday or Sunday). While a block is on, its task is *Do this now* (after a due
  reminder, ahead of the arranged order; shared scenario `planned.json` in C#, Swift and Kotlin). The task panel's
  *Plan* field sets it by keyboard; cards show `🗓 10:00–10:30`; a repeating task's block moves with it. Stored as
  `plannedAt`/`plannedMinutes` (JSON), `planned:`/`planned-length:` (frontmatter); REST `POST /api/items/{id}/plan`,
  `GET /api/plan`; MCP `plan_task`.
* **Energy** (an extra, on by default): a task takes 1 to 3 ⚡ an hour (`energy`; not said counts as 1), and a day has
  `energyPerDay` ⚡ (default 10, kept with the board: `config.json` in the vault, which syncs, `energyPerDay` in the JSON, so every app
  and computer uses it). A day's energy is what its planned blocks take: each block's hours on that day × its task's ⚡,
  done ones included, archived ones not, a block over midnight counting on both days (`Energy.PlannedOn`; same rule in
  Swift and Kotlin, shared fixture `tests/fixtures/energy.json`). Cards show `⚡⚡`; Today's summary says
  "⚡ 4.5 of 10 planned today" (in red, with by how much, past it); each Plan day head has a meter that turns red past it.
  Set a task's energy in its details (web, Android), ⋯ › Energy (sidebar, Mac/iPhone), `ot edit --energy`, or MCP
  `update_task`; the day's in Settings › Energy (web, Android), the sidebar menu, ⋯ on Mac/iPhone, `ot energy 8`, `PUT
  /api/energy` or MCP `set_daily_energy`. Frontmatter `energy: 2`; GitHub label `energy:2`. Today's line shows once
  something is planned today (every app).
* **Hooks and events** ([hooks.md](hooks.md)): `hooks.json` in the data folder (this computer only; edited in Settings ›
  Hooks, with `ot hooks`, by AI agents through MCP `add_hook`/`list_hooks`/`set_hook_enabled`/`remove_hook` or the REST API,
  or by hand) runs a command (PowerShell on Windows, bash elsewhere; the event as JSON on stdin) or posts
  to a URL when something happens: `taskCompleted`, `reminderDue`, `noteAdded`, `focusEnded`… with filters by group,
  tag, label and who did it. Events come from the activity log (app, agents, Obsidian, sync), each once, also across
  restarts; hooks run one at a time in the background with a timeout. A hook an agent (anything with the API token, or
  `ot --as`) adds or changes waits for approval (`needsApproval`) that only the signed-in app or `ot` can give; the app
  shows exactly what it would run. Settings › Hooks lists the hooks (on, off, needs approval), adds and changes them, shows
  their last runs and tries an event. `GET /api/events` streams every event (server-sent events) to extensions
  and scripts; server plugins subscribe to `HookService.Published`.
* **A task's look** (details panel): the button by the title picks an **icon** (a grid of common emojis, or type/paste any, or *No icon*); the **Color** row picks *Auto* (its first label's color), a palette color, or any color. Cards, board cards and tree rows show the icon before the title and the color as a stripe and a light tint; the sidebar shows both too.
* **Board:** drag the background (a column header, the space between cards) to move around it like a canvas, sideways
  and up and down; cards, buttons and fields keep their own behavior. Columns snap into place only on touch screens
  and narrow windows.
* `report.html?id=…` shows the task tree, progress, and a day-grouped timeline with linked sources. It is printable.
* Responsive layout down to phone width. It follows the system dark mode and respects reduced motion.

### Windows sidebar: tasks folder and notes
* Card notes save themselves after a short pause and keep updating the same note; Enter finishes it.
* Cards show labels (colored) and tags. The card menu opens the task in Obsidian or shows its markdown file.
* Menu › *Tasks folder* shows where tasks live, opens the folder or Obsidian, keeps tasks in an Obsidian vault found on
  this machine (`<vault>/One Thing`), or chooses another folder (the app restarts to switch).

### macOS and iOS (SwiftUI)
* Same sections and actions. The iOS app uses local notifications. The macOS app adds a menu bar glance with the focus task, Done, Later, and capture.
* **The window** (`AppShell`): a `NavigationSplitView` on iPad and Mac with the web's views in its order (Today, Board, Tasks, Done, Reports, Plan; ⌘1–⌘6), the groups (counts as on Today's tabs, color, context menu Rename/Color/Delete, a task dropped on one moves there: `OutlineMoves.toGroup`), Search (the shared syntax; it also narrows Board, Tasks and Done) and Settings (a Settings scene on the Mac). A task's details open in a column beside the view (40% of the window, 320–520 pt); compact width (iPhone, narrow Split View or Slide Over) shows the views as tabs (Today first, the rest under More) with a group menu, and the details as a sheet. Layouts follow the size class and the window's width, never fixed widths. Commands (`OneThingCommands`, also the iPad's keyboard shortcuts): ⌘N quick add, ⌘F search, ⌘↩ mark done or reopen, ⌘L later in an hour, ⇧⌘L tomorrow morning, ⌘I details. Every task has one context menu (Mark done/Reopen, Later with the snooze choices, timer, Open details, Open in New Window via `WindowGroup(for: UUID)` where multiple windows are supported, Move to a column or group, Copy link, Archive/Unarchive, Delete after asking). Pointer hover highlights rows and cards. Do now reorders by drag (`/api/now/order`).
* **Board** (`boardmodel.js` ported as `BoardColumns`): Inbox, Next, Doing, Done (cards finished in the last 14 days, newest first); as many columns side by side as fit at about 250 pt, the rest scroll. Cards show title and icon, group color, priority, repeat, labels, three tags, due/overdue, waiting, subtask progress and time; timer, Mark done (Archive in Done) and a Move menu. Drops and Option-arrows make the web's requests (`/stage`, `/complete`, `/reorder`); Doing shows n/3 and the web's message past three. New tasks start in Inbox (as in C#).
* **Tasks** (`outlinemodel.js` ported as `OutlineMoves`): the tree as rows with disclosure, check box, inline rename, chips and facts, a ⋯ menu (timer, add a subtask, details, up, down, into the task above, out one level); keys as on the web (Return rename/next, Tab/⇧Tab, ⌥↑/↓, Space, ←/→). *Show finished*, expand and collapse all; expanded rows are remembered.
* **Done**: finished top-level tasks by day (Today, Yesterday, the date) with time spent, Reopen and Archive; the Archive tab with Unarchive; a find box.
* Moves (`BoardMove`: complete, stage, move, reorder, reorderAfter) run on the device's board in one save, or as the same requests in order on the server; shared cases in `tests/fixtures/board-moves.json` (generated by the C# core).
* Task details edit every field the web drawer has except tags, labels, icon, color, attachments and history (title, details, priority, deadline, repeat, energy, group, steps in order, hours between steps), and offer Later, Remind me, Mark done/Reopen, Time this task, Archive/Unarchive and Delete (asks first). Subtasks open in place at any depth; a waiting task says why. Groups can be renamed, recolored and deleted (tasks move). Search follows `TaskQuery` (shared fixture `tests/fixtures/search.json`). A Settings screen holds appearance, energy in a day, focus lengths and breaks.
* **Plan** (`PlanView`): the web Plan view's grid (`PlanGrid`: week or day, work hours stretched to show blocks outside them, work days, Monday or Sunday weeks, 15-minute snapping, 15 minutes to 12 hours when resizing; iPhone (compact width) shows a day). Blocks, deadlines, *Not planned* and *Later* come from `PlanDays` (port of `PlanDays.cs`, which `GET /api/plan` uses; shared fixture `tests/fixtures/plan.json`), planning from `TaskBoard.plan/unplan` (5 minutes to 12 hours, 30 kept as none, done tasks refused; same fixture). Drag and drop (touch, pointer), long-press-then-drag to move a block on touch, a resize handle, a context menu, VoiceOver actions and arrow keys; *Plan…* opens a sheet (task, start, length). On the Mac (server), planning is `POST /api/items/{id}/plan` (`ServerAction.plan/.unplan`). Plan settings (view, hours, work hours/days, week start) are kept per group on the device (UserDefaults), not in the server's settings.
* **Reports** (`ReportsView`, Swift Charts): range 7 days / 4 weeks / 3 months / year / custom dates; KPIs, time per day (stacked focus/other, weekly for ranges over 120 days), by group (donut), top tasks, weekday × hour heat map, active-days calendar (90 days or more), and the Timeline (work bars, created→done/due line, ◆ due, ● done; tap a row to open the task). The numbers are `TimeReport` (port of `TimeReport.cs`) computed on the device from the board (its time entries and focus activity are in `/api/export`), so it works the same in every mode and offline; shared fixture `tests/fixtures/reports.json` (across midnight and the change to winter time, a running timer, focus, archived tasks).
* The macOS app runs the bundled server (`OneThing.Web --parent-pid`) and works on its tasks folder through the REST API (reads `/api/export`, sends changes), so it syncs like Windows (docs/sync.md, "On a Mac"). iOS keeps its tasks in a tasks folder on the device (`VaultStore`, `Application Support/OneThing/vault`; `board.json` is imported once and set aside as `board.json.migrated`), synced on its own (below). Settings says which it is (syncing on its own, or only on this device); switching never deletes tasks. Phones don't connect to a computer: they sync through the same places computers do.
* **iPhone and iPad: Sync with your other devices** (Settings › Sync; docs/design/phone-sync.md). The phone is a sync device of its own (`SyncEngine` over the one `VaultStore`). Places: a folder picked with `UIDocumentPickerViewController` (iCloud Drive › Apps › OneThingSync, or a folder holding it, or *Create it here*), kept as a bookmark and used through `FolderRemote` + `CoordinatedFolderAccess(securityScoped:)` at `<OneThingSync>/<library>`; or a gist (`GistRemote`) with a pasted token (`gist` scope; a button opens GitHub's token page), kept in the Keychain. Library: "One Thing" by default, editable (cleaned as on computers). The first sync is `SyncEngine.join` (a leftover `board.json` comes in beside what came; nothing is published before a complete pull). Schedule: launch and foreground, 5 s after a change (debounced), every minute while open, `BGAppRefreshTask` `com.onething.ios.sync` in the background; syncs run off the main actor, and after one the board is read again from the store and notifications are rescheduled. The screen shows last synced, *Sync now*, other devices (*Forget*), problems (including iCloud files not downloaded yet, which are requested), conflicts (read only: "Open it on a computer to choose"), and *Turn sync off* (keeps the tasks).

* **Widgets** (WidgetKit; extensions `OneThingWidgets-iOS`/`-macOS` in `apple/Widgets`, views in the package's `OneThingWidgetUI`): **Now** (systemSmall, systemMedium; iOS also accessoryRectangular and accessoryInline) and **Today** (systemLarge). The app writes `WidgetSnapshot` (port of `WidgetSnapshot.cs`: titles, group names and colors, ids and times only) to the App Group (`group.com.onething`; on a Mac `$(TeamIdentifierPrefix)group.com.onething`, read from the Info.plist's `WidgetAppGroup`), excluded from backups, whenever the board changes (any mode) and every 15 minutes, and calls `WidgetCenter.reloadAllTimelines`. The snapshot holds the Do now list and Later today at every moment they change in the next 24 hours (snooze ends, reminders, planned blocks starting and ending, deadlines, midnight; at most 24 frames, 8 tasks per list), each worked out with `Agenda.build`; the timeline has one entry per frame. Shared fixture `tests/fixtures/widget.json`. **Done** is `MarkDoneIntent` (`Button(intent:)`, `openAppWhenRun`): the widget can't reach the tasks in any mode (the app's own storage, the Mac's server), so it writes a request file to the App Group (`WidgetShelf`), the widgets leave the task out at once, and the app (brought forward) applies it with `BoardModel.applyWidgetRequests`: the same change as Mark done (local `TaskBoard.complete` as the user, or `POST /api/items/{id}/complete`), waiting while the tasks folder opens or the Mac's server isn't answering; a request is removed before it's applied and skipped when the task was done since it was made (`WidgetDoneRequest.outcome`, so a repeating task never moves on twice). A task with open subtasks, or a change that's refused, opens in the app instead.

### Android (Jetpack Compose)
* Today: the focus card, Do now, Waiting, Done today (with the time each was done), Workstreams (tasks with subtasks:
  a progress bar, how many are done, to do now and waiting, when the next comes back) and Recent notes (the last five;
  tap one to open its task). Group tabs; tapping the chosen tab (or *Edit*) renames it, sets its color or deletes it,
  after asking which group gets its tasks. 🔍 opens a search box with the shared syntax (`TaskSearch`, same as
  `TaskQuery`); results list open tasks first and open on tap.
* **Task details**, full screen (tap a title, a result, a workstream, a note, a link or a reminder's notification):
  title and details (saved on Close, Back, or Save), priority, deadline (date and time pickers, Clear), energy, repeat
  rule (typed, or picked from the web app's list; a rule it doesn't understand says why), group, Mark done (asks
  before closing open subtasks) or Reopen, Later, Do now, Archive / Unarchive (finished top-level tasks), Copy link,
  Delete (asks first, and says it can't be undone and that archiving keeps it). Subtasks with check boxes (a step that
  must wait is greyed), each opens; the breadcrumb opens the parent. *Steps in order & rollout steps*: the in-order
  switch, hours between steps, and steps added one per line. Reminders: the pending ones (Dismiss) and *Remind me…*
  with an optional message. Notes: add, and every note newest first.
* **Reminders notify.** The next pending reminder (a snooze with a reminder is one) has an alarm, exact when the phone
  allows exact alarms. When it rings, each reminder due is shown as a notification with *Mark done* (*Open* when the
  task has open subtasks) and *In an hour*; tapping it opens the task. It's marked delivered (`notifiedAt`, once, like
  `ReminderDispatcher`) only when shown: with notifications off it stays pending on Today and is told once they're on.
  While the app runs, its board does this; with it closed, the alarm and the buttons change the tasks folder through
  the same store. After a restart or an update, the alarm is set again and reminders missed meanwhile are delivered. The app asks
  for notifications (and, once, for on-time alarms) at the first snooze, reminder or focus session.
* *Later* offers the quick choices, *Pick a time…* (a date picker where past days are off, then a time picker that
  won't save a time already past) and *After another task…*. A snooze or wait can be undone from its message, like
  *Mark done*.
* **Settings** (⚙): sync with your other devices, theme (System, Light, Dark; kept on the
  phone), energy in a day, the focus timer's lengths (focus, break, long break; 1–180 minutes, kept with the board), and
  whether notifications and on-time alarms are allowed.
* **The board is a tasks folder** on the phone (`filesDir/vault`, the vault format of `docs/vault-format.md`, a port of
  `VaultBoardStore`; [design](design/phone-sync.md)). One `VaultStore`
  serves the whole app: the screens, the reminder alarm and notification buttons, the restart receiver and sync. A
  change runs on it (only the touched task files are written, all or nothing) and the screen shows a copy. On the first
  start of this version, `board.json` (and its `.bak`) is moved in once when the folder is new, then set aside as
  `board.json.migrated` (never deleted); one left behind by an interrupted move is imported beside (tasks already there
  are skipped). A `board.json` from a newer app version is neither moved nor changed (changes are off).
* **Two modes** (Settings says which): only on this phone, or syncing on its own. Switching never deletes tasks.
* **Sync on its own** (Settings › Sync with your other devices; phase 2 of the [phone sync design](design/phone-sync.md)):
  the phone is a sync device of its own through a GitHub gist (`GistRemote`, the same `device-<id>.json` bundles as the
  computers), merging three-way like `SyncPlanner`. Sign-in: a pasted classic token with the `gist` scope (a button opens
  `github.com/settings/tokens/new?scopes=gist&description=One%20Thing`; no OAuth device flow, as there's no OAuth
  app), kept sealed with an Android Keystore AES-GCM key; the gist id in the app's settings; `SyncState` in
  `filesDir/sync`. The library defaults to *One Thing* (the computers' default) and can be changed (a new library is
  joined afresh). The first sync with a place is `SyncEngine.join`: pull only, then import `board.json` if still there,
  then publish; an incomplete pull publishes nothing (*Waiting for everything from your other devices…*). Syncs: on
  launch and resume, 5 s after the last change (debounced), every minute while open (`SyncSchedule`), and a WorkManager
  periodic job (15 minutes, network) with the app closed, plus one 5 s after a change made from a notification; all on
  one background thread. After a sync that changed something, the board is shown again from the same store and the
  reminder alarm set again. The Sync screen: last synced, *Sync now*, problems (incl. contents not arrived yet, a token
  GitHub refused, with a field for a new one), other devices with *Forget*, conflicts listed read-only (*open it on a
  computer to choose*; *Got it* hides one), the library, *Turn sync off* (tasks stay; the token is forgotten). Debug
  builds can also sync through a folder in the app's external files (`OneThingSync/<library>`), to try it against a
  computer's `Apps/OneThingSync` folder without GitHub.
* **Sync identity:** `SyncState` is kept per place and library (`filesDir/sync/<place>-<hash of library>`; the device id
  in `filesDir/sync/device.json` is shared), so another library starts with nothing agreed, published or deleted. Backups
  and device transfers (`dataExtractionRules`, `fullBackupContent`) leave out `sync.xml` and `files/sync/`, and keep
  `files/vault/`. If sync state still arrives from another install (its id is
  kept in no-backup storage), it's set aside as `sync.copied` and the phone joins as a new device.
* **Opening:** the tasks folder opens off the main thread (*Moving your tasks into a tasks folder on this phone…* the
  first time). The move builds the folder in `vault.moving` and renames it into place; any failure leaves `board.json`
  untouched and turns changes off with why, never a crash.
* **Widgets** (Jetpack Glance, `Widgets.kt`): **Now** (responsive: small, or wide with the next three and *N more
  today*) and **Today** (resizable, a scrolling list with Done on each, then *Later today* with times). They run in the
  app's process and read the board the app uses (the tasks folder through `BoardHub.store`), shown with `WidgetSnapshot` (the Kotlin port; shared fixture `tests/fixtures/widget.json`).
  They're updated when the board changes (the app, notification buttons, sync) and, with a WorkManager
  job, when their list next changes on its own (`WidgetSnapshot.nextChange`). **Done** is a Glance action callback doing
  what the notification's *Mark done* does: `BoardHub.change` (through the running app, else the tasks folder) with
  `complete(id, Actor.USER)`. One at a
  time, and only for a task that's to do now (`WidgetSnapshot.canMarkDone`), so a second tap or an old widget does
  nothing. A task with open subtasks shows *Open*; when the change is refused, a toast
  says why and the app opens on the task.

### Browser extension (Edge/Chrome side panel)
* Group tabs, the focus card, Do now, Waiting, and quick capture. A note can attach the current page's URL and title.

### Visual design (all platforms)
One design language, adapted to each platform's native controls (Fluent on Windows, SwiftUI on Apple):
* **Calm canvas:** neutral surfaces with a soft indigo/pink ambient glow. Light and dark themes are both first-class.
* **One hero:** the "Do this now" card has a gradient hairline border (indigo → violet → pink). The border turns
  amber → rose when a reminder is ringing and green → cyan when all is clear. The card shows a priority pill,
  toned chips, and labeled primary and secondary actions.
* **Quiet lists:**
  * Rows show a haloed priority dot, the title, the breadcrumb, and toned chips (step, back in, due, overdue, notes).
  * Row actions are icon buttons that stay subdued until hover or focus. On touch they are always visible.
* **Segmented tabs:** group tabs sit in a pill track, and the selected tab is raised. Each tab has a count badge
  and an amber dot when a reminder is waiting there.
* **Glanceable timer:** a progress ring shows the Pomodoro phase: rose for focus, green for breaks.
* **Workstreams** lists only tasks with subtasks, with a gradient progress bar. Single tasks already live in Do now and Waiting.
* **Motion:** motion is subtle (under 250ms) and disabled under `prefers-reduced-motion`.
* **Web security:** the web UI keeps a strict CSP. Icons are built with DOM APIs, and there is no inline script or
  style.

### Color and accessibility
* Priority colors: critical `#e11d48`, high `#f97316`, normal `#3b82f6`, low `#94a3b8`. Amber marks attention.
* Every icon button has an accessible name. Text never relies on color alone: priority is also shown in the tooltip or label.

## 7. Integrations

### AI apps (MCP, CLI, OpenAPI)
AI apps reach the tasks four ways (setup per app: [`docs/ai-connectors.md`](ai-connectors.md)):

* **`onething mcp`**: the MCP server over stdio, launched by Claude Desktop, Claude Code, Copilot CLI and VS Code. It works
  on the vault directly (shared lock), so it works whether or not the app runs. Shipped next to the Windows app, as a
  `dotnet tool`, as self-contained binaries, and as a Claude Desktop extension (`.mcpb`).
* **`ot`** (short for `onething`; both are installed): the same operations from a terminal (`ot now`, `ot add …`, `ot done …`); `--json` returns the API shapes and
  `--as <agent>` attributes changes. Tasks are named by id prefix or title words; ambiguity is an error, never a guess.
* **`/mcp`**: streamable HTTP inside the running app, `Authorization: Bearer <token>`.
* **REST + OpenAPI** (`/openapi/v1.json`, and Swagger 2.0 at `/openapi/swagger2.json` for Copilot Studio): a curated
  subset without deletes, settings, or sign-in. Every `/api` route must be classified as described or hidden (a test
  enforces it).

**Connect an AI app** (sidebar menu, ✨ in the web header) lists a ready snippet per app with the real path of `onething`.
A plugin marketplace in this repository (`.claude-plugin/`, `.github/plugin/`) installs the MCP server plus a skill
that teaches agents how to help (capture in the user's words, make big things small, log progress, defer, never
delete) in Claude Code and Copilot CLI. Cloud apps (ChatGPT, Claude.ai, Copilot Studio) need a public HTTPS tunnel;
the docs explain the options and risks. The server sends the same short instructions to every MCP client. Tools:

| Tool | Purpose |
|---|---|
| `get_dashboard` | Now / waiting / overview / notes / timer, optionally for one group |
| `list_tasks`, `get_task`, `get_report` | Read tree, details, timeline |
| `create_task` | Task or subtask (group, priority, deadline, optional defer) |
| `add_steps` | Gated rollout steps with `stepDelayHours` |
| `add_note` | Progress log with optional source URL |
| `schedule_next_action`, `add_reminder` | Defer and remind |
| `complete_task`, `reopen_task`, `update_task` | Status and fields (incl. tags, labels, icon, color and repeat rule; completing a repeating task moves it on, reopening undoes that) |
| `search_tasks` | The shared search syntax (`#tag`, `label:x`, `group:x`, `is:done`, words) |
| `move_task`, `list_labels` | Reorganize the hierarchy; curated labels |
| `attach_text`, `get_rich_html`, `set_rich_html` | Text attachments and the rich HTML version |
| `task_history`, `restore_task_version` | Versions of a task's file, and going back (itself undoable) |
| `vault_info` | Where the markdown lives, files that need fixing, and the format guide (for agents with file tools) |

Every change is attributed as `Agent: <client name>`. There is intentionally **no delete tool** (and over REST only the
owner deletes), and completion can be undone with `reopen_task`. An agent can switch off or remove only the hooks it
added. There is no tool that copies arbitrary local files into the vault (that would let a
remote connector read files from the machine).

### Obsidian
The vault is plain Obsidian-flavored markdown: frontmatter properties, Obsidian Tasks tokens, callouts, block ids and
links, wiki-link attachments. Point the app at a folder inside an Obsidian vault (Menu › *Tasks folder*) and edit
tasks in either place. The web UI and sidebar open any task in Obsidian (`obsidian://open?path=…`).

### GitHub Issues connector
Groups stay in step with private GitHub repositories (issues, both ways; everything else as prefixed labels, see
`docs/connectors.md`). Each connected group has a label in its repository, `group:<name>` (at most 50 characters, a
number added when two groups here would get the same one), fixed when it's connected: every issue it makes gets it,
it lists only issues with it, and an issue relabelled to another group moves its task to that group (never deleted;
until that group syncs, the task stays where it was). So several groups can share one repository, and "every group"
can put all of them in one shared repository instead of a `todo-<group>` each. `group:` labels are never tags and
never pruned; time labels are pruned only when no issue in the repository uses them. A group connected before labels,
or the first group in a repository without any `group:` labels, labels the issues that have none. With the tasks on
GitHub, other computers find groups by their `group:` labels in repositories with the `onething` topic.

### Plugins and Ask AI
Every optional feature is a built-in plugin (Ask AI, Connect AI apps, focus timer, Plan, version history, Obsidian,
browser extension, sync, connectors, Copy for Loop, Teams, Send feedback) that can be switched off; the choice lives in `plugins.json`.
Off applies at once everywhere (UI, API, background work); extras without background work also come on at once, the
others at the next start. Ask AI drives GitHub Copilot
CLI or Claude Code over the Agent Client Protocol with One Thing's MCP tools attached, or a model reached with an API
key (OpenAI- or Anthropic-compatible; keys encrypted on this computer) with the same tools. Reading needs no permission;
changing tasks asks, showing what would change (once or for the chat); anything else always asks. Chats are kept and
can be searched, reopened (agents resume their session with `session/load` when they can), renamed and deleted with
undo. A chat stays with the agent or model it began with. Details: [`docs/plugins.md`](plugins.md).

### Send feedback
One sheet on every platform (the **Send feedback** plugin): kind chips **Bug**, **Idea**, **Other**; a multiline note
(at most 2000 code points, a counter in the last 200); an optional email hinted *Only if you’d like a reply. It’s shown with your note on GitHub.*; the fine
print *We’ll include the app version, your OS and its version, device model, language and screen size.*; **Send**. Each sheet makes one UUID v4
`clientID` and sends it again on every retry, so the relay files a note once.

* **Relay:** `POST https://issues.awesomehq.app/v1/issues` with `Authorization: Bearer pk_…` (the app's public key: it
  can only open issues in this repository; no GitHub token or GitHub API anywhere). Body: `message` (control
  characters except new lines and tabs removed, trimmed, cut to 2000 code points), `title` = `<Kind> · <Platform>:
  <message>`, `contact` (every control character removed, trimmed, cut to 200 code points;
  left out when empty), `clientID`, `labels` = `kind:…`, `platform:…`, `source:button|shake|page|qr` (a button or menu, a shake, a link on a web page, a QR code),
  and `metadata` in this order, leaving out what isn't known: Platform (lowercase), App version (`1.2.3 (45)`), OS,
  Device, Language, Screen, Source. Never ids, IPs, location, screenshots, logs or anything from the tasks.
* **Answers:** 2xx sent (*Thanks — we read every note.*, close); 408, 429, 5xx or no answer: keep the draft, *Couldn’t
  send — check your connection and try again.*; any other 4xx: a generic error. Shared cases (body and answers):
  `tests/fixtures/feedback.json`, run by C# (`Feedback`), Swift, Kotlin and the web app.
* **Web, Windows and Linux:** the browser never calls the relay. The web sheet posts to `POST /api/plugins/feedback`
  (owner's session only, 403 otherwise); the server adds the platform it runs on (Windows, macOS or Linux), its
  version, `<OS> <version>`, the processor type as Device, and the browser's language and screen size, then sends it
  and answers 202, 503 (try again) or 422 (failed). The sidebar (**⋯ › Send feedback…**), both tray menus and the
  browser extension open `/?feedback=button`, which opens the sheet (the extension shows its button only while the
  plugin is on, from `GET /api/plugins`). Also: Settings › Feedback and Ctrl K.
* **iPhone, iPad, Mac, Android:** a native sheet sending to the relay directly. Settings › Feedback everywhere; Mac
  Help › Send Feedback…; Android ⋮ on Today. Shake to send (iPhone, iPad, Android; source `shake`), on by default with
  a Settings switch, never while another sheet, a dialog or a break is on screen.
* **Debug builds** log the body (without `contact`) instead of sending: `ONETHING_FEEDBACK_SEND=1` (server, Xcode scheme) or
  `-PonethingFeedbackSend=true` (Android) sends; `ONETHING_FEEDBACK_URL` points the server at another relay (tests).
* Privacy: [`docs/privacy.md`](privacy.md) (and the store labels).

### Teams
Paste a Teams **Workflows** webhook URL (channel › Workflows › "Post to a channel when a webhook request is
received"). Reminders are posted as Adaptive Cards with the title, path, message, priority, due time, and an
*Open report* link. The URL must be https, is stored only in `settings.json`, and is never returned by the API.

### Browser
The extension uses the REST API with the API token. A deep link `/?item=<id>` opens a task drawer. Browsers sign in
with **single-use launch codes**: `POST /api/launch` returns `/auth?code=…&return=/path`, which is valid for
2 minutes and accepts local paths only. The long-lived API token therefore never appears in URLs, browser history,
or history sync.

### Task links (`onething://task/<id>`)
Every task has a link that opens the installed app on it (`TaskLinks` in the core, with the same rules in Swift and
Kotlin; shared cases in `tests/fixtures/links.json`). Only two shapes are followed: `onething://task/<guid>` (any case, with or without
dashes, a trailing `/`, `?…` or `#…` ignored) and the bare scheme (just the app); anything else is ignored, so a
crafted link can't reach another page or action. Copied from the task header (web) or a sidebar card (Windows) as
plain text plus HTML named after the task; returned as `link` on every task (REST, MCP), printed by `ot show`, opened
by `ot open`.

* A link is the only argument that counts when the app is started with one: its text can never add options.
* **Windows**: the app writes `HKCU\Software\Classes\onething` at each start (only the user's own instance: the
  default data folder and port). A second launch with a link hands it to the running one over a per-session named
  pipe (`PipeOptions.CurrentUserOnly`, ≤2 KB, validated again on arrival) and exits; the app window opens on the
  task. A link clicked inside the app window opens there.
* **macOS / iOS**: `CFBundleURLTypes` + `onOpenURL` open the task's details. **Android**: a `VIEW`/`BROWSABLE`
  intent filter (`singleTask`, so a link never starts a second copy of the board) opens the task's details. **Linux**: the `.desktop` file handles
  `x-scheme-handler/onething`; a running tray opens its window on the task through `/api/launch`.

## 8. Security (local server)

* Kestrel binds to loopback only. Requests with a non-loopback remote address or `Host` header are rejected, which blocks DNS rebinding.
* `/api` and `/mcp` require the bearer token (256-bit random, per user; file mode 600 on Unix) or the session cookie
  (`HttpOnly`, `SameSite=Strict`). Tokens are compared in constant time.
* The API token is never put in a URL. Browsers get the cookie through single-use launch codes, and `return` paths
  must be local and free of control characters.
* Mutations authenticated by cookie also need the `X-OneThing-Client` header. This forces a CORS preflight, so
  other localhost origins cannot forge writes. No CORS policy is enabled.
* **The owner and everything else.** The person at this computer is a session the app opened itself (launch links made
  in its own process, or asked for with the owner key) or a request that sends the owner key with the token (the Mac
  app; `owner-key` in the data folder, mode 600, never handed out). Anything holding only the token (an AI agent, a
  script) can read and change tasks, but not: delete anything (tasks, groups, labels, time entries, attachments), use
  Ask AI, switch extras, move the tasks folder, set the Teams webhook, change sync or connected apps (GitHub, Notion,
  To Do; reading them is fine), approve, turn down or try hooks, see hook runs or the secret part of a hook's address,
  or switch off or remove a hook it didn't add. Those answer 403. Its changes are recorded as `Agent: <X-OneThing-Actor>`,
  or `Agent: API client` when it doesn't say (never as you).
* Sign-in links asked for with only the token are capped (256 open at once) apart from the owner's, so they can't
  block the app's own.
* Strict CSP (`default-src 'self'`, no inline script, `frame-ancestors 'none'`), `nosniff`, `no-referrer`, and
  `Cache-Control: no-store` on the API. The DOM is built with `textContent` only. Links are http(s) only.
* Input limits: titles ≤300 characters, notes ≤10k, attachments ≤25 MB, rich HTML ≤2 MB. Unknown groups and ids
  return 404, bad input returns 400, and sequence violations return 409.
* Attachments: only raster images are served inline; HTML, SVG, and everything else download as
  `application/octet-stream` with a `sandbox` CSP. Attachment links that resolve outside the vault are refused, and
  removing an attachment only ever deletes (to the trash) files under `_attachments/` that no other task links.
* Rich HTML is untrusted: it's served with `sandbox; script-src 'none'` and only framed by the app (sandboxed iframe,
  opaque origin, no scripts).

## 9. Platforms and build

| Platform | Tech | CI |
|---|---|---|
| Core, server, and view models | .NET 10, xUnit v3 (Microsoft Testing Platform), coverage gates (Core ≥90%, Server ≥85%, Desktop ≥85%) | Linux |
| Web and extension | Vanilla ES modules (no build step), `node --test`, Playwright e2e | Linux |
| Windows | WPF (.NET 10, Fluent theme), Win32 AppBar, toast notifications | Windows: build, tests, publish, and a real-app `--smoke-test` with a screenshot artifact |
| macOS and iOS | Swift 5.9, SwiftUI, XcodeGen | macOS: `swift test` (shared scenarios), iOS simulator build, macOS build and zip |
| Android | Kotlin, Jetpack Compose (min SDK 26, target 35), AlarmManager and notifications for reminders and the focus timer | Linux: `gradle testDebugUnitTest` (shared fixtures) and `assembleDebug` |

The **shared scenarios** in `tests/fixtures/scenarios/*.json` hold a board, a time, and the expected Now, Waiting,
focus, attention, and states. Both the C# and Swift test suites run them.

## 10. Roadmap

* **Next:**
  * Rotate the API token, and compact the activity log.
  * Phones sync on their own, with conflict-free merges (Android through a gist, iPhone and iPad through iCloud Drive or
    a gist; they never connect to a computer directly). Next: Keep mine / Keep theirs on phones, OneDrive on phones.
  * Signed and notarized installers (MSIX, TestFlight, notarized DMG).
  * Recurring tasks.
  * Keyboard navigation of cards.
* **Later:**
  * A Teams bot with actionable buttons (Done and Snooze from Teams), which needs an Entra app registration.
  * Widgets and Live Activities on iOS and macOS.
  * Calendar-aware scheduling.
  * Undo for recent actions.

## 11. Decisions

| Decision | Choice | Why |
|---|---|---|
| Restart vs. extend PR #1 | Restart on a new branch, reusing the spec and AppBar ideas | PR #1 was sample data only: no persistence, editing, notifications, MCP, or Apple app |
| Persistence | Markdown vault (one file per top-level task) with a private git history | AI-native and Obsidian-compatible; people, apps, and agents edit the same files. File-per-subtask was rejected (thousands of tiny files, clunky in Obsidian). |
| Subtask metadata | Obsidian Tasks tokens + hidden `%%{json}%%` on the same line | Everything about a subtask moves with its line; nothing is split between the body and frontmatter. |
| File writes | Never on read; only touched files; verified by content; all-or-nothing | Pointing the app at existing notes must change nothing, and no edit may be lost to a race. |
| Versioning | Private git repository per vault in local app data | Real history and restore without touching a user's own repository or Obsidian Git; never inside the synced folder, where two devices running git on one repository would corrupt it. |
| Apple stack | Native SwiftUI + Swift core port | Native feel and reliable CI. Parity with C# is enforced by the shared fixtures. |
| Web front-end | Vanilla JS served by the server | Instant load, no toolchain, strict CSP |
| Teams | Workflows webhook | Works without an app registration |
| Feedback | A create-only relay (issues.awesomehq.app) with a public key; the web app goes through the local server | A token that can open issues can also read them, and anything in an app can be extracted; the web app has no fixed origin to put on the relay's list |
| MCP transport | Streamable HTTP inside the running app, plus `onething mcp` over stdio | Local AI apps launch servers themselves and must work without the app; the vault's per-folder lock makes several processes safe, and the app picks their changes up. |

## 12. Requirement traceability

| Request | Status |
|---|---|
| Windows desktop app, always visible, reduces available screen (AppBar) | ✅ |
| MCP/Copilot interface so agents can contribute | ✅ MCP tools, attributed |
| Big tasks with subtasks | ✅ unlimited nesting |
| Reminders, notifications, deadline, priority, and notes per task and subtask | ✅ |
| Rollout example (steps 24h apart, task drops down until due, reminder when due) | ✅ sequential steps plus step delay, Waiting, reminders |
| MS Teams integration | ✅ reminder cards via webhook (bot actions are on the roadmap) |
| Notes in app, recent notes, full report and timeline in the browser | ✅ |
| Smart, not busy UI with collapsible panels | ✅ |
| Browser integration and a sidebar extension for Edge/Chrome | ✅ |
| Lightweight, fast, native | ✅ WPF, SwiftUI, vanilla web |
| iOS, macOS, and web versions | ✅ (Apple apps are local-only until sync lands) |
| Color coding and priority | ✅ |
| High-level glance: what to do now | ✅ focus card, workstreams, tab badges |
| Pomodoro | ✅ linked to tasks |
| Multiple task groups as tabs (Personal/Work/…) | ✅ follow-up request |
| Sidebar placement: dock left/right on any monitor, float with always-on-top, monitor hot-plug | ✅ follow-up requests |
| AI-native: data as markdown in folders, HTML version when needed | ✅ vault format v2, rich HTML companion |
| Obsidian (must) | ✅ the vault *is* Obsidian markdown; open in Obsidian; keep tasks in an Obsidian vault |
| Easy ordering: drag and drop, arrow keys/icons | ✅ Do now: drag, ↑/↓ buttons, Alt+↑/↓ (web and sidebar); subtasks in the web task panel; `put_first` for agents, `ot first` |
| Hierarchy, notes, attachments, tags, labels; intuitive and minimal | ✅ any depth with moves, notes, attachments, tags, colored labels, one filter box |
| Autosave, notes without a Save button | ✅ web drawer and notes, sidebar notes |
| Versioning ("use git or something like that") | ✅ private git history with view/restore (UI, REST, MCP) |
| Available to AI tools: CLI, API, Claude Desktop, Claude Code, Copilot CLI, VS Code, ChatGPT/Copilot Studio; "MCP connectors" | ✅ `ot`, `onething mcp`, `/mcp`, OpenAPI 3.1 + Swagger 2.0, `.mcpb` extension, plugin + skill marketplace, Connect dialog; cloud apps via tunnel (documented) |
| In-app terminal/chat driving Copilot/Claude CLI over ACP (warm, MCP loaded, choose when both installed); features as plugins | ✅ Ask AI plugin (dashboard panel + sidebar box; Copilot and Claude Code; permissions; warm agent); every optional feature is a switchable plugin ([`docs/plugins.md`](plugins.md)) |
| Ask AI as a full chat with sessions and history; ACP plus API keys for OpenAI- and Anthropic-compatible APIs | ✅ chats kept, searched, reopened (ACP `session/load` or a primer), renamed, deleted with undo; API models with presets, encrypted keys, same tools and permissions (tested live with Copilot; Claude untested on the dev machine) |
| Paste media into tasks and notes | ✅ pasted pictures/files are attached and embedded (`![[…]]`, Obsidian-style) in details and notes, web and sidebar; into quick add (they wait under the box and go into the new task's details; a picture alone names the task) and a task's title (goes into its details). A paste counts as a picture unless real text comes with it (an office copy); a link, file name/path, blank text or HTML that's only the image (browser/Teams "Copy image") doesn't count. Sidebar: Ctrl+V, Shift+Insert and right-click › Paste, also image files copied in Explorer |
| Browser extension for Edge/Chrome and Safari, with a setup guide while unsigned | ✅ one source, side panel (Edge/Chrome) and popup (Safari app built in CI); guided setup and pairing codes with per-browser access |
| Notion, MS To Do, Loop, Apple Notes | ⏳ planned as sync plugins |
