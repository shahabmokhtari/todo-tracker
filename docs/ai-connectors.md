# Connect AI apps

Todo Tracker is meant to be used *with* AI: tell Claude or Copilot what you need to do and it lands in your list,
ask what to work on next, let an agent log progress or break a project into steps. Every change an AI makes is
attributed to it (you'll see "Agent: claude" in the timeline) and versioned, so it can be undone.

The fastest way is **⋯ › Connect an AI app** in the sidebar (or the ✨ button in the web dashboard): pick your app,
copy one thing, done. This page explains what those snippets do and covers the apps that need more setup.

There are four ways in, from most to least local:

| Way | For | Needs the app running? |
| --- | --- | --- |
| **`tt mcp`** – MCP over stdio | Claude Desktop, Claude Code, GitHub Copilot CLI, VS Code, Cursor, any local MCP client | No |
| **`tt` CLI** | Terminals, scripts, and agents that run shell commands | No |
| **`/mcp`** – MCP over HTTP | MCP clients that prefer a URL | Yes |
| **REST + OpenAPI** | ChatGPT (GPT Actions), Copilot Studio, Power Automate, scripts | Yes |

`tt` works directly on your tasks folder, the same one the app uses, and is safe to run alongside it (changes take
a per-folder lock and re-read the files first). Several AI apps can be connected at once.

## Get `tt`

* **Windows app:** `tt.exe` is installed next to `TodoTracker.exe`. "Connect an AI app" fills in its full path.
* **.NET tool:** `dotnet tool install -g TodoTracker.Cli --add-source <folder with the .nupkg>` (the package is a CI
  artifact; `dotnet pack src/TodoTracker.Cli -o <folder>` builds it).
* **Standalone:** CI publishes self-contained `tt` for Windows, macOS and Linux (`tt-<platform>.zip`, no .NET needed).

`tt` uses the tasks folder the app uses (`Documents/Todo Tracker`, or the folder chosen in the app). Override it with
`--vault <folder>` or the `TODOTRACKER_VAULT` environment variable.

## Claude Desktop

**Extension (easiest):** download `todo-tracker-<platform>.mcpb` from the CI artifacts and open it; Claude shows an
install dialog. Leave *Tasks folder* empty to use the app's folder.

**Or by hand:** Settings → Developer → Edit config, then add (with the path from "Connect an AI app"):

```json
{ "mcpServers": { "todo-tracker": { "command": "C:\\Program Files\\Todo Tracker\\tt.exe", "args": ["mcp"] } } }
```

Restart Claude Desktop completely after editing.

## Claude Code

```bash
claude mcp add todo-tracker --scope user -- tt mcp
```

For the tools *and* a skill that teaches Claude how to help with your tasks (capture, break down, log, defer, never
delete), install the plugin from this repository:

```
/plugin marketplace add shahabmokhtari/todo-tracker
/plugin install todo-tracker@todo-tracker
```

The plugin launches `tt` from your PATH.

## GitHub Copilot CLI

Add to `~/.copilot/mcp-config.json` (or run `/mcp add` inside Copilot):

```json
{ "mcpServers": { "todo-tracker": { "type": "local", "command": "tt", "args": ["mcp"], "tools": ["*"] } } }
```

The same plugin works in Copilot CLI (it reads Claude-format marketplaces too):

```bash
copilot plugin marketplace add shahabmokhtari/todo-tracker
copilot plugin install todo-tracker@todo-tracker
```

## VS Code (GitHub Copilot Chat)

Command palette → **MCP: Open User Configuration** (or a workspace `.vscode/mcp.json`), then add:

```json
{ "servers": { "todo-tracker": { "type": "stdio", "command": "tt", "args": ["mcp"] } } }
```

A copy is in [`integrations/vscode/mcp.json`](../integrations/vscode/mcp.json).

## Other MCP clients over HTTP

While the app runs, its MCP endpoint is `http://127.0.0.1:5317/mcp` with the header
`Authorization: Bearer <token>` (**⋯ › Copy MCP config** copies both). It only listens on this computer.

## ChatGPT, Claude.ai, Microsoft 365 Copilot and Copilot Studio

These connect **from the cloud**, so they can't reach `127.0.0.1`. They need a public HTTPS address in front of your
local server, and most require OAuth rather than a fixed token:

* **ChatGPT** (custom MCP server / developer mode): enter a public HTTPS URL to `/mcp`, or use OpenAI's Secure MCP
  Tunnel, which forwards to a local command such as `tt mcp`. Auth options are OAuth or none, so only expose it through
  the tunnel or an authenticating proxy.
* **Claude.ai / Claude custom connectors**: same requirement (public HTTPS, usually OAuth). On the desktop, prefer the
  Claude Desktop extension above.
* **Copilot Studio / Power Platform custom connectors**: import `http://127.0.0.1:5317/openapi/swagger2.json`
  (Swagger 2.0) from a machine where the app runs, then point the connector's host at your tunnel. Use *API key* auth
  with the header `Authorization` and the value `Bearer <token>`.
* **GPT Actions** and other OpenAPI tools: `/openapi/v1.json` (OpenAPI 3.1).

The API description is a curated, safe subset: reading and organizing tasks, logging notes, history and restore.
Deleting, settings and sign-in are left out.

> **Tunnels must keep the local Host header.** The app only answers requests addressed to `127.0.0.1`/`localhost`
> (that blocks DNS-rebinding attacks), so tell the tunnel to rewrite the Host header: `ngrok http 5317
> --host-header=rewrite`, cloudflared `--http-host-header 127.0.0.1:5317`, or Dev Tunnels with a host header of
> `127.0.0.1:5317`. Don't turn off "local only": that would also open the app to your network.
>
> A tunnel makes your tasks reachable from the internet. Keep the token secret, prefer tunnels that add their own
> sign-in (for example Microsoft Dev Tunnels with Entra ID, or Cloudflare Access), and stop the tunnel when you're done.

## Windows Copilot (on-device agent registry)

Windows' agent connector registry is in preview and lists MSIX-packaged MCP servers. Todo Tracker isn't packaged as
MSIX yet; until then, use the Claude Desktop or VS Code setup.

## The `tt` command

```
tt                                  what to do now (and what is waiting)
tt add 'Renew passport !! due:7d #admin'   quote it: shells treat # ! @ specially
tt add Renew passport --due 7d --tag admin  the same without quick words
tt add 'Book flights' --under trip  a subtask; <task> is an id prefix or words from the title
tt list #release                    find tasks: words, #tag, label:x, group:work, is:done
tt show passport                    a task with subtasks, notes and its markdown file
tt note passport 'Booked appointment for Tuesday'   (or tt note passport - to read it from stdin)
tt snooze passport tomorrow         defer: 45m, 2h, 3d, tomorrow, next week, fri 14:30, 2026-02-01, 2026-02-01T14:30
tt snooze "move in" after keys      wait until another task is done
tt done Renew passport              finish (several at once by id: tt done a1b2c3 d4e5f6)
tt first call the bank              put it at the top of Do now (it becomes the focus)
tt steps rollout "Ring 0" "Ring 1" --delay 24
tt edit passport --priority high --due none
tt tag passport +travel -admin      tt label passport "+Deep work"
tt move visa --under trip           --top, -g Personal, --index 0
tt attach outage ./timeline.log
tt history passport                 tt restore passport <version>
tt vault                            where the files are; tt vault guide; tt vault use <folder|Obsidian vault>
tt mcp                              the MCP server over stdio
```

Every command takes `--json` (the same shapes as the REST API and MCP tools) and `--as <name>` to attribute changes to
an agent (or set `TT_AGENT`). Exit codes: `0` done, `1` couldn't (the reason is on stderr), `2` wrong usage.

## What the AI sees

The MCP server introduces itself with short instructions (start with the dashboard, capture in the user's words,
make big things small, log progress, defer instead of piling up, never delete). The tools:

| Tool | What it does |
| --- | --- |
| `get_dashboard` | What to do now, what is waiting (with wake times), an overview per group, recent notes |
| `search_tasks`, `list_tasks`, `get_task` | Find and read tasks (search syntax: words, `#tag`, `label:x`, `group:x`, `is:done`) |
| `create_task`, `add_steps` | Capture tasks and subtasks; ordered steps that unlock one at a time |
| `add_note`, `attach_text`, `set_rich_html` | Log progress; attach text; a rich HTML version for tables and layouts |
| `schedule_next_action`, `add_reminder` | Defer and remind |
| `complete_task`, `reopen_task`, `update_task`, `move_task` | Finish, undo, change, reorganize |
| `put_first` | Put tasks at the top of Do now (when the user says what matters most) |
| `list_labels`, `vault_info`, `get_report` | Labels, where the files are (and their format), the timeline |
| `task_history`, `restore_task_version` | Every change is a version; put a task back |
| `move_card`, `archive_task`, `unarchive_task` | Board columns (inbox, next, doing); put finished tasks away |
| `start_timer`, `stop_timer`, `log_time`, `get_time_report` | Time spent on tasks, and where the time went (per day, group, task) |

There is deliberately no delete tool.
