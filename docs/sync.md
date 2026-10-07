# Sync

Your tasks stay in a folder on each computer. Sync keeps those folders in step through a place every computer can
reach, and merges changes made on more than one of them. It is on by default and needs no setup when OneDrive or
iCloud Drive is on the computer.

| Where | Plugin | Used |
| --- | --- | --- |
| **OneDrive** | OneDrive sync | First choice. A work or school account first, then a personal one. |
| **iCloud Drive** | iCloud Drive sync | When there is no OneDrive (Mac, or iCloud for Windows). |
| **A private GitHub gist** | GitHub gist sync (off by default) | When you pick it. Signs in with the GitHub CLI (`gh auth login`) or `GH_TOKEN`. |

Each is a plugin: turn them on or off under **Plugins**. Open the **Sync** panel from the cloud button in the dashboard
header (or **⋯ › Sync** in the sidebar) to see where it syncs, when it last did, to sync now, to pick another place or
turn sync off.

## How it works

* On OneDrive and iCloud Drive, computers meet in **`Apps/TodoTrackerSync/<library>`**. Each computer writes only its own files
  there: `devices/<id>.json` (what its tasks folder holds) and `blobs/` (the contents, named by their hash; contents are
  written first, the list last, so a half-uploaded change is never read).
* A gist (one per library) holds one file per computer (`device-<id>.json`, up to 9 MB including attachments: bigger tasks folders should sync through OneDrive or iCloud Drive). Each check sends the gist's **ETag**
  (`If-None-Match`); GitHub answers *304 Not Modified* when nothing changed, so idle checks cost almost nothing.
  After its own write a computer only treats the gist as "seen" when the gist's history shows no other write in
  between.
* It syncs when the app starts, a few seconds after you change something, and every minute (to pick up the other
  computers' changes). Nothing is read or written when neither side changed.
* The tasks folder itself is never moved. If it already is inside OneDrive (say, in OneDrive's Documents), OneDrive is
  not used for sync: OneDrive already copies the folder as it is, and both at once would copy every change twice. Move
  the tasks folder out of OneDrive (**⋯ › Tasks folder › Choose another folder…**) to sync with merging instead.

## Libraries and computers

* A **library** is a set of tasks that syncs together. It is named after the tasks folder (`Todo Tracker` by default), so the usual folder on every computer meets the others, and a second tasks folder with another name stays separate. Change the name in the Sync panel to join or split them.
* The Sync panel lists the **other computers** and when each last synced. **Forget** one you no longer use (or an old tasks folder of this computer) and it's no longer merged.

## Changes on more than one computer

Every computer remembers what it last agreed on with each other computer, and merges three-way against that:

* **Different tasks, or different parts of a task** (one computer checks off a step, the other renames the next one;
  one adds a note, the other changes the due date): both changes are kept, the same way on every computer.
* **Moves and renames** follow the task (tasks are matched by their id, not their file name).
* **Tabs, labels and the Do now order** are merged item by item.
* **The same line changed on two computers:** both versions are kept in the task, and the Sync panel lists it under
  *Changed on both computers*: **Keep mine** or **Keep <other computer>'s** (only where the two clashed; every other change from both stays; available once the other computer has synced the merged task too, usually within a minute), **Keep both**, or compare them side by side
  in **Beyond Compare**, **WinMerge** or **Visual Studio Code** (whichever is installed; you edit the task on the right).
* **Two versions that can't be merged** (a task created on both before they ever synced, a binary attachment changed
  on both): the other one is kept next to it as *"Title (from <computer>)"*.
* **Deleted on one computer, changed on another:** the change wins (nothing you typed is lost). Deleted files go to
  the tasks folder's `.todo-tracker/trash` (kept for 30 days).
* A computer remembers what it deleted for 180 days, so an old computer that comes back can't bring deleted tasks
  back. Computers not heard from for 180 days are no longer merged.

When two computers had to choose (both moved the same task, say), they make the same choice, so they always end up
with the same files.

## On a Mac

The Mac app comes with Todo Tracker's server inside. It starts in the background with the app (and stops when the app
quits), so the Mac uses the same tasks folder as everywhere else, `~/Documents/Todo Tracker`, and syncs it like a
Windows computer does:

* **With Windows:** both need the same cloud. Install OneDrive on the Mac (it's used first, a work or school account
  before a personal one), or iCloud for Windows on the PC. Both meet in `Apps/TodoTrackerSync/<library>`.
* **The first time,** the tasks the Mac app kept on its own (before it synced) move into the tasks folder and sync
  from there. They are added to the other computers' tasks, never replace them (a task already there stays as it
  is). The old file is kept as `board.json.migrated`. While the folder opens, changes wait a moment.
* Version history needs git (Xcode's command line tools, or Homebrew's git); without it, history is off on the Mac.
* **More:** the menu bar glance shows where the tasks are and **Open Todo Tracker…** opens the full window in the
  browser (board, reports, Settings, the Sync panel).
* The server comes for Apple silicon Macs. If it can't start, the app says so and keeps the tasks on the Mac
  (`~/Library/Logs/TodoTracker/server.log` says why). A downloaded, unsigned app may need
  `xattr -dr com.apple.quarantine "/Applications/TodoTracker.app"` once.
* **iPhone and iPad** keep their tasks on the device for now: they can't run the server, and reading the tasks folder
  directly needs the vault format in Swift (planned).
## Safety

* Only files inside the tasks folder's own folders sync; never `AGENTS.md`, hidden folders (`.obsidian`, `.git`,
  `.todo-tracker` except the settings, order and activity), links that lead outside the folder, or files over 50 MB.
* Paths and contents from other computers are checked (no `..`, contents must match their hash) before anything is
  written, and a file is only changed if it is still exactly what the merge was worked out from.
* What sync remembers (the device id, what it agreed on, the contents a merge needs) is kept in the app's local data
  folder (`sync/`), never in the tasks folder or the cloud.
