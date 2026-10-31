# AGENTS.md

Guidance for AI coding agents (Copilot, Claude Code, Codex, Cursor…) and people working on this repository. For using
Todo Tracker *from* an agent (MCP tools, `tt`, the skill), see [docs/ai-connectors.md](docs/ai-connectors.md).

## What this is

Todo Tracker is an ADHD-friendly task app: it always shows **one thing to do now**, keeps deferred work out of sight
until it's due, and lets AI agents read and change the list. Tasks are markdown files in a folder the user owns (an
Obsidian vault works). One .NET server (REST, MCP, the web app) runs inside every desktop app; the phone apps and the
Mac app have native ports of the core rules.

## Map

| Path | What | Built/tested on |
| --- | --- | --- |
| `src/TodoTracker.Core` | Domain model (`TaskBoard`, `WorkItem`), agenda rules, search, quick capture, snooze, repeat, focus timer, the markdown vault store and its format (`Vault/TaskMarkdown.cs`), connectors' sync engine | any OS |
| `src/TodoTracker.Server` | REST API (`ApiEndpoints.cs`), MCP tools (`TodoTools.cs`), security, hooks, plugins (sync, connectors, Ask AI, GitHub), the embedded web app (`wwwroot/`) | any OS |
| `src/TodoTracker.Web` | The server on its own (no desktop shell) | any OS |
| `src/TodoTracker.Cli` | `tt`, and `tt mcp` (MCP over stdio) | any OS |
| `src/TodoTracker.Desktop` | Platform-neutral view models for the sidebar and tray | any OS |
| `src/TodoTracker.Windows` | The WPF sidebar (AppBar docking, toasts, hotkey, app window with WebView2) | Windows |
| `src/TodoTracker.Tray` | The Avalonia tray app for Linux (and macOS) | any OS |
| `apple/` | Swift core port, SwiftUI app for macOS and iOS (XcodeGen) | macOS CI only |
| `android/` | Kotlin core port and Jetpack Compose app | JDK 17 + Android SDK |
| `extension/` | Edge/Chrome/Safari side panel extension | any OS |
| `integrations/` | Claude Desktop extension, Claude Code / Copilot CLI plugin and skill, VS Code sample | — |
| `tests/` | xUnit v3 tests per project, `web/` (node:test), `e2e/` (Playwright), `system/` (Windows UI Automation), `fixtures/` (shared by C#, Swift and Kotlin) | |
| `docs/` | User docs, the product spec, and generated references (`mcp.md`, `cli.md`) | |
| `website/` | The public site at todotracker.awesomehq.app (static, Cloudflare Workers; `website/README.md` says how to deploy) | any OS |

## Build and test

```bash
dotnet build TodoTracker.slnx -c Release -warnaserror      # warnings are errors (also locally: Directory.Build.props)
dotnet test --solution TodoTracker.slnx -c Release --no-build
node --test tests/web/*.test.mjs extension/tests/*.test.mjs
cd tests/e2e && npm install && npx playwright install chromium && npx playwright test   # needs: dotnet build tests/FakeAcpAgent
```

Narrow a run while you work: `dotnet test --project tests/TodoTracker.Core.Tests --filter "FullyQualifiedName~Snooze"`.
The solution includes the Windows app, so on Linux and macOS build and test the other projects one by one, as CI does:
`for p in Core Server Desktop Cli Tray; do dotnet test --project tests/TodoTracker.$p.Tests; done`.

- **Swift:** `cd apple/TodoTrackerKit && swift test` (macOS only; CI runs it and builds both apps).
- **Android:** `cd android && gradle testDebugUnitTest assembleDebug` (JDK 17, Gradle 8.11, SDK platform 35).
- **Windows end to end:** `TodoTracker.exe --smoke-test --smoke-log smoke.log` boots the real app on a temporary board
  and checks that the API and the sidebar see the same data. The system tests drive the real sidebar with UI
  Automation (`-Exe <TodoTracker.exe>`; they take over the screen and keyboard for a while, so not while you work):
  `tests/system/placement.system-test.ps1` (dock, float, always on top, restarts), `fullscreen.system-test.ps1`
  (full-screen apps and Remote Desktop), and `multimonitor.system-test.ps1` (needs two monitors; skipped in CI, run it
  after changing placement code).
- **Android by hand:** an emulator (`system-images;android-35;google_apis;x86_64`) runs the app;
  `adb shell date @<seconds>` (as root) moves its clock to reach a break quickly.

CI (`.github/workflows/ci.yml`) runs seven jobs on every PR: Linux (core, server, CLI, view models, coverage), Linux
tray, Android, web + e2e, AI connectors (packages `tt` and the Claude Desktop extension), Windows (build, tests,
publish, smoke and system tests), and macOS/iOS. Keep all of them green.

## Run it without touching anyone's tasks

Never point a test or a demo at the user's own data folder or tasks folder.

```bash
DATA=$(mktemp -d)
TODOTRACKER_TOKEN=dev-token-0123456789abcdefghijklmnop TODOTRACKER_CLOUD=off TODOTRACKER_VAULT="$DATA/vault" \
  dotnet run --project src/TodoTracker.Web -- --port 5399 --data "$DATA"
# open the sign-in link it prints; the API takes:  Authorization: Bearer dev-token-…
dotnet run --project src/TodoTracker.Cli -- now --data "$DATA" --vault "$DATA/vault" --as my-agent
```

- `--data <dir>` (or `TODOTRACKER_DATA`) is the app's data folder; with it, the tasks folder is `<dir>/vault` unless
  `--vault`/`TODOTRACKER_VAULT` says otherwise. **`TODOTRACKER_VAULT` wins over `--data`**: set it (as above) or make
  sure it isn't set, or a test run writes into the folder it names. `TODOTRACKER_CLOUD=off` keeps it from syncing
  through OneDrive/iCloud.
- Only one Windows sidebar runs at a time: a second start hands over to the running one. To try a build of the sidebar,
  quit the running app first, and start it again when you're done.
- `TODOTRACKER_AGENT_PATH` (a folder) and `TODOTRACKER_TEST_AGENT` (the e2e fake agent) control what Ask AI finds.

## Rules that keep the product what it is

- **Nothing is ever lost.** Agents get no delete tool; connectors never delete (they archive or close); sync merges;
  every change is a version. Don't add a path that destroys user data without an explicit, human-only action.
- **Every change says who made it.** Mutations go through `TaskBoard` with an `Actor` (user, agent, connector, vault,
  system); the timeline and hooks show it. Use the `X-TodoTracker-Actor` header / `--as` / the MCP client name.
- **The same rules on every platform.** Agenda, snooze choices, repeat rules, energy, deep links and the export format
  are implemented in C#, Swift and Kotlin with shared cases in `tests/fixtures` (`scenarios/`, `snooze.json`,
  `breaks.json`, `repeat.json`, `energy.json`, `export.json`, `board-moves.json`). Change one, change all three and the fixture. The tasks
  folder format and sync have generated fixtures too (`vault/`, `sync/`) for the phones that sync on their own
  ([docs/design/phone-sync.md](docs/design/phone-sync.md)).
- **Every feature on every platform, docs in the same PR.** A user-facing change lands in the web app, the Windows
  sidebar, Mac/iPhone (SwiftUI) and Android where it applies (the Linux tray is quick add only), and the docs it touches
  (`docs/guide.md`, `docs/product-spec.md`, `docs/plugins.md`, the README, the skill) change with it. An optional
  feature is a plugin (`docs/plugins.md`): switched off, it leaves every window at once.
- **Cross-platform by default.** Server, Core, CLI and tray code run on Windows, macOS and Linux: guard OS-specific
  code with `OperatingSystem.IsWindows()`/`IsMacOS()`, use `Path.Combine`, and find external tools (git, gh) without
  assuming the PATH a terminal has.
- **Local only, owner first.** The server binds to loopback, checks the Host header and a bearer token. Things only the
  person at the computer may do (approve hooks, move where tasks live) check `Security.IsAppSession`.
- **Extras are plugins** (`src/TodoTracker.Server/Plugins`): each can be switched off; new ones are registered in
  `Plugins.BuiltIn` (and listed in `tests/.../PluginApiTests.cs`).

## Changing things: where else to look

| When you change… | Also update |
| --- | --- |
| an MCP tool (`TodoTools.cs`) | `[Description]`s on the tool and every parameter; the skill (`integrations/agent-plugin/skills/todo-tracker/SKILL.md`); `integrations/claude-desktop/manifest.json`; then regenerate `docs/mcp.md` |
| a `tt` command (`CliCommands.cs`) | its usage/details text; the skill; then regenerate `docs/cli.md` |
| a REST endpoint (`ApiEndpoints.cs`) | the published list in `OpenApiSetup.cs` if agents should see it |
| the task model (`WorkItem`, `TaskBoard`) | `BoardSerializer`, `Vault/TaskMarkdown.cs` (+ `docs/vault-format.md`), the Swift and Kotlin codecs, `tests/fixtures/export.json` |
| the vault format or sync (`Core/Vault`, `Core/Sync`) | regenerate `tests/fixtures/vault/` and `tests/fixtures/sync/` and review the diff; the Swift and Kotlin vault and sync code ([docs/design/phone-sync.md](docs/design/phone-sync.md)) |
| the web app's `format.js` or `icons.js` | copy them to `extension/lib/` (a test checks they're identical) |
| anything a user sees | the docs in `docs/` and the README; screenshots if it shows (`node tests/e2e/screenshots.mjs`) |

Generated files (commit them after regenerating):

```bash
TT_WRITE_DOCS=1 dotnet test tests/TodoTracker.Cli.Tests --filter ReferenceDocsTests     # docs/mcp.md, docs/cli.md
TT_WRITE_FIXTURES=1 dotnet test tests/TodoTracker.Core.Tests --filter ExportFixtureTests  # tests/fixtures/export.json
TT_WRITE_FIXTURES=1 dotnet test --project tests/TodoTracker.Core.Tests --filter "FullyQualifiedName~Fixtures"  # tests/fixtures/vault, sync (then build again: tests read the copies in bin/)
cd tests/e2e && node screenshots.mjs        # docs/images/*.png, from a made-up demo board (quit the app first on Windows)
```

## Style

- **C#:** .NET 10, nullable on, analyzers at `latest-recommended`, warnings are errors. File-scoped namespaces,
  primary constructors, records for DTOs. XML doc comments on public members say *what it is for the user*, briefly.
- **Tests first** for behavior changes: a failing test, then the fix. Test names are sentences
  (`A_task_moved_to_another_group_leaves_this_groups_repository`).
- **Words the user reads** (UI, errors, docs): plain, short, friendly, second person, no jargon ("Couldn't move your
  tasks yet: a file is open in another app"). Say what happened and what to do.
- **Web app:** plain ES modules, no framework, no build step; DOM built with `h(...)` and `textContent` only (strict
  CSP, no inline script).
- Line endings follow `.gitattributes` (`.sh`, `.swift`, `.yml`: LF).

## Pull requests

One topic per PR, a short description of what changes for the user, tests for the change, docs updated, all seven CI
jobs green. Commit messages: a short summary line, then what and why.
