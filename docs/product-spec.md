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
5. **Local-first and fast.** Native shells, a local JSON store, and no account or cloud needed.
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

* **Single writer.** One process owns `board.json`. The file store holds an exclusive lock-file handle for the
  process lifetime; the OS releases it if the process crashes. A second instance reports "already running".
* **Atomic saves.** Each save writes a temp file and then replaces the board, keeping a `.bak` copy. A corrupt board
  is set aside as `board.json.corrupt-<timestamp>` and the backup is loaded.
  Mutations that throw are rolled back in memory.
* **Data folder.** `%LOCALAPPDATA%\TodoTracker` (override with `TODOTRACKER_DATA` or `--data`) holds `board.json`,
  `settings.json` (Teams webhook), and `api-token`.

## 4. Domain model (schema v1)

| Entity | Fields |
|---|---|
| **Board** | `schemaVersion`, `groups[]`, `items[]` (roots), `activity[]`, `pomodoro` |
| **Group (tab)** | `id`, `name` (unique, case-insensitive), `color` (`#rrggbb`) |
| **Task** | `id`, `title` (≤300), `details`, `priority` (low/normal/high/critical), `createdAt`, `completedAt`, `deadline`, `nextActionAt` (defer gate), `sequential`, `stepDelayMinutes`, `groupId` (roots only), `reminders[]`, `notes[]`, `children[]` |
| **Reminder** | `id`, `dueAt`, `message`, `kind` (`manual` or `nextAction`), `notifiedAt`, `dismissedAt` |
| **Note** | `id`, `at`, `text`, `author {kind: user/agent/browser/teams/system, name}`, `sourceUrl` (http/https only), `sourceTitle` |
| **Activity** | `at`, `itemId`, `kind`, `summary`, `actor`. This is the audit trail and the report timeline. |

Timestamps are UTC ISO-8601 with milliseconds. Readers ignore unknown fields and reject newer schema versions.

## 5. Scheduling rules

**State** (derived from time, never stored), evaluated in this order:
1. `done`: the item has `completedAt`.
2. `locked`: the item or an ancestor sits behind an unfinished earlier step of a *sequential* parent.
3. `waiting`: the item's or an ancestor's `nextActionAt` is in the future.
4. `container`: the item has open subtasks. Its leaves are the actionable things.
5. `actionable`: anything else.

**Needs attention.** The item has a due, undismissed reminder and is not locked or done. This surfaces waiting and
container items in *Do now*.

**Do now** holds actionable items plus attention items. They are ordered by attention, then effective priority
(the maximum of the item and its ancestors), then overdue, then deadline, then age. The first one is the **focus**.

**Waiting** lists only the top-most deferred item, so a snoozed project shows once, not once per subtask. It is
ordered by wake time, then priority.

**Sequences.** Completing step *n* sets step *n+1*'s gate to `completedAt + stepDelay`, or keeps an existing later
gate. Completing a locked step is rejected with "Finish X first". Completing the last step completes the sequence.
Completing a parent completes all of its descendants. Reopening a child reopens its ancestors.

**Snooze / schedule.** Sets `nextActionAt` and (default) adds a `nextAction` reminder at that time. Rescheduling
dismisses the previous undelivered schedule reminder, so reminders never stack.

**Delivery.** Every 15s the server marks due, undelivered reminders as delivered, at most once and before sending,
so a crash never double-notifies. It then fans them out to desktop toasts and Teams. A failing integration never
blocks the others.

**Quick capture:** `!`/`!high`, `!!`/`!critical`/`!urgent`, `!low`, `@15m`/`@2h`/`@3d`/`@tomorrow` (9:00 local),
`due:today`/`due:tomorrow`/`due:3d`/`due:YYYY-MM-DD` (17:00 local). Unknown tokens stay in the title.

## 6. UX specification

### Windows sidebar
* Docked as a Win32 **AppBar** on the right edge. The shell shrinks the work area, so maximized windows stop at the
  sidebar. The window re-docks when the shell moves bars. It drops *Topmost* while a full-screen app runs, uses
  per-monitor DPI v2, and can be undocked from the menu.
* Layout, top to bottom:
  1. Header: collapse to a 56px strip, open dashboard, and a menu.
  2. Group tabs with counts and an attention dot.
  3. Quick capture.
  4. Status line.
  5. **Do this now** card.
  6. Collapsible sections: Do now, Waiting, Workstreams (progress bars; click for the report), and Recent notes.
  7. Focus timer bar.
* Card actions: ✓ done, 🔕 dismiss, ↩ bring back, ⏰ snooze menu (15m, 1h, 3h, tomorrow 9:00, +24h), ✎ inline note,
  ▶ focus, and ⋯ (add subtask, edit in browser, open report).
* Native toasts offer Done and Snooze 1h. `Ctrl+Alt+Space` opens quick capture from any app. There is an optional
  *Start with Windows*.
* The menu has: copy the MCP config, copy the API token (for the extension), connect Teams, reserve screen edge, and start with Windows.

### Web dashboard and report (`http://127.0.0.1:5317`)
* The dashboard has the same sections as the sidebar, plus a task drawer. The drawer edits the title, priority,
  deadline, details, steps-in-order, hours between steps, and group, and manages subtasks, rollout steps
  (one per line), reminders, and notes.
* `report.html?id=…` shows the task tree, progress, and a day-grouped timeline with linked sources. It is printable.
* Responsive layout down to phone width. It follows the system dark mode and respects reduced motion.

### macOS and iOS (SwiftUI)
* Same sections and actions. The iOS app uses local notifications. The macOS app adds a menu bar glance with the focus task, Done, Later, and capture.

### Browser extension (Edge/Chrome side panel)
* Group tabs, the focus card, Do now, Waiting, and quick capture. A note can attach the current page's URL and title.

### Color and accessibility
* Priority colors: critical `#e11d48`, high `#f97316`, normal `#3b82f6`, low `#94a3b8`. Amber marks attention.
* Every icon button has an accessible name. Text never relies on color alone: priority is also shown in the tooltip or label.

## 7. Integrations

### MCP (Copilot and agents)
The streamable HTTP endpoint is `http://127.0.0.1:5317/mcp` with `Authorization: Bearer <token>`. The sidebar
menu option *Copy MCP config* copies a ready `mcpServers` block. Tools:

| Tool | Purpose |
|---|---|
| `get_dashboard` | Now / waiting / overview / notes / timer, optionally for one group |
| `list_tasks`, `get_task`, `get_report` | Read tree, details, timeline |
| `create_task` | Task or subtask (group, priority, deadline, optional defer) |
| `add_steps` | Gated rollout steps with `stepDelayHours` |
| `add_note` | Progress log with optional source URL |
| `schedule_next_action`, `add_reminder` | Defer and remind |
| `complete_task`, `reopen_task`, `update_task` | Status and fields |

Every change is attributed as `Agent: <client name>`. There is intentionally **no delete tool**, and completion can
be undone with `reopen_task`.

### Teams
Paste a Teams **Workflows** webhook URL (channel › Workflows › "Post to a channel when a webhook request is
received"). Reminders are posted as Adaptive Cards with the title, path, message, priority, due time, and an
*Open report* link. The URL must be https, is stored only in `settings.json`, and is never returned by the API.

### Browser
The extension uses the REST API with the API token. A deep link `/?item=<id>` opens a task drawer. The launch link
`/auth?token=…&return=/path` sets the session cookie and accepts local paths only.

## 8. Security (local server)

* Kestrel binds to loopback only. Requests with a non-loopback remote address or `Host` header are rejected, which blocks DNS rebinding.
* `/api` and `/mcp` require the bearer token (256-bit random, per user; file mode 600 on Unix) or the session cookie
  (`HttpOnly`, `SameSite=Strict`). Tokens are compared in constant time.
* Mutations authenticated by cookie also need the `X-TodoTracker-Client` header. This forces a CORS preflight, so
  other localhost origins cannot forge writes. No CORS policy is enabled.
* Strict CSP (`default-src 'self'`, no inline script, `frame-ancestors 'none'`), `nosniff`, `no-referrer`, and
  `Cache-Control: no-store` on the API. The DOM is built with `textContent` only. Links are http(s) only.
* Input limits: titles ≤300 characters, notes ≤10k. Schema-version guard. Unknown groups and ids return 404, bad input returns 400, and sequence violations return 409.

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
| Persistence | Local JSON, single writer | Human-readable, easy to back up and export, and shared with Swift. SQLite is not needed at this size. |
| Apple stack | Native SwiftUI + Swift core port | Native feel and reliable CI. Parity with C# is enforced by the shared fixtures. |
| Web front-end | Vanilla JS served by the server | Instant load, no toolchain, strict CSP |
| Teams | Workflows webhook | Works without an app registration |
| MCP transport | Streamable HTTP inside the running app | One writer, the same live board as the UI, and no second process |

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
