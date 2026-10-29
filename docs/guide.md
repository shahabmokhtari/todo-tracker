# Todo Tracker guide

How to use every part of Todo Tracker. New here? Start with the [README](../README.md); for AI apps see
[ai-connectors.md](ai-connectors.md).

## The Windows sidebar

The sidebar docks on the right. To move it, use **⋯ › Position**:
- **Dock right** or **Dock left** reserves that edge, on any monitor.
- **Float as a window** makes it a normal window you can drag and resize. Turn on **⋯ › Always on top** if you want it
  to stay above other windows.

Your choice is remembered. Todo Tracker also sits in the notification area (the tray): click its icon to bring the
sidebar back; right-click it to open the full window, add a task, start or pause the focus timer, hide the sidebar
(it gives the screen edge back) or quit. A red dot on it means a reminder is due.

**⋯ › Hide the sidebar** puts it away until you want it back: it keeps running in the notification area, also after a
restart. Click the tray icon, press Ctrl+Alt+Space, or start Todo Tracker again to bring it back.

**Full-screen apps and Remote Desktop:** a full-screen window (a Remote Desktop session, a video) ignores the edge the
sidebar reserves, so the sidebar goes behind it rather than covering part of it. A full-screen Remote Desktop session
you're using is resized to sit next to the sidebar instead (turn this off in **⋯ › Position › Make room for
full-screen Remote Desktop**); it goes back to full screen when you hide the sidebar or quit.

**Order Do now your way:** drag a card (drop it on the big card to make it the focus),
or use its ↑/↓ buttons or Alt+↑/↓; **Skip** on the big card does something else first (it moves down one). New tasks
go below the ones you arranged, so your focus stays put. A reminder that
goes off brings its task to the top; move it anywhere after that and it stays there. Subtasks reorder the same way in
the web task panel.

**On every card:** **Mark done**, **Later**, **Note** (the sticky note), **Focus**, **Timer**, and **⋯** for the
rest: **Dismiss reminder** (keeps the task, stops showing the reminder), **Copy link**, and what extras add (Copy for
Loop; Open in Obsidian in the sidebar). Same on Windows, the web, Mac, iPhone and Android.

**Notes on cards.** The task you're on (the big card) shows every note, newest first, with who wrote it and when. Any
other card says **2 notes ▾**: click it to read them right there, **Hide notes ▴** to fold them away.

**Open a task.** Click its title (in the sidebar it opens full size in the app window), or **⋯ › Open in the app** /
**Open in the browser** in the sidebar, **⋯ › Open full size** / **Open in a new tab** on the web, **⋯ › Open details** /
**Open in the browser** on a Mac, and on Android tap its title (or **⋯ › Open**).

Type a task in the box at the top and press Enter:

```
Deploy ring 2 !! @2h due:tomorrow      →  critical, back in 2h with a reminder, due tomorrow 17:00
Plan the offsite @next-week            →  back next Monday 9:00   (also @fri, @weekend, @tonight, @2w…)
```

**Not now?** Press **Later** on a task: back in 15 minutes, an hour, 3 hours, this evening, tomorrow morning, in 2 days,
next Monday, in a week or a month (each says when, e.g. *Fri 9:00*). Or **Pick a time…** and type it the way you'd say
it (`next week`, `fri 14:30`, `3d`, `weekend`, `2026-03-01 14:00`; it shows when that is before you snooze), or pick a
date. Or **After another task…**: it waits until that task is done, then comes back with a reminder. Every app offers
the same choices (`tt snooze <task> next week`, `tt snooze <task> after <other task>`, and the AI tools too).

**Again and again?** Type a rule in a task's **Repeat** box: `every weekday`, `every 2 weeks on Monday, Thursday`,
`every month on the last Friday`, `every 3 days when done`. Done moves it to its next time (deadline and all) instead
of finishing it; it still counts as done today. Done by mistake? **Reopen** it (or **Undo**) and it's back as it was,
with the subtasks you'd already done still done.

**Your own automations.** Hooks run a script or call a webhook when a task is done, a reminder goes off, a break
starts… Add them in Settings › Hooks, with `tt hooks add`, or ask your AI agent (the app asks you to allow its hooks: Allow once and they just run).
Extensions can listen to `GET /api/events`. See [docs/hooks.md](hooks.md).

## The full app (web and app window)

The web app (and the app window on Windows) has six views; switch with the side bar, `Alt+1`…`Alt+6`, or `Ctrl+K`:

- **Today**: what to do now (the same list as the sidebar).
- **Board**: Inbox → Next → Doing → Done. Drag cards along, or focus one and press `Alt+←/→` (`Alt+↑/↓` within a
  column). Doing is kept to three on purpose. New tasks start in Inbox.
- **Tasks**: every task as an outline. Type to rename, `Enter` for the next task, `Tab`/`Shift+Tab` to nest and
  un-nest, `Alt+↑/↓` to move, `Space` to finish, `Esc` to leave; or drag a row before, after or into another.
- **Done**: what you finished, day by day, with the time it took. Archive finished tasks (or everything older than a
  week or a month) to keep it short; bring them back from **Archive**, or delete them.
- **Reports**: time tracked, focus sessions and tasks done for a week, a month, three months or a year, with time per
  day, a donut by group, where the time went, when you work (weekday × hour), active days, and a timeline of each
  task's work, deadline and finish.
- **Plan**: your day or week as a time grid. Drag a task from *Not planned* onto a time to plan a block of work on it,
  drag the block to move it or its bottom edge to change how long; while a block is on, that task is the one to do
  now. Tasks you put off (snoozed, waiting for another task, a repeating task until its next time) are under
  *Later*, with when each comes back, and marked on that day; drag them in too. Each group keeps its own view (day or
  week, work hours or the whole day). An extra: switch it off in Settings.

**How much energy do you have?** (the Energy extra) Say how draining a task is: ⚡ (light), ⚡⚡ or ⚡⚡⚡ (draining) an
hour, in its details (or ⋯ › Energy on a card in the sidebar, on a Mac or an iPhone; not said counts as ⚡). Say how much you have in a day in Settings ›
Energy (10 unless you change it; every app uses the same number). Today then says "⚡ 4.5 of 10 planned today", and
each day in the Plan view has a meter: its blocks' hours times their ⚡. Past what you have, it turns red, so you can
move something to another day before you run out. `tt energy 8` and `tt edit <task> --energy 3` do it from a terminal,
and AI apps can too.

Open a task to edit everything in one place; the expand button makes it full size (with its own address, so Back
closes it). Its **Time** section starts or stops the timer and lists the time spent, which you can fix, remove or add
by hand. Starting a focus session on a task starts that task's timer too (the session's time counts as focus time on
it). When the session ends, a full-screen break reminds you to rest: take it, skip it, or **Start next focus now**
(same task, the break ends). When a break you saw runs out, it asks **Break's over: Start next focus** or **Not now**;
on Windows the notification has the button too. The phone apps do the same (Android and iPhone), and say
when a focus session or a break ends with the app closed.

To set up a rollout, open the task (⋯ › *Edit details in browser* › *Add rollout steps*), enter one step per line,
and choose 24 hours between steps. Only the current step shows in **Do now**. When you finish it, the next step
waits 24 hours and then comes back with a reminder.

**Links to a task.** The link button in a task's header (or **⋯ › Copy link** on any card) copies
`todotracker://task/<id>`; paste it in a note, a chat, a mail or a calendar invite (rich editors show it as a link
named after the task). Clicking it brings the app up on that task: on Windows the app window opens on it (and the app
starts if it wasn't running), on macOS, iPhone and Android the task's details open,
and on Linux the full window opens on it. `tt show` prints a task's link, `tt open <task>` opens it, and AI apps get
it with every task (`link`). The app registers the link for you when it starts (Windows: per user, no admin rights;
Linux: `install.sh`).

## The Android app

Today on your phone: the one thing to do now, the rest of Do now, what's waiting, what you did today (and when),
**Workstreams** (tasks with subtasks, with how far along each is) and **Recent notes**. Tap a title, a workstream or a
note to open that task.

**A task's details** fill the screen. Change the title and details (saved when you close it), the priority, the
deadline, how much energy it takes, how it repeats (type a rule or pick one) and its group. Mark it done or reopen it,
put it off with **Later**, archive it once it's done, copy its link, or delete it (it asks first: deleting can't be
undone, archiving keeps it). Below: its subtasks (tick them off, tap one to open it; the path at the top takes you
back), **Steps in order & rollout steps** (one step after another, with hours between them), reminders, and notes.

**Reminders tell you, even with the app closed.** When a reminder is due, or something you snoozed comes back, your
phone shows a notification: **Mark done** (or **Open**, when it has subtasks still to do), **In an hour**, or tap it to
open the task. With notifications off, it waits on Today and tells you once they're on. The first time you snooze or
set a reminder, the app asks to send notifications, and once whether it may set alarms so they come right on time.
After your phone restarts, reminders you missed show up.

**Later** offers the same quick choices as everywhere, **Pick a time…** (a day, then a time; times already past can't
be picked) and **After another task…**. Changed your mind? Tap **Undo** on the message, like after **Mark done**.

**Find a task:** tap 🔍 and type. Words match the title or details; `is:done`, `is:archived` and `group:work` work as
on the web.

**Groups:** tap the group you're in again (or **Edit**) to rename it, give it a color, or delete it. Its tasks move to
the group you pick, so none are lost.

**Settings** (⚙): light, dark, or as your phone is; how much energy you have in a day; how long a focus session and its
breaks last; and whether notifications and on-time alarms are on.

## Where your tasks live

Tasks are markdown files in **Documents/Todo Tracker**: one folder per group (tab), one file per task, with subtasks
as checkboxes (Obsidian Tasks syntax), notes as callouts, and attachments next to them. Edit them anywhere: in the
app, in Obsidian, in an editor, or with an AI agent. The app picks the changes up. See
[`docs/vault-format.md`](vault-format.md).

- **Use Obsidian:** **⋯ › Tasks folder › Keep tasks in an Obsidian vault** lists your vaults. The app then uses
  `<vault>/Todo Tracker` (restart to switch). Any task opens in Obsidian from its menu or the web drawer.
- **Pointing the app at existing notes is safe.** It reads them without rewriting anything until you change a task.
- **History:** every change is saved as a version (needs `git`). Open a task in the web drawer › *History* to view or
  restore a version.
- **Tags, labels, search:** add `#tags` and colored labels to tasks; click one (or press `/`) to filter. The same
  syntax works everywhere: `#release label:"Deep work" group:work is:done words`.

## Connect AI apps

Choose **⋯ › Connect an AI app** (or ✨ in the web dashboard), pick Claude Code, Copilot CLI, Claude Desktop, VS Code
or another app, and copy the one line it shows. For example:

```bash
claude mcp add todo-tracker --scope user -- tt mcp
```

`tt mcp` works on your tasks folder directly, so AI apps work even when Todo Tracker isn't running. There is also a
Claude Desktop extension (`.mcpb`), a plugin with a skill for Claude Code and Copilot CLI
(`/plugin marketplace add shahabmokhtari/todo-tracker`), and OpenAPI descriptions for cloud tools. See
[`docs/ai-connectors.md`](ai-connectors.md), including ChatGPT and Copilot Studio.

## Ask AI and plugins

Type in the **Ask AI** box at the bottom of the sidebar (or the chat button in the dashboard): Copilot, Claude Code,
or a model you add with an API key (OpenAI, Anthropic, Azure, OpenRouter, Ollama, LM Studio…) adds, finds and
organizes tasks for you, asking before it changes anything. Every chat is kept: **Chats** finds, reopens, renames or
deletes them. Turn any extra off under **⋯ › Plugins**. See [`docs/plugins.md`](plugins.md).

## Browser extension

Edge, Chrome and Safari. Open **Browser extension** (puzzle button in the dashboard, or **⋯ › Set up the browser
extension** in the sidebar): it walks you through loading it (it isn't in the stores yet) and pairs it with a 6-digit
code. See [`docs/browser-extension.md`](browser-extension.md).

## Phones

The phone apps (iPhone, iPad, Android) can use the tasks on your computer, at home on Wi-Fi or anywhere with
Tailscale. Turn on the **Phones** extra (Settings › Extras), then in **Settings › Phones** (or **⋯ › Connect a phone…**
in the sidebar) turn on **Let phones connect** and restart Todo Tracker. **Connect a phone** shows a QR code: scan it in
the phone app (it works once, for 10 minutes). **Connected phones** lists them; **Disconnect** takes one's access back.
A phone only reaches your tasks, never settings, hooks or AI apps. See [`docs/plugins.md`](plugins.md#phones).

## Teams reminders

Choose sidebar menu **⋯ › Connect Teams reminders…** and paste a Teams Workflows webhook URL.

## Sync your computers

Your tasks folder stays in step with your other computers through **OneDrive**, **iCloud Drive** or a **private GitHub
gist**: changes made on both are merged, and a conflict never loses either side. Turn it on in Settings › Sync. See
[sync.md](sync.md).

## Connectors: Notion, Microsoft To Do, GitHub Issues

Keep a group in step with a Notion database, a Microsoft To Do list, or its own private GitHub repository (tasks as
issues; snoozes, reminders and the rest as labels like `snooze:2026-10-20T14:30Z`; the repositories are made for you
with the GitHub CLI). You can even keep your tasks only on GitHub, without a tasks folder. Nothing is ever deleted on
either side. See [connectors.md](connectors.md).

## Hooks: your own automations

Run a script or call a webhook when a task is done, a reminder goes off, a break starts… Add them in Settings › Hooks
or with `tt hooks add`; an AI agent can add them too (you're asked to allow each one). Extensions can follow every
event at `GET /api/events`. See [hooks.md](hooks.md).

## Linux

CI builds `todo-tracker-linux-x64.tar.gz` (artifact **linux-tray**; no .NET needed). Unpack it and run `./install.sh`
(add `--autostart` to start it with your session): it puts Todo Tracker in your app menu and `todo-tracker` and `tt` in
`~/.local/bin`. The icon sits in the tray; on GNOME, tray icons need the AppIndicator extension. A second start opens
the running one's window.

## The iPhone, iPad and Mac app

Today works like everywhere else: the Do this now card, Do now, Waiting, Done today (with when each was done),
Workstreams and Recent notes (tap one to open its task).

* **Task details** (tap a title): change the title, details, priority, deadline (or clear it), repeat rule (type it in
  words or pick one; if a rule isn't understood, you're told why), energy and group. From there you can also snooze it
  (*Later…*), set a reminder (*Remind me…*, with an optional message), mark it done or reopen it, time it, and add notes.
* **Subtasks** show at every depth with done and reopen buttons; tap one to open it, *Back* to return. A subtask shows
  *Part of …*, which opens its parent. *Steps in order* and *Hours between steps* can be changed any time.
* **Waiting tasks say why** ("Waits for “X” and snoozed until Fri 9:00"), with *Do now* and *Open parent*.
* **Archive** a finished task to put it away; *Unarchive* brings it back. *Delete* asks first.
* **Groups:** long-press a group tab (or ⋯ next to the tabs) to rename it, change its color, or delete it. Its tasks move
  to the group you pick; they're never deleted.
* **Search** (magnifying glass): the same words and filters as the other apps (`group:`, `is:done`, `is:archived`,
  quotes).
* **Settings** (gear): light or dark, energy in a day, focus timer lengths and full-screen breaks.
* **Not done after all** on a repeating task puts it back as it was: the subtasks you'd done stay done and its
  reminders come back.

### Use your computer's tasks on an iPhone or iPad

On its own, the iPhone and iPad app keeps its tasks on the device. It can use the tasks on your computer instead, over
your Wi-Fi at home or anywhere with Tailscale:

1. On the computer, turn on the **Phones** extra, then **Settings › Phones › Let phones connect**, and restart Todo
   Tracker. Choose **Connect a phone**: it shows a QR code and a link.
2. On the phone, open **Settings › Computer › Connect to a computer…**, check **This phone's name** (the computer lists
   the phone by it; type e.g. "Shahab's iPad", since the phone only offers "iPad"), and scan the code (or paste the
   link; opening the link on the phone works too). The code works once, for 10 minutes.

From then on the phone shows the computer's tasks and your changes go straight there (the timeline says they came from
your phone). Settings says **Connected to** your computer. Only opening the full app in the browser stays on the
computer.

* **Away from its Wi-Fi** (and not on Tailscale), you still see your tasks as they were when the phone last reached the
  computer, with the time: "Can't reach … You're seeing your tasks from 6:05 PM; changes wait until you're back on its
  Wi-Fi or Tailscale." Changes are off until then. The phone tries again every 30 seconds and each time you open the
  app; pull down to try now.
* **Disconnect** (Settings › Computer) takes the phone back to its own tasks, just as you left them: they're never
  touched while it's connected. To remove the phone on the computer too, use **Disconnect** in Settings › Phones there.
* If the computer **disconnects the phone**, the app says so and goes back to the phone's own tasks; **Reconnect**
  pairs it again.
* The phone only trusts your computer: it checks the computer's certificate against the fingerprint in the code, and
  keeps its key in the Keychain.

## The server on its own, and the Apple apps from source

```bash
# Web dashboard / API / MCP without the WPF shell (any OS)
dotnet run --project src/TodoTracker.Web -- --port 5317 --data ./data

# macOS / iOS (on a Mac with Xcode)
cd apple/TodoTrackerKit && swift test
cd apple && brew install xcodegen && xcodegen generate && open TodoTracker.xcodeproj
```

