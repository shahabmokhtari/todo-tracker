# Plugins

Every optional part of Todo Tracker is a plugin, so you can turn off what you don't use and keep the app calm.
Switch them in the dashboard (**Plugins** button in the header) or the sidebar menu (**⋯ › Plugins**). A change
applies after a restart; the sidebar offers to restart right away.

| Plugin | What it adds | On by default |
| --- | --- | --- |
| **Ask AI** | Chat with GitHub Copilot, Claude Code, or a model with an API key (OpenAI, Anthropic, Azure, Ollama…) inside the app; every chat is kept (dashboard panel and a one-line box in the sidebar) | Yes |
| **Connect AI apps** | Step-by-step setup for Claude, Copilot, VS Code, ChatGPT… | Yes |
| **Focus timer** | The Pomodoro-style timer | Yes |
| **Version history** | Every change saved as a version (needs git); view and restore in the task panel | Yes |
| **Obsidian** | Open tasks in Obsidian; keep tasks in an Obsidian vault | Yes |
| **Browser extension** | Setup guide for the Edge, Chrome and Safari extension, with pairing codes ([`browser-extension.md`](browser-extension.md)) | Yes |
| **OneDrive sync** | Sync with your other computers through OneDrive (work or school account first, then personal) ([`sync.md`](sync.md)) | Yes |
| **iCloud Drive sync** | Sync with your Mac, iPhone and iPad through iCloud Drive | Yes |
| **GitHub gist sync** | Sync through a private gist on your GitHub account | No |
| **Teams reminders** | Reminder cards in a Teams channel | Yes |

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

* **It gets Todo Tracker's tools** (the same MCP tools as other AI apps). Copilot connects to the app's own MCP
  endpoint; agents that only support stdio get `tt mcp`. API models use the app's MCP endpoint too, and their changes
  are recorded as the model (by its name).
* **Reading your tasks needs no permission. Changing them asks first,** showing what would change (for example
  *Change your tasks: create task {"title":"Milk"}*): *Allow*, *Allow for this chat*, or *Don't allow*. Allowing for
  a chat is forgotten when you switch chats. Anything else (commands, files) always asks, and an unanswered question
  is refused after 5 minutes.
  A request counts as Todo Tracker's only when the tool is named the way agents name this server's tools
  (`todo-tracker-<tool>` for Copilot, `mcp__todo-tracker__<tool>` for Claude) and it isn't a command, delete or move;
  a tool name mentioned anywhere else (say, inside a command) never counts. Only "once" choices are sent to the agent,
  so nothing is remembered beyond the chat. API models only get Todo Tracker's tools, and a tool they weren't offered
  is refused.
* An API model's answer has limits: up to 12 rounds and 24 tool calls, and long tool results are cut (the model is
  told). A tool call is never retried. If an answer is stopped, the chat can still go on.
* It runs in its own working folder (`<data>/agent-chat/work`), not in your tasks folder. Copilot gets its own Copilot
  home there (without your personal MCP servers and plugins); it still signs in with `gh`. If Copilot answers that it
  isn't signed in that way, the chat switches to your normal Copilot settings and says so.
* If the agent crashes while starting, Todo Tracker tries again and then says what happened (with the exit code).

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
  `openPanel`, `closePanel`, `refresh`, and `addHeaderButton`. See `wwwroot/js/plugins/agent-chat.js`.
* Plugins are built in and reviewed with the app. Code from elsewhere is never loaded.
