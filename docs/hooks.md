# Hooks and events

Run your own script, or call a web address, when something happens in Todo Tracker: a task done, a reminder going off,
a break starting. Extensions and scripts can also listen to everything as it happens.

## Where hooks live

Hooks are in `hooks.json` in the app's data folder. Settings › Hooks shows the exact path. On Windows it's
`%LOCALAPPDATA%\TodoTracker\hooks.json`; the `TODOTRACKER_DATA` environment variable moves the folder.

The file is on this computer only (it isn't in the synced tasks folder). Changes apply at once (no restart). There are
four ways to change it:

* **Settings › Hooks** in the app: add, change, switch on or off, remove, approve.
* **`tt hooks`**: `list`, `add <event> --powershell … --bash … | --url …`, `edit <id>`, `enable`/`disable <id>`,
  `approve <id> <version>`, `remove <id>`, `test [event]`, `path` (`tt help hooks` for the options).
* **AI agents**: the MCP tools `list_hooks`, `add_hook`, `set_hook_enabled`, `remove_hook`; or the REST API
  (`POST /api/hooks`, `PUT /api/hooks/{id}`, `POST /api/hooks/{id}/enabled`, `DELETE /api/hooks/{id}`).
* By hand: edit the file.

**Approval.** A hook runs code on your computer, so what an AI agent (or anything else with the API token, or `tt` run
with `--as`/`TT_AGENT`) adds or changes is saved with `"needsApproval": true` and doesn't run until you approve it: in
Settings › Hooks (it shows everything it would do) or with `tt hooks approve <id> <version>` (`tt hooks list` shows
everything it would do and its version). Approving names the version you looked at: if it changed since, it isn't
approved (each change gets a new random revision, so a version can't be matched by trying). Changing a hook that waits for approval keeps it waiting;
only approving approves. Switching a hook off or removing it needs no approval.

**You're asked.** You don't have to go looking: on Windows a notification says *"Claude wants to run a hook"* with what
it would run. When that line is all of it (a short, plain command or address with nothing else set), it has **Allow**;
otherwise **Review…** opens all of it in Settings › Hooks first. Both have **Don't allow**. Without the Windows app, or
with its notifications switched off, the app's own window or browser tab (the owner's session) asks in a dialog that
shows everything. Each version is asked about once: **Allow** approves exactly what you saw, and from then on the hook
just runs, with nothing more to check; **Don't allow** removes it (only if it still waits, in the version you saw);
*Later* leaves it waiting in Settings › Hooks. If an agent changes it afterwards, you're asked again.

Who is "you": `tt` run without `--as`, and a window the app opened itself (the sidebar, Settings in the app window,
*Open in browser* from the tray or the Mac app). Those links come from the app's own process, or with the owner key
(`owner-key` next to `api-token` in the data folder, never sent by the server). A browser signed in by pasting the API
token, or with a link asked for with the token alone, is treated like anything else holding the token: what it adds
waits for approval and it can't approve (and an ordinary link never replaces an owner's session in that browser).
Hooks you add from the app or with `tt` run at once.

An agent can't set or change a hook's environment variables or folder (they could make a harmless-looking command run
something else), and sees the values of headers and variables hidden (`•••`; sent back unchanged, they keep their
values).

A change made in the app, by `tt` or by an agent rewrites the file (it never overwrites a file it can't read: fix that
first), so comments in a hand-edited file don't survive it. Each hook gets an `id`; one written by hand without it gets
one from its content.

```json
{
  "version": 1,
  "hooks": {
    "taskCompleted": [
      {
        "type": "command",
        "powershell": "$e = [Console]::In.ReadToEnd() | ConvertFrom-Json; Add-Content \"$HOME\\done.log\" \"$($e.at) $($e.task.title)\"",
        "bash": "jq -r '.at + \" \" + .task.title' >> ~/done.log",
        "timeoutSec": 30,
        "match": { "groups": ["Work"], "actors": ["user"] }
      }
    ],
    "reminderDue": [
      { "type": "http", "url": "https://example.com/webhook", "headers": { "Authorization": "Bearer …" } }
    ],
    "*": [
      { "type": "command", "name": "log everything", "bash": "cat >> ~/todo-events.jsonl; echo >> ~/todo-events.jsonl" }
    ]
  }
}
```

Each key is an event name, or `*` for every event. A hook is one of these:

| Field | Meaning |
|---|---|
| `type` | `command` (run a program) or `http` (POST the event as JSON) |
| `powershell` | What a command hook runs on Windows (with `pwsh` when it's installed, else Windows PowerShell) |
| `bash` | What a command hook runs on Linux and macOS |
| `cwd` | Working folder (default: the data folder); `%VARS%` are expanded |
| `env` | Extra environment variables |
| `timeoutSec` | Stop it after this long (default 30, at most 600); the whole process tree is stopped |
| `url`, `headers` | Where an `http` hook posts, and extra headers (it also sends `X-Todo-Tracker-Event`) |
| `match` | Only for some events: `groups`, `tags`, `labels` (any one of each list given), `actors` (`user`, `agent`, `vault`, `system`, `connector`, or an agent's name). An event without a task (a break) runs a hook only when its `match` doesn't ask about the task |
| `name` | What Settings shows for it (default: what it runs) |
| `id`, `enabled`, `needsApproval`, `addedBy`, `addedAt` | Kept by the app: its handle, `false` when switched off, `true` while an agent's hook waits for approval, and who added or changed it when |

A command hook gets the event as JSON on standard input, and `TT_EVENT`, `TT_EVENT_ID`, `TT_TASK_ID` and `TT_TASK_TITLE`
in its environment (PowerShell hooks read and write UTF-8). Hooks run one at a time, in the background, never holding
up the app; `timeoutSec` covers anything a hook leaves running that keeps its output open. What they print (the start
of it) and whether they worked shows in Settings › Hooks (the last 50 runs) and in `hooks.log` next to `hooks.json`.
**Try it** in Settings sends an example event to the hooks for the event you pick.

## Events

| Event | When |
|---|---|
| `taskCreated` | A task or subtask was added |
| `taskUpdated` | Its title, details, priority, deadline, tags, look or repeat rule changed |
| `taskCompleted` | Done (a repeating task: this time of it) |
| `taskReopened` | Not done after all |
| `taskDeleted` | Deleted |
| `taskScheduled` | Snoozed, waiting for another task, or back |
| `taskMoved` | Moved in the tree, to another group or on the board |
| `taskArchived` | Put away |
| `noteAdded` | A note was written on it |
| `reminderAdded`, `reminderDue`, `reminderDismissed` | A reminder was set, went off, was dismissed |
| `attachmentAdded`, `attachmentRemoved` | A file or picture was attached or removed |
| `labelChanged` | Its labels changed |
| `groupChanged` | A group (tab) was added, renamed or removed |
| `timeLogged` | The timer started, or time was logged, corrected or removed |
| `focusStarted`, `focusEnded`, `breakEnded` | The focus timer |

Events come from the activity log, so they fire however the change was made: in the app, by an AI agent, in Obsidian,
or by sync from another computer. Each one is sent once (also across restarts). Changes made while the app was closed
are sent when it starts, if they're less than a day old. The first time, nothing from before is sent.

## The event

```json
{
  "version": 1,
  "id": "3f9c0b1e2a7d4c5b6e8f",
  "event": "taskCompleted",
  "at": "2026-01-05T16:02:11+00:00",
  "actor": { "kind": "user" },
  "summary": "Completed \"Ship the release\"",
  "task": {
    "id": "7b0c2f9e-…",
    "title": "Ship the release",
    "path": ["Release 2.3", "Ship the release"],
    "group": "Work",
    "tags": ["deploy"],
    "labels": ["Urgent"],
    "priority": "high",
    "deadline": "2026-01-07T17:00:00+00:00",
    "done": true,
    "url": "http://127.0.0.1:5317/#/task/7b0c2f9e-…"
  }
}
```

Fields are only ever added (`version` changes if that ever isn't so). `id` is the same for the same happening, so a hook
can skip one it already handled. A deleted task has only its `id` and `title`.

## Listening: the event stream

`GET /api/events` (with the API token, like the rest of the API) streams every event as
[server-sent events](https://developer.mozilla.org/docs/Web/API/Server-sent_events): `event:` is the name, `data:` the
JSON above, `id:` its id. `?events=taskCompleted,noteAdded` picks some. A comment line every 20 seconds keeps the
connection open.

```bash
curl -N -H "Authorization: Bearer $TT_TOKEN" "http://127.0.0.1:5317/api/events?events=taskCompleted"
```

Plugins inside the server can subscribe to `HookService.Published` instead.
