# Todo Tracker

An ADHD-friendly task sidebar that always tells you **what to do now**. Deferred work stays out of sight and comes
back on time with a reminder. Rollout steps unlock themselves 24 hours apart, and Copilot and other agents can add
notes and tasks through MCP.

| | |
|---|---|
| **Windows** | Native WPF sidebar docked to the screen edge (maximized windows don't cover it), toasts, `Ctrl+Alt+Space` quick capture |
| **Web** | Dashboard and full report/timeline at `http://127.0.0.1:5317` |
| **macOS / iOS** | Native SwiftUI apps (macOS adds a menu bar glance) |
| **Browser** | Edge/Chrome side panel: glance, capture, and notes that attach the current page |
| **Agents** | MCP endpoint (`/mcp`) with task tools; changes are attributed to the agent |
| **Teams** | Reminder cards through a Teams Workflows webhook |

The full plan, design, rules, and requirement traceability are in [`docs/product-spec.md`](docs/product-spec.md). The original request is in [`docs/request.md`](docs/request.md).

## Quick start (Windows)

```powershell
dotnet run --project src/TodoTracker.Windows
```

The sidebar docks on the right. Type a task in the box at the top and press Enter:

```
Deploy ring 2 !! @2h due:tomorrow      →  critical, back in 2h with a reminder, due tomorrow 17:00
```

To set up a rollout, open the task (⋯ › *Edit details in browser* › *Add rollout steps*), enter one step per line,
and choose 24 hours between steps. Only the current step shows in **Do now**. When you finish it, the next step
waits 24 hours and then comes back with a reminder.

### Connect Copilot (MCP)

Choose sidebar menu **⋯ › Copy MCP config** and paste the result into your MCP client configuration (for example, `~/.copilot/mcp-config.json`):

```json
{ "mcpServers": { "todo-tracker": { "type": "http", "url": "http://127.0.0.1:5317/mcp",
  "headers": { "Authorization": "Bearer <token>" }, "tools": ["*"] } } }
```

### Browser extension

1. Open `edge://extensions` or `chrome://extensions`, turn on developer mode, choose **Load unpacked**, and select the `extension/` folder.
2. Open the side panel and paste the token from sidebar menu **⋯ › Copy API token**.

### Teams reminders

Choose sidebar menu **⋯ › Connect Teams reminders…** and paste a Teams Workflows webhook URL.

## Other platforms

```bash
# Web dashboard / API / MCP without the WPF shell (any OS)
dotnet run --project src/TodoTracker.Web -- --port 5317 --data ./data

# macOS / iOS (on a Mac with Xcode)
cd apple/TodoTrackerKit && swift test
cd apple && brew install xcodegen && xcodegen generate && open TodoTracker.xcodeproj
```

## Development

```bash
dotnet build TodoTracker.slnx -warnaserror
dotnet test --solution TodoTracker.slnx               # xUnit v3 on Microsoft Testing Platform
node --test tests/web/*.test.mjs extension/tests/*.test.mjs
cd tests/e2e && npm install && npx playwright install chromium && npx playwright test
```

`src/TodoTracker.Windows/bin/.../TodoTracker.exe --smoke-test --smoke-log smoke.log` boots the real app against a
temporary board and verifies that the API and the sidebar see the same data. CI runs it on every PR.

| Path | What |
|---|---|
| `src/TodoTracker.Core` | Domain model, agenda rules, quick capture, focus timer, JSON store, reminder dispatcher |
| `src/TodoTracker.Server` | REST API, MCP tools, security, Teams notifier, reminder loop, embedded web UI (`wwwroot`) |
| `src/TodoTracker.Web` | Standalone server host |
| `src/TodoTracker.Desktop` | Platform-neutral view models for the sidebar |
| `src/TodoTracker.Windows` | WPF sidebar (AppBar, toasts, hotkey) |
| `apple/` | Swift core port, SwiftUI UI, and XcodeGen project for iOS and macOS |
| `extension/` | Edge/Chrome side panel extension |
| `tests/fixtures/scenarios` | Shared agenda scenarios run by both the C# and Swift tests |

CI (`.github/workflows/ci.yml`) runs on Linux (core, server, view models, coverage gates), web (JS tests and
Playwright), Windows (full build, tests, publish, smoke test), and macOS (Swift tests, iOS and macOS app builds).
