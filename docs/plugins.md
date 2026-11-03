# Plugins

Every optional part of Onething is a plugin, so you can turn off what you don't use and keep the app calm.
Switch them in **Settings › Extras** (every window) or the sidebar menu (**⋯ › Plugins**).

* **Off is off at once**, everywhere: its buttons, menu items and views leave every window (web, app window, sidebar),
  its API answers 404, a paired browser extension loses access, and its background work stops (Teams cards, version
  saving, sync).
* **On**: extras that only change what you see come on at once (Connect AI apps, Focus timer, Plan, Obsidian, Browser
  extension, Copy for Loop, Energy, Send feedback, and the connectors). The ones with their own background work (Ask AI, Version history, the
  sync providers, Teams reminders) come on at the next start if they weren't running; Settings says so, and the
  sidebar offers to restart right away.

| Plugin | What it adds | On by default |
| --- | --- | --- |
| **Ask AI** | Chat with GitHub Copilot, Claude Code, or a model with an API key (OpenAI, Anthropic, Azure, Ollama…) inside the app; every chat is kept (dashboard panel and a one-line box in the sidebar) | Yes |
| **Connect AI apps** | Step-by-step setup for Claude, Copilot, VS Code, ChatGPT… | Yes |
| **Focus timer** | The Pomodoro-style timer | Yes |
| **Plan** | The Plan view: a day or week grid to plan blocks of work, with what isn't planned (and what's put off till later) beside it | Yes |
| **Energy** | Say how much energy each task takes (⚡ to ⚡⚡⚡ an hour) and how much you have in a day; cards show ⚡, Today and the plan add up each day and say when it's too much | Yes |
| **Version history** | Every change saved as a version (needs git); view and restore in the task panel | Yes |
| **Obsidian** | Open tasks in Obsidian; keep tasks in an Obsidian vault | Yes |
| **Browser extension** | Setup guide for the Edge, Chrome and Safari extension, with pairing codes ([`browser-extension.md`](browser-extension.md)) | Yes |
| **OneDrive sync** | Sync with your other computers through OneDrive (work or school account first, then personal) ([`sync.md`](sync.md)) | Yes |
| **iCloud Drive sync** | Sync with your Macs (and Windows with iCloud for Windows) through iCloud Drive | Yes |
| **GitHub gist sync** | Sync through a private gist on your GitHub account | No |
| **Notion** | Keep a group in step with a Notion database, both ways ([`connectors.md`](connectors.md)) | No |
| **Microsoft To Do** | Keep a group in step with a To Do list, both ways ([`connectors.md`](connectors.md)) | No |
| **GitHub Issues** | Each group in its own private GitHub repository: tasks are issues, both ways; repositories are made with the GitHub CLI ([`connectors.md`](connectors.md#github-issues)) | No |
| **Copy for Loop** | Copy a group's tasks as a checklist, or one task with its notes and pictures, to paste into Microsoft Loop | Yes |
| **Teams reminders** | Reminder cards in a Teams channel | Yes |
| **Send feedback** | Tell us about a bug or an idea from Settings, the menus or the command palette; it becomes an issue on Onething's GitHub ([below](#send-feedback)) | Yes |

The core (tasks, groups, ordering, notes, the markdown vault, the API, and MCP) is always on. The choice is stored in
`plugins.json` in the app's data folder and respected by every process (the app, `tt`, and `tt mcp`).

## Ask AI

Ask AI chats about your tasks with an agent you already have, over the Agent Client Protocol:

* **GitHub Copilot CLI** (`copilot --acp`), or
* **Claude Code** through its ACP adapter (`claude-agent-acp`, or a pinned version run with `npx` the first time),

or with **a model you reach with an API key** (*+ Add a model with an API key* in the picker). There are presets for
OpenAI, Anthropic, Azure OpenAI, OpenRouter, Ollama and LM Studio (on this computer, no key), and any other service
that speaks the OpenAI or Anthropic API. The address must be https, except for a model on this computer. Keys stay on
this computer, one file per model in `<data>/agent-chat/keys`: encrypted for your account on Windows (DPAPI), and
readable only by you elsewhere. A key is never shown again or sent to a screen. For a model that isn't on this
computer, the chat says where your messages (and the tasks it looks at) are sent.

An agent starts when you first ask something and then stays running (warm), so later answers are quick.

### Chats are kept

Every chat is saved as you go (`<data>/agent-chat/chats/<id>.json`). **Chats** lists them, newest first. You can search
titles and messages, open a chat to carry on, rename it, or delete it, with **Undo**. A deleted chat can be brought
back for 30 days. **New** starts another chat, and the one before is kept.

A chat stays with the agent or model it began with. Choosing someone else in the picker starts a new chat. Reopening
an agent chat carries on the agent's own session when the agent can load sessions (Copilot can; whatever it replays
isn't shown twice). Otherwise a new session starts and is given the earlier messages, and the chat says so.

### What it can do

* **It gets Onething's tools** (the same MCP tools as other AI apps). Copilot connects to the app's own MCP
  endpoint; agents that only support stdio get `tt mcp`. API models use the app's MCP endpoint too, and their changes
  are recorded as the model (by its name).
* **Reading your tasks needs no permission. Changing them asks first,** showing what would change (for example
  *Change your tasks: create task {"title":"Milk"}*): *Allow*, *Allow for this chat*, or *Don't allow*. Allowing for
  a chat is forgotten when you switch chats. Anything else (commands, files) always asks, and an unanswered question
  is refused after 5 minutes.
  A request counts as Onething's only when the tool is named the way agents name this server's tools
  (`todo-tracker-<tool>` for Copilot, `mcp__todo-tracker__<tool>` for Claude) and it isn't a command, delete or move;
  a tool name mentioned anywhere else (say, inside a command) never counts. Only "once" choices are sent to the agent,
  so nothing is remembered beyond the chat. API models only get Onething's tools, and a tool they weren't offered
  is refused.
* An API model's answer has limits: up to 12 rounds and 24 tool calls, and long tool results are cut (the model is
  told). A tool call is never retried. If an answer is stopped, the chat can still go on.
* It runs in its own working folder (`<data>/agent-chat/work`), not in your tasks folder. Copilot gets its own Copilot
  home there (without your personal MCP servers and plugins); it still signs in with `gh`. If Copilot answers that it
  isn't signed in that way, the chat switches to your normal Copilot settings and says so.
* If the agent crashes while starting, Onething tries again and then says what happened (with the exit code).

## Send feedback

**Send feedback** opens one sheet: pick **Bug**, **Idea** or **Other**, write a note (up to 2000 characters; a counter
shows near the end), add an email only if you'd like a reply, and **Send**. It says *Thanks — we read every note.*
and closes. Without a connection it keeps what you wrote and says so: **Send** again sends the same note (it's never
filed twice).

* **Where:** Settings › Feedback and the command palette (Ctrl K) in the web app; **⋯ › Send feedback…** in the
  Windows sidebar and **Send feedback…** in the tray menu (Windows and Linux), which open the web app's sheet; the
  browser extension's speech-bubble button (shown only while this extra is on). The iPhone, iPad, Mac and Android
  apps have their own sheet (see the [guide](guide.md)).
* **What's sent:** the note, the kind, the email if you typed one, and coarse facts: platform, app version, system
  version, device model (the processor type on a computer), language, screen size and how the sheet was opened
  (`button`, `shake`, `page` or `qr`). The email is cleaned like the note (control characters out, trimmed) and cut to
  200 characters.
  Never anything from your tasks, and no ids. It goes to the issues relay at `issues.awesomehq.app` (with the app's
  public key, which can only open issues; there's no GitHub token in any app), which files it as an issue on the
  public GitHub repository, email included if you gave one. See [privacy](privacy.md).
* **How:** the web app never calls the relay itself: it posts to `POST /api/plugins/feedback` and Onething on this
  computer adds the facts and sends it. Only the person at this computer can (the app's own session; an agent or a
  script with the API token gets 403). The title names the system the server runs on (Windows, macOS or Linux),
  whether the sheet is in the app window or in a browser.
* **Off:** the menu entries, the Settings section, the palette command and the browser extension's button leave at
  once, and the endpoint answers 404.

**Developers.** Debug builds log the note (without the email) instead of sending it. `ONETHING_FEEDBACK_SEND=1` sends from a debug build
(`0` never sends, even from a release build), and `ONETHING_FEEDBACK_URL` sends to another relay (the e2e tests use a
stand-in on 127.0.0.1). The Mac, iPhone and iPad apps read `ONETHING_FEEDBACK_SEND=1` from the Xcode scheme's
environment; Android debug builds send only when built with `-PonethingFeedbackSend=true`. The request body and what
each answer means are in `tests/fixtures/feedback.json`, checked in C#, Swift, Kotlin and the web app.

## Adding a plugin (developers)

A plugin is a class that implements `ITodoPlugin` (`src/TodoTracker.Server/Plugins/Plugins.cs`) and is listed in
`PluginHost.BuiltIn`:

```csharp
public sealed class MyPlugin : ITodoPlugin
{
    public PluginInfo Info { get; } = new("my-plugin", "My plugin", "What it does, in one line.");
    public string? WebModule => "/js/plugins/my-plugin.js";              // optional UI in the dashboard
    public void ConfigureServices(IServiceCollection services, TodoTrackerServerOptions options) { /* services */ }
    public void MapEndpoints(RouteGroupBuilder group) { /* routes under /api/plugins/my-plugin */ }
}
```

* Endpoints live under `/api/plugins/{id}` and share the API's security (token or session, loopback only). They are
  not part of the OpenAPI description for AI tools.
* The web module exports `activate(host)`; `host` offers `api`, `post`, `put`, `del`, `h`, `icon`, `toast`,
  `openPanel`, `closePanel`, `refresh`, `addHeaderButton`, `addTaskAction` (a button in every task's details and its
  ⋯ menu), and `on()` (whether the plugin is on now). See `wwwroot/js/plugins/agent-chat.js`.
* What a module adds through `host` is tagged with it and hidden the moment its plugin is switched off (no reload).
  Anything else it shows must check `host.on()`.
* Set `Live: true` in `PluginInfo` when the plugin has no background work of its own (its services are always
  registered): it then comes on without a restart too. Either way, endpoints under `/api/plugins/{id}` answer 404 while
  it's off, and services that keep working in the background must check `PluginHost.IsSwitchedOff(id)`.
* Native UI (the Windows sidebar) checks `PluginHost.IsRunning(id)` each time it builds a menu, and listens to
  `PluginSettings.Changed` for anything always on screen.
* Plugins are built in and reviewed with the app. Code from elsewhere is never loaded.
