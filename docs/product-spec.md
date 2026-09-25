# Todo Tracker product plan, design, and specifications

See `docs/request.md` for the original user request that this specification traces back to.

## 1. Product vision

Todo Tracker is an ADHD-friendly task command center for people who are using AI agents, chat, browsers, and multiple workstreams at the same time. The app should reduce cognitive load by keeping the current next action visible, hiding work that is not actionable yet, and making every deferred task return at the right time.

The product starts with a native Windows desktop sidebar that reserves monitor space so maximized windows do not cover it. The shared task core is intentionally platform-neutral so the same task behavior can power web, macOS, iOS, browser extensions, Teams, and MCP/Copilot integrations.

## 2. Target users and needs

### Primary user

A power user with ADHD who often works on multiple long-running technical tasks, rollout sequences, browser research, Teams conversations, and AI-agent-assisted coding sessions.

### User needs

- See what requires attention now without scanning a noisy task database.
- Keep long-running tasks and subtasks structured without losing context.
- Defer blocked/time-gated work until it is actionable again.
- Capture notes quickly without breaking focus.
- Review a full timeline/report only when needed.
- Let AI agents add notes, update priorities, and schedule follow-up work safely.
- Use color and simple hierarchy to understand priority at a glance.
- Use Pomodoro/focus timing without opening another app.

## 3. Product principles

1. **Now first:** actionable work is always above waiting work.
2. **Low clutter:** collapsed panels and compact cards are preferred over dense tables.
3. **Time-aware:** reminders, deadlines, and next-action times drive task visibility.
4. **ADHD-aligned:** the UI favors small next steps, visible state, and low interruption cost.
5. **Native where it matters:** desktop shells should use native OS capabilities for speed and reliable window behavior.
6. **Shared behavior:** scheduling and ordering rules belong in the shared core, not duplicated per platform.
7. **Agent-friendly but user-controlled:** AI/MCP/Copilot updates should be structured, auditable, and reversible.

## 4. Scope by platform

### 4.1 Windows desktop

Windows is the first-class native shell.

Current foundation:

- WPF desktop sidebar.
- Native AppBar registration to reserve the right edge of the monitor.
- Always-visible compact UI with collapsible sections.
- Shared-core sample board bound into the sidebar.

Required next capabilities:

- Create/edit/delete tasks and subtasks from the sidebar.
- Persist local data.
- Trigger Windows notifications for due reminders.
- Snooze/reschedule next action directly from reminders.
- Open full timeline/report in the browser.

### 4.2 Web

The web platform provides browser access and future full report/timeline views.

Current foundation:

- ASP.NET Core web project.
- Shared-core-backed agenda and waiting JSON endpoints.
- Simple browser dashboard showing actionable and waiting work.

Required next capabilities:

- Replace sample data with shared persistence/API access.
- Add full task timeline/report pages.
- Add note-entry flow optimized for quick capture.
- Support responsive layouts for desktop browser and mobile browser.

### 4.3 macOS and iOS

Apple support should reuse shared task behavior while native UI shells evolve.

Current foundation:

- Buildable Apple adapter project.
- Shared-core agenda/waiting projection into accessibility-ready snapshots.
- Test coverage for Apple snapshot projection.
- This is not yet a native MAUI/iOS/Mac Catalyst shell; it is the validated
  adapter layer that a native Apple shell should bind to when Apple build
  workloads and runners are introduced.

Required next capabilities:

- Add native UI once Apple workloads/runners are available.
- Use platform-native notifications.
- Support widgets/Live Activities where useful for reminders and focus state.
- Keep accessibility labels rich enough for VoiceOver and glanceable UI.

### 4.4 Browser extensions

Browser extensions should be added after persistence and web report routes exist.

Planned capabilities:

- Edge/Chrome sidebar extension.
- Quick note capture from the current tab.
- Attach URL/title metadata to a task note.
- Open full task report in the web app.

### 4.5 Teams and MCP/Copilot integrations

Planned capabilities:

- Teams reminder notifications and action cards.
- MCP tool surface for agents to add notes, set next-action time, update priority, and report progress.
- Audit trail showing whether updates came from the user, Teams, browser, or an AI agent.

## 5. Domain model specification

### 5.1 Task/WorkItem

Each task or subtask has:

- Stable id.
- Title.
- Optional detail/description.
- Priority: low, normal, high, critical.
- Status: active, waiting, completed.
- Created timestamp.
- Optional deadline.
- Optional next-action timestamp.
- Zero or more reminders.
- Zero or more timestamped notes.
- Zero or more nested subtasks.

### 5.2 Reminder

A reminder has:

- Due timestamp.
- Message.
- Dismissed state.

Rules:

- A due reminder makes an incomplete item actionable.
- A future reminder can place an incomplete item into waiting.
- Adding a reminder must not resurrect completed work.
- Dismissed reminders should no longer make an item actionable.

### 5.3 Notes

A note has:

- Timestamp.
- Text.

Rules:

- Notes cannot be empty.
- Recent notes are sorted by newest note timestamp.
- Sidebar shows short recent notes; full history belongs in report/timeline view.

### 5.4 Pomodoro

A Pomodoro session has:

- Focus duration.
- Break duration.
- Started timestamp.
- Mode: focus or break.

Rules:

- Durations must be positive.
- Remaining time never returns a negative value.
- Future UI should allow start, pause, reset, skip to break, and associate focus sessions with tasks.

## 6. Scheduling and ordering rules

### 6.1 Agenda

The agenda contains incomplete tasks/subtasks that are actionable now.

A task is actionable when:

- It is active and has no future next-action gate, or
- Its next-action time is now/past, or
- It has a due, non-dismissed reminder.

Ordering:

1. Higher priority first.
2. Earlier effective due time first.
3. Earlier creation time first.

### 6.2 Waiting list

The waiting list contains incomplete tasks/subtasks that are intentionally deferred.

A task is waiting when:

- Status is waiting, and
- It is not actionable yet.

Ordering:

1. Earlier effective due time first.
2. Higher priority second.

### 6.3 Effective due time

Effective due time is the earliest non-null value among:

- Next-action timestamp.
- Earliest non-dismissed reminder timestamp.
- Deadline.

## 7. Rollout workflow example

Scenario: rollout feature X requires feature A and feature B, and each rollout step can only happen 24 hours after the previous step.

Expected behavior:

1. Parent task: `Roll out feature X` stays visible as the overall workstream.
2. Current due step appears in `Do now`.
3. User completes/logs the current step.
4. User or agent schedules the next subtask action for 24 hours later.
5. The next step moves to `Waiting`.
6. When the next-action timestamp arrives, it appears in `Do now`.
7. Notes preserve what happened at each step.
8. The full report/timeline shows all steps, timestamps, notes, reminders, and agent updates.

## 8. UI/UX specification

### 8.1 Sidebar layout

Top-to-bottom layout:

1. Header with current mode/context.
2. `Do now` expanded by default.
3. `Waiting` collapsible.
4. `Recent notes` collapsible.
5. Pomodoro card.
6. Optional lower-priority panels collapsed by default.

### 8.2 Task card

Each card should show:

- Title.
- Priority color.
- Short detail or next action.
- Next due/action time.
- Reminder/deadline indicator when present.

### 8.3 Color coding

- Critical: pink/red.
- High: orange.
- Normal: blue.
- Low: slate/gray.

### 8.4 ADHD-specific UX constraints

- Avoid dense grids by default.
- Avoid showing every future task in the primary list.
- Keep action labels short.
- Prefer one clear next action per card.
- Use progressive disclosure for notes and timeline details.
- Avoid unnecessary animations or noisy notification patterns.

## 9. Integration specifications

### 9.1 MCP/Copilot

Initial structured update shape:

- Task id.
- Note text.
- Optional next-action timestamp.
- Optional priority.

Future tool operations:

- Create task.
- Add subtask.
- Add note.
- Schedule next action.
- Set reminder.
- Mark completed.
- Produce task report.

Safety requirements:

- Agent updates should be attributable.
- Destructive changes should be reversible or require confirmation.
- Agent-created reminders should be visible in the same UI as user-created reminders.

### 9.2 Teams

Planned notification payload:

- Task title.
- Reminder message.
- Priority.
- Due time.
- Quick actions: open, snooze, mark done, add note.

### 9.3 Browser

Planned browser flows:

- Add note from current page.
- Attach source URL/title.
- Open full report/timeline.
- Show active task/sidebar extension summary.

## 10. Persistence specification

Persistence is not implemented yet, but should support:

- Local-first storage.
- Task hierarchy.
- Reminder state.
- Notes timeline.
- Agent/source attribution.
- Future sync across desktop/web/mobile.

Candidate path:

1. Local JSON or SQLite for early desktop prototype.
2. Repository/API layer in shared application service.
3. Optional cloud sync later.

## 11. Build pipeline specification

The repository uses GitHub Actions for continuous validation.

Pipeline triggers:

- Pull requests.
- Pushes to `main`.
- Pushes to `master`.
- Pushes to `copilot/**` task branches.

Pipeline steps:

1. Check out repository.
2. Install .NET 8 SDK.
3. Restore `TodoTracker.sln`.
4. Build `TodoTracker.sln` in Release mode with warnings treated as errors.
5. Run the console test runner in Release mode.
6. Smoke-test the web app via `/health` and `/api/agenda`.
7. Build the desktop solution on a Windows runner to catch Windows/WPF issues
   that Linux cross-targeting can miss.

Local equivalent:

```bash
dotnet restore TodoTracker.sln
dotnet build TodoTracker.sln --configuration Release --no-restore --warnaserror
dotnet run --configuration Release --no-build --project tests/TodoTracker.Core.Tests/TodoTracker.Core.Tests.csproj
```

## 12. Validation criteria

Current automated coverage verifies:

- A task scheduled 24 hours in the future is not actionable before then.
- Future reminders remain waiting until due.
- Due reminders make tasks actionable.
- Higher-priority due work sorts above lower-priority due work.
- Recent notes show newest task notes first.
- Pomodoro remaining time is clamped at zero.
- Completed work is not actionable and is not resurrected by reminders.
- Reminder dismissal updates stored task reminders.
- Apple platform snapshots preserve task data and accessibility labels.
- Recent notes handles non-positive counts.
- Domain input validation rejects invalid values.

Manual validation should verify:

- Web `/api/agenda` returns shared-core agenda JSON.
- Windows desktop shell reserves monitor space on Windows.
- GitHub Actions build runs successfully on PRs.

## 13. Phased implementation plan

### Phase 1: Foundation

Status: in progress/current foundation.

- Shared domain model.
- Agenda/waiting ordering.
- Windows sidebar shell.
- Web dashboard/API foundation.
- Apple adapter foundation.
- Build pipeline.
- Core/platform projection tests.

### Phase 2: Persistence and editing

- Add local storage.
- Add create/edit/delete task flows.
- Add reminder/deadline editors.
- Add quick note capture.
- Add full report/timeline route.

### Phase 3: Notifications and focus tools

- Windows notifications.
- Snooze/complete/add-note actions.
- Pomodoro controls.
- Task-associated focus sessions.

### Phase 4: Agent and collaboration integrations

- MCP/Copilot tool surface.
- Teams notifications/action cards.
- Agent attribution/audit trail.

### Phase 5: Native and browser expansion

- Native macOS/iOS UI using Apple adapter.
- Web persistence-backed dashboard.
- Edge/Chrome sidebar extension.
- Cross-device sync strategy.

## 14. Open decisions

- Local persistence technology: JSON vs SQLite.
- Sync model and identity strategy.
- Exact MCP server boundary and authentication.
- Teams notification provider setup.
- Whether macOS/iOS should use MAUI, native Swift UI over shared service APIs, or another native shell approach.
- Browser extension API boundaries and permissions.
