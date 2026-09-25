# Todo Tracker

Todo Tracker is an ADHD-friendly task sidebar concept for Windows. The first
implementation uses a lightweight .NET/WPF desktop shell plus a shared core
library for task hierarchy, reminders, waiting states, notes, priority ordering,
agent updates, and Pomodoro timing.

## Current scope

- Windows-first desktop sidebar that can reserve the right edge of the desktop
  so maximized windows do not cover it.
- Nested tasks and subtasks with priorities, deadlines, reminders, notes, and
  "wait until next action" scheduling.
- Agenda logic that keeps deferred rollout steps out of the "Do now" list until
  their reminder/next-action time arrives.
- Collapsible sidebar panels for "Do now", "Waiting", "Recent notes", and
  Pomodoro focus state.
- Shared domain types that can be reused by future Teams, browser extension,
  MCP/Copilot, macOS, iOS, and web clients.

## Build and test

```bash
dotnet restore TodoTracker.slnx
dotnet build TodoTracker.slnx --no-restore
dotnet run --project tests/TodoTracker.Core.Tests/TodoTracker.Core.Tests.csproj
```

The Windows shell targets `net8.0-windows` and sets
`EnableWindowsTargeting=true` so it can compile in non-Windows CI. Running the
desktop UI requires Windows.

## Architecture

- `src/TodoTracker.Core` contains platform-neutral task, reminder, notes,
  agenda, Pomodoro, and agent-update models.
- `src/TodoTracker.Windows` contains the WPF sidebar shell and native AppBar
  integration for reserving desktop work area.
- `tests/TodoTracker.Core.Tests` is a dependency-free console test runner for
  the core scheduling and ordering rules.

See `docs/product-spec.md` for the plan, design, and phased specification.