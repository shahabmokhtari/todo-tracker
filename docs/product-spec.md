# Todo Tracker: product plan, design, and specification

Traces to the original request in [`request.md`](request.md). Section 12 maps each requirement to its status.

## 1. Vision

Todo Tracker is an **ADHD-friendly command center** for people juggling many workstreams, AI agents, chat, and
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
| Focus | Pomodoro on the current task. A toast suggests a break, and completed sessions are logged on the task. |

## 3. Architecture

```
┌──────────────────────────── Windows ─────────────────────────────┐
│ TodoTracker.Windows (WPF, AppBar)                                 │
│   ├─ TodoTracker.Desktop (MVVM view models, platform-neutral)     │
│   └─ embeds TodoTracker.Server ── 127.0.0.1:5317 ─┬─ Web UI (SPA) │
│                                                   ├─ REST /api    │
│                                                   └─ MCP  /mcp    │
│ TodoTracker.Core (domain, agenda, store) ◄── all of the above     │
└───────────────────────────────────────────────────────────────────┘
   ▲ REST (loopback + token)            ▲ MCP (streamable HTTP + token)
   │                                    │
 Edge/Chrome side panel extension    Copilot CLI / VS Code / agents

TodoTracker.Web  – the same server as a standalone process (no WPF); used by CI e2e tests.
apple/           – TodoTrackerKit (Swift port of Core) + SwiftUI apps for iOS and macOS.
```

* **Markdown vault.** The board lives in a folder of markdown files: one folder per group, one file per top-level
  task, Obsidian-compatible. The full format and the app's rules for touching files are in
  [`vault-format.md`](vault-format.md). In short: reading never writes, a change rewrites only the files it touched,
  saves are verified against the files' content and are all-or-nothing, edits made outside the app (Obsidian,
  editors, agents) are picked up and acted on, and broken files are reported and never overwritten.
* **Vault location.** `Documents/Todo Tracker` by default, or a folder chosen in the app (for example
  `<Obsidian vault>/Todo Tracker`), `TODOTRACKER_VAULT`, or `<data>/vault` with a custom data folder. A `board.json`
  from earlier versions is imported once (and renamed to `board.json.migrated`).
* **Several processes.** The app, the `tt` CLI, and MCP servers can share a vault; a per-vault lock in local app data
  (never in the synced folder) serializes writes. One server runs per data folder (`instance.lock`).
* **History.** Every change is kept as a version in a private git repository on this device (local app data, never in the synced vault), saved a
  few seconds after changes settle and checked every two minutes for edits made outside the app. Any task can be
  viewed or restored from its history. Off when git isn't installed.
* **Data folder.** `%LOCALAPPDATA%\TodoTracker` (override with `TODOTRACKER_DATA` or `--data`) holds
  `settings.json` (Teams webhook, vault folder), `api-token`, and `desktop.json` (window placement).

## 4. Domain model

| Entity | Fields |
|---|---|
| **Board** | `groups[]`, `labels[]`, `items[]` (roots), `activity[]`, `pomodoro` |
| **Group (tab)** | `id`, `name` (= its folder; unique, case-insensitive), `color` (`#rrggbb`) |
| **Label** | `name` (≤40), `color`. Curated and colored; using a new one defines it with the next palette color. |
| **Task** | `id`, `title` (≤300), `details`, `priority` (low/normal/high/critical), `createdAt`, `completedAt`, `deadline`, `nextActionAt` (defer gate), `sequential`, `stepDelayMinutes`, `groupId` (roots only), `tags[]` (free-form, nested with `/`, ≤20), `labels[]`, `attachments[]`, `reminders[]`, `notes[]`, `children[]` (any depth). Tasks can move anywhere in the hierarchy. A top-level task can have a rich HTML version. |
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

**Do now** holds actionable items plus attention items. Attention items (a due reminder) come first, as their own
block. After that, **the person's own order wins**: they arrange Do now by dragging cards, with ↑/↓ buttons, or
Alt+↑/↓ (agents use `put_first`, the CLI `tt first`); moves stay within a block. Tasks they never placed come after
the ones they did, in the automatic order: effective priority (the maximum of the item and its ancestors), then
overdue, then deadline, then age, so capturing something new never takes over the chosen focus. The first one is the
**focus**. The order is shared by every app and process (`.todo-tracker/state.json`); arranging one group's tab keeps
the other groups' places. In the web task panel, subtasks are reordered the same way among their siblings (never
changing parent); the API also reorders top-level tasks.

**Waiting** lists only the top-most deferred item, so a snoozed project shows once, not once per subtask. It is
ordered by wake time, then priority.

**Sequences.** Completing step *n* sets step *n+1*'s gate to `completedAt + stepDelay`, or keeps an existing later
gate. Completing a locked step, or anything under one, is rejected with "Finish X first". Completing the last step
completes the sequence. Completing a parent completes all of its open descendants, and the UIs ask first. Reopening
that parent reopens exactly the children that action closed. Reopening a child reopens its ancestors.

**Snooze / schedule.** Sets `nextActionAt` and (default) adds a `nextAction` reminder at that time. Rescheduling
dismisses the previous undelivered schedule reminder and any reminder that is already due (even if delivered), so "Later" on a ringing reminder really moves the task to *Waiting* and reminders never stack.

* **Quick choices** (`Snooze.Choices`, the same ids in C#, the web app via `GET /api/snooze`, Swift and Kotlin;
  `tests/fixtures/snooze.json` checks them): `15m`, `1h`, `3h`, `evening` (18:00; offered before 17:00), `tomorrow`
  (next 9:00; before 4:00 that's this morning), `2d`, `monday` (next Monday; a week on when it's Monday), `week`, `month`
  (9:00 local on that day; a month from Jan 31 is the end of February). Each shows when it ends: "today 17:00",
  "tomorrow 9:00", "Fri 14:30", "Mon 12 Jan, 9:00".
* **Typed rules** (`Snooze.Parse`, "Pick a time…" with a live preview, `schedule {rule}`, MCP `when`, `tt snooze`):
  `45m`/`2h` from now; `3d`, `2 weeks`, `1 month` (that day at 9:00); weekdays (`fri`, `next fri`); `tonight`,
  `tomorrow 14:00`, `weekend` (Saturday), `next week` (Monday), `next month` (the 1st); a time alone (`9am`, `14:30`;
  today, or tomorrow when it's past); dates (`2026-03-01`, with an optional time); an optional leading `in` (`in 3 days`). A rule that isn't
  understood or is in the past is refused with examples. Phones offer the quick choices and a date picker.
* **After another task** (`after: <id>` in the file; `POST /api/items/{id}/after`, MCP `wait_for_task`,
  `tt snooze <task> after <other>`): the task is *Waiting* ("after “X”") until X is done, then comes back with a
  reminder ("X is done: back to Y"). It replaces a snooze until a time (and snoozing or *Do now* replaces it). Refused:
  itself, a done task, its own subtasks or parents, or a circle. If X is deleted the wait ends. In *Waiting*, tasks
  back at a time come first (soonest first), then those waiting for a task.

**Delivery.** Every 15s the server marks due, undelivered reminders as delivered, at most once and before sending,
so a crash never double-notifies. It then fans them out to desktop toasts and Teams. A failing integration never
blocks the others.

**Quick capture:** `!`/`!high`, `!!`/`!critical`/`!urgent`, `!low`, `@15m`/`@2h`/`@3d`/`@tomorrow` (next 9:00 local; before 4:00 that means this morning; `@3d` is exactly 72h, as before),
and any snooze rule written as one word after `@` (`@fri`, `@weekend`, `@2w`, `@next-week`),
`due:today`/`due:tomorrow`/`due:3d`/`due:YYYY-MM-DD` (17:00 local; `due:today` after 17:00 means 23:59). Malformed tokens (`due:2026-13-01`, `@+5m`) stay in the title on every platform. Unknown tokens stay in the title.

## 6. UX specification

### Windows sidebar
* **Placement** (menu › *Position*), remembered across restarts in `desktop.json`:
  * **Dock right** (default) or **Dock left**. The sidebar registers as a Win32 **AppBar**, so the shell shrinks the
    work area and maximized windows stop at the sidebar. It docks on any monitor (menu lists displays when there are
    several), re-docks when the shell moves bars or the display or DPI changes, and drops *Topmost* while a
    full-screen app runs. A docked sidebar is always on top.
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
  6. Collapsible sections: Do now, Waiting, Workstreams (progress bars; click for the report), and Recent notes.
  7. Focus timer bar.
* Card actions: ✓ done, 🔕 dismiss, ↩ bring back, ⏰ snooze menu (the quick choices with when each ends, *Pick a
  time…*, *After another task*), ✎ inline note,
  ▶ focus, and ⋯ (add subtask, edit in browser, open report).
* Native toasts offer Done and Snooze 1h. `Ctrl+Alt+Space` opens quick capture from any app. There is an optional
  *Start with Windows*.
* The menu has: Position, Always on top, Connect an AI app, copy the MCP config, copy the API token (for the extension), connect Teams,
  and start with Windows.
* **System test:** `tests/system/placement.system-test.ps1` drives the real app with UI Automation through every
  placement, checks the live work area, window bounds, and topmost state, and verifies placement persists across a
  restart. It runs in Windows CI.

### Web dashboard and report (`http://127.0.0.1:5317`)
* The dashboard has the same sections as the sidebar, plus a filter box (`/`) with the search syntax. Tag and label
  chips on cards filter on click. Files that need fixing are shown in a banner, and the footer shows where the
  markdown lives with an *Open in Obsidian* link.
* **Nothing needs a Save button.** The task drawer saves as you type (debounced; edits made during a save are saved
  after it; failures retry; everything is flushed when the panel closes or the page is hidden). Notes save while
  typed and stay one note until you start a new one.
* The drawer edits the title, labels (toggle chips, add new), tags, priority, deadline, details, group, and where the
  task belongs (any other task, or the top level). It shows the subtask tree, rollout steps (one per line),
  attachments (button or drag and drop; images open inline, everything else downloads), the rich HTML version in a
  sandboxed frame, reminders, notes, and the task's version history with view and restore.
* `report.html?id=…` shows the task tree, progress, and a day-grouped timeline with linked sources. It is printable.
* Responsive layout down to phone width. It follows the system dark mode and respects reduced motion.

### Windows sidebar: tasks folder and notes
* Card notes save themselves after a short pause and keep updating the same note; Enter finishes it.
* Cards show labels (colored) and tags. The card menu opens the task in Obsidian or shows its markdown file.
* Menu › *Tasks folder* shows where tasks live, opens the folder or Obsidian, keeps tasks in an Obsidian vault found on
  this machine (`<vault>/Todo Tracker`), or chooses another folder (the app restarts to switch).

### macOS and iOS (SwiftUI)
* Same sections and actions. The iOS app uses local notifications. The macOS app adds a menu bar glance with the focus task, Done, Later, and capture.
* The macOS app runs the bundled server (`TodoTracker.Web --parent-pid`) and works on its tasks folder through the REST API (reads `/api/export`, sends changes), so it syncs like Windows (docs/sync.md, "On a Mac"). iOS keeps a board on the device.

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

* **`tt mcp`**: the MCP server over stdio, launched by Claude Desktop, Claude Code, Copilot CLI and VS Code. It works
  on the vault directly (shared lock), so it works whether or not the app runs. Shipped next to the Windows app, as a
  `dotnet tool`, as self-contained binaries, and as a Claude Desktop extension (`.mcpb`).
* **`tt`**: the same operations from a terminal (`tt now`, `tt add …`, `tt done …`); `--json` returns the API shapes and
  `--as <agent>` attributes changes. Tasks are named by id prefix or title words; ambiguity is an error, never a guess.
* **`/mcp`**: streamable HTTP inside the running app, `Authorization: Bearer <token>`.
* **REST + OpenAPI** (`/openapi/v1.json`, and Swagger 2.0 at `/openapi/swagger2.json` for Copilot Studio): a curated
  subset without deletes, settings, or sign-in. Every `/api` route must be classified as described or hidden (a test
  enforces it).

**Connect an AI app** (sidebar menu, ✨ in the web header) lists a ready snippet per app with the real `tt` path.
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
| `complete_task`, `reopen_task`, `update_task` | Status and fields (incl. tags and labels) |
| `search_tasks` | The shared search syntax (`#tag`, `label:x`, `group:x`, `is:done`, words) |
| `move_task`, `list_labels` | Reorganize the hierarchy; curated labels |
| `attach_text`, `get_rich_html`, `set_rich_html` | Text attachments and the rich HTML version |
| `task_history`, `restore_task_version` | Versions of a task's file, and going back (itself undoable) |
| `vault_info` | Where the markdown lives, files that need fixing, and the format guide (for agents with file tools) |

Every change is attributed as `Agent: <client name>`. There is intentionally **no delete tool**, and completion can
be undone with `reopen_task`. There is no tool that copies arbitrary local files into the vault (that would let a
remote connector read files from the machine).

### Obsidian
The vault is plain Obsidian-flavored markdown: frontmatter properties, Obsidian Tasks tokens, callouts, block ids and
links, wiki-link attachments. Point the app at a folder inside an Obsidian vault (Menu › *Tasks folder*) and edit
tasks in either place. The web UI and sidebar open any task in Obsidian (`obsidian://open?path=…`).

### Plugins and Ask AI
Every optional feature is a built-in plugin (Ask AI, Connect AI apps, focus timer, version history, Obsidian, Teams)
that can be switched off; the choice lives in `plugins.json` and applies after a restart. Ask AI drives GitHub Copilot
CLI or Claude Code over the Agent Client Protocol with Todo Tracker's MCP tools attached, or a model reached with an API
key (OpenAI- or Anthropic-compatible; keys encrypted on this computer) with the same tools. Reading needs no permission;
changing tasks asks, showing what would change (once or for the chat); anything else always asks. Chats are kept and
can be searched, reopened (agents resume their session with `session/load` when they can), renamed and deleted with
undo. A chat stays with the agent or model it began with. Details: [`docs/plugins.md`](plugins.md).

### Teams
Paste a Teams **Workflows** webhook URL (channel › Workflows › "Post to a channel when a webhook request is
received"). Reminders are posted as Adaptive Cards with the title, path, message, priority, due time, and an
*Open report* link. The URL must be https, is stored only in `settings.json`, and is never returned by the API.

### Browser
The extension uses the REST API with the API token. A deep link `/?item=<id>` opens a task drawer. Browsers sign in
with **single-use launch codes**: `POST /api/launch` returns `/auth?code=…&return=/path`, which is valid for
2 minutes and accepts local paths only. The long-lived API token therefore never appears in URLs, browser history,
or history sync.

## 8. Security (local server)

* Kestrel binds to loopback only. Requests with a non-loopback remote address or `Host` header are rejected, which blocks DNS rebinding.
* `/api` and `/mcp` require the bearer token (256-bit random, per user; file mode 600 on Unix) or the session cookie
  (`HttpOnly`, `SameSite=Strict`). Tokens are compared in constant time.
* The API token is never put in a URL. Browsers get the cookie through single-use launch codes, and `return` paths
  must be local and free of control characters.
* Mutations authenticated by cookie also need the `X-TodoTracker-Client` header. This forces a CORS preflight, so
  other localhost origins cannot forge writes. No CORS policy is enabled.
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

The **shared scenarios** in `tests/fixtures/scenarios/*.json` hold a board, a time, and the expected Now, Waiting,
focus, attention, and states. Both the C# and Swift test suites run them.

## 10. Roadmap

* **Next:**
  * Rotate the API token, and compact the activity log.
  * Sync the Apple apps with the desktop server (optional remote mode, then cloud sync with conflict-free merges).
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
| MCP transport | Streamable HTTP inside the running app, plus `tt mcp` over stdio | Local AI apps launch servers themselves and must work without the app; the vault's per-folder lock makes several processes safe, and the app picks their changes up. |

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
| Easy ordering: drag and drop, arrow keys/icons | ✅ Do now: drag, ↑/↓ buttons, Alt+↑/↓ (web and sidebar); subtasks in the web task panel; `put_first` for agents, `tt first` |
| Hierarchy, notes, attachments, tags, labels; intuitive and minimal | ✅ any depth with moves, notes, attachments, tags, colored labels, one filter box |
| Autosave, notes without a Save button | ✅ web drawer and notes, sidebar notes |
| Versioning ("use git or something like that") | ✅ private git history with view/restore (UI, REST, MCP) |
| Available to AI tools: CLI, API, Claude Desktop, Claude Code, Copilot CLI, VS Code, ChatGPT/Copilot Studio; "MCP connectors" | ✅ `tt`, `tt mcp`, `/mcp`, OpenAPI 3.1 + Swagger 2.0, `.mcpb` extension, plugin + skill marketplace, Connect dialog; cloud apps via tunnel (documented) |
| In-app terminal/chat driving Copilot/Claude CLI over ACP (warm, MCP loaded, choose when both installed); features as plugins | ✅ Ask AI plugin (dashboard panel + sidebar box; Copilot and Claude Code; permissions; warm agent); every optional feature is a switchable plugin ([`docs/plugins.md`](plugins.md)) |
| Ask AI as a full chat with sessions and history; ACP plus API keys for OpenAI- and Anthropic-compatible APIs | ✅ chats kept, searched, reopened (ACP `session/load` or a primer), renamed, deleted with undo; API models with presets, encrypted keys, same tools and permissions (tested live with Copilot; Claude untested on the dev machine) |
| Paste media into tasks and notes | ✅ pasted pictures/files are attached and embedded (`![[…]]`, Obsidian-style) in details and notes, web and sidebar |
| Browser extension for Edge/Chrome and Safari, with a setup guide while unsigned | ✅ one source, side panel (Edge/Chrome) and popup (Safari app built in CI); guided setup and pairing codes with per-browser access |
| Notion, MS To Do, Loop, Apple Notes | ⏳ planned as sync plugins |
