# Connect AI apps

One Thing is meant to be used *with* AI: tell Claude or Copilot what you need to do and it lands in your list,
ask what to work on next, let an agent log progress or break a project into steps. Every change an AI makes is
attributed to it (you'll see "Agent: claude" in the timeline) and versioned, so it can be undone.

The fastest way is **⋯ › Connect an AI app** in the sidebar (or the ✨ button in the web dashboard): pick your app,
copy one thing, done. This page explains what those snippets do and covers the apps that need more setup.

There are four ways in, from most to least local:

| Way | For | Needs the app running? |
| --- | --- | --- |
| **`onething mcp`** – MCP over stdio | Claude Desktop, Claude Code, GitHub Copilot CLI, VS Code, Cursor, any local MCP client | No |
| **`ot` CLI** (or `onething`) | Terminals, scripts, and agents that run shell commands | No |
| **`/mcp`** – MCP over HTTP | MCP clients that prefer a URL | Yes |
| **REST + OpenAPI** | ChatGPT (GPT Actions), Copilot Studio, Power Automate, scripts | Yes |

`ot` is the short name of `onething`: both are installed and do the same thing. It works directly on your tasks folder, the same one the app uses, and is safe to run alongside it (changes take
a per-folder lock and re-read the files first). Several AI apps can be connected at once.

## Get `ot`

* **Windows app:** `onething.exe` and `ot.exe` are installed in the `cli` folder next to `OneThing.exe`. "Connect an AI app"
  fills in the full path. To type `ot` in any terminal, choose **Add ot to your PATH** in the sidebar's menu (it adds
  that folder to your own PATH, no admin rights; open a new terminal after), or add the folder yourself.
* **Mac app:** `onething` and `ot` are inside the app (`OneThing.app/Contents/Resources/Server/cli`), and "Connect an AI
  app" fills in that full path, so nothing needs to be on your PATH. To type `ot` in Terminal, choose **One Thing ›
  Install Command Line Tool…** (or Settings › Command line tool › Install): it links both into `~/.local/bin` (no
  password; a file there that isn't One Thing's is only replaced if you say so, and then kept beside it), and tells you
  the one line to add to your shell profile if `~/.local/bin` isn't on your PATH yet. Moved the app? Settings shows
  the link as broken; **Repair** links it again.
* **Linux app:** the install script links `onething` and `ot` into `~/.local/bin` (most distributions have it on the
  PATH; if `ot` isn't found, add `export PATH="$HOME/.local/bin:$PATH"` to `~/.profile`).
* **.NET tool:** `dotnet tool install -g OneThing.Cli --add-source <folder with the .nupkg>` (the package is a CI
  artifact; `dotnet pack src/OneThing.Cli -o <folder>` builds it). A .NET tool has one name: `onething`.
* **Standalone:** self-contained `onething` and `ot` for Windows, macOS and Linux (`onething-cli-<platform>.zip`, no
  .NET needed) are on the Releases page and in every CI run.

`ot` uses the tasks folder the app uses (`Documents/OneThing`, or the folder chosen in the app). Override it with
`--vault <folder>` or the `ONETHING_VAULT` environment variable.

## Claude Desktop

**Extension (easiest):** download `onething-<platform>.mcpb` from the Releases page (or a CI run) and open it; Claude
shows an install dialog. Leave *Tasks folder* empty to use the app's folder.

**Or by hand:** Settings → Developer → Edit config, then add (with the path from "Connect an AI app"):

```json
{ "mcpServers": { "onething": { "command": "C:\\Program Files\\One Thing\\cli\\onething.exe", "args": ["mcp"] } } }
```

On a Mac the command is inside the app: `/Applications/OneThing.app/Contents/Resources/Server/cli/onething`. On Linux,
`~/.local/bin/onething` (with your home folder spelled out: Claude doesn't expand `~`).

Restart Claude Desktop completely after editing.

## Claude Code

```bash
claude mcp add onething --scope user -- onething mcp
```

For the tools *and* a skill that teaches Claude how to help with your tasks (capture, break down, log, defer, never
delete), install the plugin from this repository:

```
/plugin marketplace add shahabmokhtari/todo-tracker
/plugin install onething@onething
```

The plugin launches `onething` from your PATH.

## GitHub Copilot CLI

Add to `~/.copilot/mcp-config.json` (or run `/mcp add` inside Copilot):

```json
{ "mcpServers": { "onething": { "type": "local", "command": "onething", "args": ["mcp"], "tools": ["*"] } } }
```

The same plugin works in Copilot CLI (it reads Claude-format marketplaces too):

```bash
copilot plugin marketplace add shahabmokhtari/todo-tracker
copilot plugin install onething@onething
```

## VS Code (GitHub Copilot Chat)

Command palette → **MCP: Open User Configuration** (or a workspace `.vscode/mcp.json`), then add:

```json
{ "servers": { "onething": { "type": "stdio", "command": "onething", "args": ["mcp"] } } }
```

A copy is in [`integrations/vscode/mcp.json`](../integrations/vscode/mcp.json).

## Other MCP clients over HTTP

While the app runs, its MCP endpoint is `http://127.0.0.1:5317/mcp` with the header
`Authorization: Bearer <token>` (**⋯ › Copy MCP config** copies both). It only listens on this computer.

Anything with the token (an agent, a script) reads and changes tasks; its changes show as `Agent: <name>` (the
`X-OneThing-Actor: agent:<name>` header, or the MCP client's name; without one, `Agent: API client`). Some things are
yours only and answer 403 to the token alone: deleting, Ask AI, switching extras, moving the tasks folder, the Teams
webhook, sync and connected-app settings, approving, turning down or trying hooks, and switching off or removing hooks
it didn't add.

## ChatGPT, Claude.ai, Microsoft 365 Copilot and Copilot Studio

These connect **from the cloud**, so they can't reach `127.0.0.1`. They need a public HTTPS address in front of your
local server, and most require OAuth rather than a fixed token:

* **ChatGPT** (custom MCP server / developer mode): enter a public HTTPS URL to `/mcp`, or use OpenAI's Secure MCP
  Tunnel, which forwards to a local command such as `onething mcp`. Auth options are OAuth or none, so only expose it through
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

Windows' agent connector registry is in preview and lists MSIX-packaged MCP servers. One Thing isn't packaged as
MSIX yet; until then, use the Claude Desktop or VS Code setup.

## The `ot` command (or `onething`)

```
ot                                  what to do now (and what is waiting)
ot add 'Renew passport !! due:7d #admin'   quote it: shells treat # ! @ specially
ot list #release                    find tasks: words, #tag, label:x, group:work, is:done
ot tree --show now --depth 2        the tree as Tasks shows it, with each parent's progress
ot note passport 'Booked appointment for Tuesday'
ot snooze passport tomorrow         defer: 45m, 2h, 3d, tomorrow, next week, fri 14:30, after <other task>
ot done passport                    finish
ot group add Garden --color '#22c55e'   a new group (tab); then ot add 'Plant tulips' -g Garden
ot plan passport tomorrow 9:00      a block on the plan (ot plan shows it)
ot log passport 45m                 time already spent; ot focus start passport for a focus session
ot export > backup.json             everything, as JSON
ot sync                             how syncing is going (ot sync now: yours, while the app isn't running)
onething mcp                        the MCP server over stdio (ot mcp works too)
```

Every command takes `--json` (the same shapes as the REST API and MCP tools) and `--as <name>` to attribute changes to
an agent (or set `ONETHING_AGENT`). All the commands and options: [cli.md](cli.md).

`ot` never deletes anything (a task, a group, a label, an attachment or logged time): an agent in a shell runs `ot`
too, and `ot` can't tell it from you. Finish (`ot done`) or archive (`ot archive`) instead, and delete in the app.
What only you should do (switch extras with `ot plugins`, choose where reminders go with `ot settings teams`, turn
down a hook with `ot hooks reject`, move your tasks folder with `ot vault use`) works without `--as` and is refused
with it.

## What `ot` and MCP cover

Everything the app does can be done with `ot`, and everything that's safe for an agent with an MCP tool. What's
missing is missing on purpose, and says why: deleting (nothing is ever lost), the person's own settings and choices,
and what needs a person at the app (signing in, pairing a browser, Ask AI). A test keeps this table in step with
the server's endpoints.

<!-- Coverage table: made from tests/OneThing.Cli.Tests/Reach/Coverage.cs by CoverageTests. Don't edit by hand. -->

| What | `ot` | MCP tool | Why not (where missing) |
| --- | --- | --- | --- |
| What to do now, what's waiting, the timer | `ot now` | `get_dashboard` |  |
| Find tasks | `ot list` | `search_tasks` |  |
| Every task | `ot list --all` | `list_tasks` |  |
| One task | `ot show`, `ot open` | `get_task` |  |
| The task tree (groups, progress, one branch, what's shown) | `ot tree` | `get_tree` |  |
| Add a task | `ot add` | `create_task` |  |
| Quick capture (!! due:fri #tag @tomorrow) | `ot add` | `capture_task` |  |
| Change a task (title, details, priority, deadline, tags, labels, icon, color, repeat, energy, steps) | `ot edit`, `ot tag`, `ot label` | `update_task` |  |
| Delete a task | — | — | Deleting is the person's, in the app (nothing is ever lost): agents finish, archive or move instead, and ot can't tell a person from a shell agent. |
| Ordered steps | `ot steps` | `add_steps` |  |
| Done and undo | `ot done`, `ot reopen` | `complete_task`, `reopen_task` |  |
| Notes (with a web page), and correcting one | `ot note`, `ot note --edit` | `add_note`, `update_note` |  |
| Snooze, and back to Do now | `ot snooze`, `ot snooze <task> clear` | `schedule_next_action`, `clear_snooze` |  |
| Wait for another task | `ot snooze <task> after` | `wait_for_task` |  |
| Tasks a task can wait for (the app's picker) | — | — | The app's picker for After another task…: ot snooze &lt;task&gt; after &lt;words&gt; and wait_for_task find the task themselves. |
| Reminders, and dismissing one | `ot remind`, `ot remind --dismiss` | `add_reminder`, `dismiss_reminder` |  |
| Do now's order | `ot first`, `ot order` | `put_first`, `arrange_now` |  |
| Next to a sibling | `ot move --before`, `ot move --after` | `reorder_task` |  |
| Under another task, top level, another group | `ot move` | `move_task` |  |
| Board columns | `ot stage` | `move_card` |  |
| Archive and unarchive | `ot archive`, `ot unarchive` | `archive_task`, `unarchive_task` |  |
| Archive everything finished a while ago | `ot archive --done-before` | — | An agent archives one finished task at a time (archive_task), so the person sees each. |
| The timeline (who did what) | `ot timeline` | `get_report` |  |
| Groups (tabs) | `ot groups` | `list_groups` |  |
| Make a group | `ot group add` | `create_group` |  |
| Rename or recolor a group | `ot group rename`, `ot group color` | `update_group` |  |
| Delete a group | — | — | Deleting is the person's, in the app (nothing is ever lost): agents finish, archive or move instead, and ot can't tell a person from a shell agent. |
| Labels | `ot labels` | `list_labels` |  |
| Make, rename or recolor a label | `ot labels add`, `ot labels rename`, `ot labels color` | `create_label`, `update_label` |  |
| Delete a label | — | — | Deleting is the person's, in the app (nothing is ever lost): agents finish, archive or move instead, and ot can't tell a person from a shell agent. |
| Tags in use | `ot tags` | `list_tags` |  |
| Time a task | `ot start`, `ot stop` | `start_timer`, `stop_timer` |  |
| Log time, and correct an entry | `ot log`, `ot log --edit` | `log_time`, `update_time_entry` |  |
| Remove logged time | — | — | Deleting is the person's, in the app (nothing is ever lost): agents finish, archive or move instead, and ot can't tell a person from a shell agent. |
| Where the time went | `ot time` | `get_time_report` |  |
| The focus (Pomodoro) timer | `ot focus` | `focus_timer` |  |
| The focus timer's lengths (focus, break, long break, sessions before it) | `ot focus lengths` | `set_focus_lengths`, `get_dashboard` |  |
| The plan | `ot plan`, `ot plan settings` | `get_plan` |  |
| Plan or unplan a task | `ot plan <task>` | `plan_task` |  |
| How the Plan view looks | `ot plan settings --view` | — | How the Plan view looks (work hours, work days) is set in the app or with ot plan settings, also with --as (the API lets anything with the token set it); no MCP tool, get_plan reads them. |
| Energy | `ot energy` | `get_dashboard`, `set_daily_energy` |  |
| Attach a file | `ot attach` | `attach_text` |  |
| Read an attachment | `ot attachment` | `read_attachment` |  |
| A picture shown inside details | — | — | How the app shows a pasted image in a task's details: ot attachment and read_attachment get the file. |
| Remove an attachment | — | — | Deleting is the person's, in the app (nothing is ever lost): agents finish, archive or move instead, and ot can't tell a person from a shell agent. |
| Rich HTML | `ot rich` | `get_rich_html`, `set_rich_html` |  |
| Versions of the whole folder | `ot history` | `board_history` |  |
| Save a version now | — | — | ot and onething mcp save a version after every change themselves. |
| Versions of a task, a version's text, going back | `ot history <task>`, `ot history <task> --text`, `ot restore` | `task_history`, `get_task_version`, `restore_task_version` |  |
| Export everything | `ot export` | `export_board` |  |
| The tasks folder and its format | `ot vault`, `ot vault guide` | `vault_info` |  |
| Obsidian vaults on this computer | `ot vault obsidian` | — | For the person choosing where tasks live. |
| Move where the tasks live | `ot vault use` | — | The person's choice, in the app: ot refuses it with --as. |
| Settings | `ot settings`, `ot settings theme` | — | The person's choice, in the app: an agent has no settings tool. |
| Where reminders are posted (Teams) | `ot settings teams` | — | The person's choice, in the app: ot refuses it with --as. |
| Extras (plugins) on and off | `ot plugins`, `ot plugins on`, `ot plugins off` | — | The person's choice, in the app: ot refuses switching with --as. |
| Hooks: list, add, change, on/off, remove | `ot hooks`, `ot hooks add`, `ot hooks edit`, `ot hooks enable`, `ot hooks remove` | `list_hooks`, `add_hook`, `update_hook`, `set_hook_enabled`, `remove_hook` |  |
| Approve or turn down a hook | `ot hooks approve`, `ot hooks reject` | — | Only the person allows what runs on their computer: ot refuses it with --as. |
| Try hooks now | `ot hooks test` | — | Runs scripts on this computer now: the person's. |
| Sync: status and sync now | `ot sync`, `ot sync now` | — | Syncing runs in the app by itself every few minutes; an agent has nothing to set or start. Syncing now is the person's (ot sync now refuses --as); ot sync shows the status to anyone, and runs only while the app doesn't (it syncs by itself). |
| Sync: set up, clashes, devices | — | — | Setting up sync and choosing a side in a clash are the person's, in Settings › Sync. |
| Connectors (Notion, Microsoft To Do, GitHub): status and sync now | `ot connectors`, `ot connectors sync`, `ot connectors sync github` | — | Syncing runs in the app by itself every few minutes; an agent has nothing to set or start. Syncing now (which starts one not syncing yet, like Start syncing in the app) is the person's: ot connectors sync refuses --as and runs only while the app doesn't; ot connectors shows the status to anyone. |
| Connectors: set up, sign in, preview, disconnect | — | — | Signing in to another service and choosing what to connect are the person's, in Settings › Connected apps. |
| Connect an AI app (setup snippets) | — | — | Setup for the person connecting an app (this page has it too); an agent is already connected. |
| Ask AI (chat in the app) | — | — | Ask AI is a person chatting with an agent inside the app; an agent outside it is already connected. |
| Browser extension: pairing and devices | — | — | Pairing a browser needs the person at the browser and the app at once. |
| Send feedback | — | — | A person's note to the developers, sent from the app. |
| Sign-in and the app's own connection | — | — | How the app's own windows sign in and talk to the server: ot and onething mcp open the tasks folder directly and need no token. |
| Live events for the app's windows | — | — | The app's windows follow changes with it; scripts use hooks (ot hooks). |
| MCP over HTTP | `onething mcp` | — | It is the MCP server (onething mcp is the same over stdio). |
| The REST API's description | — | — | For HTTP-based AI tools (ChatGPT, Copilot Studio); ot and MCP describe themselves. |

<!-- End of the coverage table. -->

## What the AI sees

The MCP server introduces itself with short instructions (start with the dashboard, capture in the user's words,
make big things small, log progress, defer instead of piling up, never delete), and every tool and parameter is
described for the model. The full list, made from the code: [mcp.md](mcp.md). There is deliberately no delete tool.

For Claude Code and Copilot CLI, the plugin adds a skill
([`SKILL.md`](../integrations/agent-plugin/skills/onething/SKILL.md)) with recipes: what to do now, follow-ups
after a meeting, planning a day, rollouts, end of day, automations.
