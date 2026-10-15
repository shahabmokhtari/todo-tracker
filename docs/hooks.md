# Hooks and events

Run your own script, or call a web address, when something happens in Todo Tracker: a task done, a reminder going off,
a break starting. Extensions and scripts can also listen to everything as it happens.

## Where hooks live

Hooks are in `hooks.json` in the app's data folder. Settings › Hooks shows the exact path. On Windows it's
`%LOCALAPPDATA%\TodoTracker\hooks.json`; the `TODOTRACKER_DATA` environment variable moves the folder.

The file is on this computer only. It isn't in the synced tasks folder, and nothing in the app writes it: no API, no AI
tool. Only someone who can edit files on this computer decides what runs. Changes apply at once (no restart).

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

A command hook gets the event as JSON on standard input, and `TT_EVENT`, `TT_EVENT_ID`, `TT_TASK_ID` and `TT_TASK_TITLE`
in its environment. Hooks run one at a time, in the background, never holding up the app. What they print (the start
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
