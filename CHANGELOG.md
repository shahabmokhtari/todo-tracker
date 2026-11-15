# Changelog

What changed in each release of One Thing, newest first. A release is a `v*` tag; the Release workflow publishes
the apps it builds and copies the matching section below into the GitHub Release.

## [2.0.0]

The first public release. One Thing always shows **one thing to do now**, keeps deferred work out of sight until
it's due, and lets AI agents read and change your list. Your tasks are markdown files in a folder you own.

### Apps

- **Windows:** a sidebar that docks to either edge of any monitor or floats on top, with a tray icon, a global
  hotkey, toasts, and the full app in its own window. It sits beside full-screen apps and Remote Desktop.
- **Mac:** a native app with a sidebar (Today, Board, Tasks, Plan, Done, Reports), a menu bar glance and desktop
  widgets.
- **iPhone and iPad:** a native app. On iPad it has a persistent sidebar, a two-column Today and task details
  beside the list.
- **Android:** a native app with Today, Board, Tasks, Plan, Done and Reports, reminder notifications and widgets.
  On tablets it has a side rail and a two-column Today.
- **Apple TV:** one thing now, in big type, with a focus timer you run from the remote.
- **Web and browser:** the web app in any browser, and a side panel extension for Edge, Chrome and Safari.
- **Linux:** a tray app for quick add.

### Focus on one thing

- Today shows the one task to do now, then Do now, Workstreams (one step at a time), Coming up and Done today.
- **Later** snoozes a task to a time in words ("in an hour", "next Monday 9:00") or until another task is done.
- A focus timer that times the task it's on, with full-screen breaks on every screen.
- Energy: say how draining a task is and how much you have in a day.
- Repeating tasks, reminders, deadlines, icons and colors, tags and labels, search.
- Board (Inbox, Next, Doing, Done) with drag and drop, a day and week Plan, Reports with a Timeline, and an archive.

### Your tasks, your files

- Tasks are markdown files in a folder you choose (an Obsidian vault works), with version history. Nothing is ever
  lost: changes are versions, and agents can't delete.
- Sync between your devices through OneDrive, iCloud Drive or a private GitHub gist. Phones sync on their own, or
  use your computer's tasks over Wi-Fi or Tailscale.
- Connectors for Notion, Microsoft To Do and GitHub Issues that never delete.

### For AI agents

- An MCP server and the `onething` CLI (`ot` for short), a Claude Desktop extension, and a plugin with a skill for
  Claude Code and Copilot CLI. Every change says who made it.
- Ask AI: chat with Copilot, Claude or an API model about your tasks.
- Hooks and an event stream, with your approval before an agent's hook runs.
- Task links (`onething://task/<id>`) that open the app on a task.

### Also

- Send feedback from every app.
- A new app icon and the name One Thing (formerly Todo Tracker).

[2.0.0]: https://github.com/shahabmokhtari/todo-tracker/releases/tag/v2.0.0
