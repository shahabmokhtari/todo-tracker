# Todo Tracker product plan, design, and specification

## Product goal

Create an always-visible, low-distraction task command center for ADHD workflows.
The app should keep only actionable work prominent, defer tasks that are waiting
on time or external signals, and make it easy for humans and AI agents to add
notes or update next actions.

## Design principles

1. **Now first:** the top panel shows tasks that need attention now. Waiting
   work is visible but secondary.
2. **Low clutter:** use collapsible panels, short task cards, color-coded
   priority, and progressive disclosure for notes and reports.
3. **Time-aware workflows:** every task and subtask can carry reminders,
   deadlines, and next-action times. Deferred rollout steps drop down until they
   become actionable.
4. **Fast native shell:** start with a lightweight Windows desktop sidebar using
   native AppBar behavior to reserve monitor space.
5. **Reusable core:** keep scheduling and task state platform-neutral so future
   web, browser extension, iOS, and macOS clients share behavior.
6. **Agent-ready:** expose simple task update models so MCP/Copilot integrations
   can add notes, schedule next action times, or adjust priorities.

## MVP requirements

### Task model

- Tasks can contain nested subtasks.
- Tasks have a title, priority, status, optional detail, optional deadline,
  optional next-action time, reminders, and notes.
- Notes are timestamped and shown in a recent-notes panel.
- Reminders and next-action times determine when waiting tasks re-enter the
  actionable agenda.

### Sidebar UI

- The Windows shell docks to the right side of the desktop.
- The shell reserves desktop work area so maximized windows do not cover it.
- The sidebar contains collapsible sections for:
  - actionable work,
  - waiting work,
  - recent notes,
  - Pomodoro focus state.
- Priority colors:
  - critical: pink/red,
  - high: orange,
  - normal: blue,
  - low: slate.

### Rollout workflow

For a rollout with sequential 24-hour steps:

1. The current due step appears in "Do now".
2. After completing/logging the step, the next step is scheduled 24 hours out.
3. The scheduled step appears in "Waiting" until its next-action time.
4. When due, it moves back to "Do now".

### Integrations

- MCP/Copilot agents should be able to contribute structured updates:
  task id, note, optional next-action time, optional priority.
- Microsoft Teams notifications should be added after local notification and
  persistence are in place.
- Browser integration should start as "open full report/timeline in browser"
  before adding Edge/Chrome sidebar extensions.

### Platform support

- Windows remains the first native desktop shell.
- Web support starts with a browser-hosted dashboard and JSON endpoints backed by
  the shared task core.
- macOS and iOS support starts with a shared Apple adapter that converts tasks
  into native-client-friendly, accessibility-ready snapshots. Native UI projects
  can bind to this adapter once Apple workloads/runners are available.

## Phased implementation plan

1. **Foundation**
   - Platform-neutral task/reminder/notes/Pomodoro domain.
   - Agenda and waiting-list ordering rules.
   - Windows sidebar shell with native desktop reservation.
   - Build pipeline for restore, release build, and tests.
2. **Persistence and editing**
   - Local storage.
   - Add/edit task, subtask, reminder, deadline, priority, and notes from the
     sidebar.
   - Full report/timeline browser view.
3. **Notifications**
   - Windows notifications for due reminders.
   - Snooze and "schedule next step" quick actions.
4. **Integrations**
   - MCP/Copilot task update endpoint.
   - Teams notification connector.
   - Browser sidebar extension.
5. **Cross-platform**
   - Web client against the shared task API.
   - macOS and iOS clients reusing the Apple task snapshot adapter and core
     workflow semantics.

## Validation criteria

- A task scheduled 24 hours in the future is not actionable before then.
- A due reminder makes its task actionable.
- Higher-priority due work sorts above lower-priority due work.
- Recent notes show newest task notes first.
- Pomodoro remaining time never becomes negative.
- Windows shell compiles and contains AppBar registration/removal logic.
