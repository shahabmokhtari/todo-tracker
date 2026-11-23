# Tasks: a tree that stays readable with hundreds of tasks

## Why

A real board: 12 groups, 34 top-level tasks, 225 subtasks. In **Tasks** under *All groups* it reads as 34 rows with
no group in sight (seven of them are called "Go to market"). Collapsed, a row says nothing about what's inside;
*Expand all* is a wall of 260 lines; long titles are cut off; and there is no way to look at one branch on its own
or at only what can be done now.

The tasks and their files don't change. Everything here is how the tree is **shown**, and what's remembered is
per device (which rows are open, what's shown, where you're zoomed). Nothing is written to the tasks folder.

## Goals

- Under *All groups* the tree's top level is the groups.
- A collapsed row tells you how big it is and how far along.
- One branch can fill the view, with a path back.
- The tree can be narrowed to what can be done now, or what's waiting.
- Titles are readable: wrapped, and without repeating the parent's name.
- The same rules in the web app, Mac/iPhone/iPad and Android, proven by one shared fixture. The Windows sidebar
  has no tree (its app window is the web app), so it gets this through the web app.

Not in this change: selecting several rows at once, and grouping *Do now* on Today by parent.

## Approaches considered

1. **A separate "Projects" view** (cards per top-level task, drill in). Another place to look, and it duplicates
   Workstreams on Today. Rejected.
2. **Columns** (Finder-style: groups | tasks | subtasks). Good for three fixed levels, poor on a phone and for
   "any depth". Rejected.
3. **Make the existing tree carry the hierarchy** (chosen): group headers, progress on parents, zoom, a *Show*
   choice, open-to-level. One view to learn, the keys and drag-and-drop people already use keep working, and a
   phone gets drill-in for free through zoom.

## The design

```
Tasks                                   Show: [To do ▾]   Open to: Groups · Tasks · All      + New task
All groups › Bazar360 › Go to market                                   (the path; only while zoomed)

▾ ● Bazar360                                                              39 to do   ▁▁▁▁▁▁  2/41
    ▸ ○ Go to market                                                               ▁▁▁▁▁▁  0/20  ⤢
    ▾ ○ Arz360 mobile (iOS + Android)                                              ▃▁▁▁▁▁  2/17  ⤢
        ○ Confirm the bundle id with the store record, then upload the first
          build to TestFlight
        ○ …
▸ ● Deck Huddle                                                           32 to do   ▁▁▁▁▁▁  0/32
```

### 1. Groups are the top level

With no group picked, the tree has one **group row** per group, in the board's group order, and that group's
top-level tasks under it. A group row shows the group's color and name, how many tasks are left to do, and its
progress. It opens and closes like any row and stays at the top of the list while its tasks scroll under it.
Dropping a task on a group row moves it to that group as a top-level task (what dropping on the group in the side
bar does today). A group row has no check box, isn't renamed here and can't be dragged.

Groups with nothing to show are left out while a search or a *Show* choice other than *To do* / *Everything* is
on; otherwise an empty group still shows (so there is somewhere to drop into).

With one group picked, there are no group rows: the tree starts at that group's tasks, as today.

### 2. Parents show their size and progress

Every task with subtasks shows `done/total` and a thin bar. Both count **leaves** under the task (a subtask with
its own subtasks isn't counted itself), not the task. This is what the server and Workstreams already do; the
Mac/iPhone and Android trees counted every descendant and change to match. A group's numbers are the sum over its
top-level tasks, where a task without subtasks counts as one.

### 3. Zoom into a branch

Any task with subtasks can be **zoomed**: the tree then shows only what's under it, starting at its subtasks.
Above the tree is the **path**: *All groups › group › each parent › the task*. Each part goes there (a group part
picks that group; a task part zooms to it); the last part is the zoomed task, with its progress, and opens its
details. Zoom in with ⤢, a row's *Zoom in*, or *Alt+Enter* (*Option-Return* on a Mac or an iPad with a keyboard)
on the focused row.

- **Going back out is one level at a time:** *Escape* (when no row is being edited) on the web and the Mac, the Back
  button on an iPhone and system Back on Android go to the zoomed task's parent (the task part of the path before it,
  `Outline.zoomUp`), and from the first level out to the tree. The path's *All groups* and group parts still go all the
  way out.
- The web address is `#/tasks/<id>`, so a zoom can be linked; the browser's Back follows the address history.
- While zoomed, *New task* adds inside the zoomed task, and a row at the top level of the zoom can't be moved out
  one level (it would vanish from the view); drag or *Move…* still can.
- **Show narrows a zoom like a search does:** the zoom stays (and so do the path and, on the web, the address); the
  rows under it are what the choice keeps, and an empty branch says what's left out ("Nothing here is waiting. Show ›
  To do shows the rest."). A search that leaves the zoomed task out shows its nearest parent the search keeps (or the
  top) for now. What's remembered changes only when the task is really gone (`Outline.rememberedZoom`): deleted,
  archived or moved out of the picked group (the top), or without subtasks (its parent).
- "Show in tree" (from a task's details) leaves any zoom that doesn't contain the task.
- On a phone this is how a deep tree is read: tap ⤢ to go in, the path to come back.

### 4. Show: To do · Can do now · Waiting · Everything

One choice replaces the *Show finished* box:

| Choice | Keeps |
| --- | --- |
| **To do** (default) | every task that isn't finished |
| **Can do now** | tasks that can be done now (`actionable`), with their parents |
| **Waiting** | tasks that are waiting or come after an unfinished step (`waiting`, `locked`), with their parents |
| **Everything** | finished tasks too |

A parent kept only for its subtasks stays (it's the context). A choice narrows the tree; it doesn't open or close
rows. Someone who had *Show finished* on starts at *Everything*. The search box keeps working with any choice, and
while there's search text, the rows on the way to a match are shown open (without changing what's remembered).

### 5. Open to a level

*Expand all* and *Collapse all* become **Open to: Groups · Tasks · Subtasks · All**: one press opens the tree
exactly that deep (Groups: only group rows; Tasks: top-level tasks showing, closed; Subtasks: one level of
subtasks; All: everything). *Groups* is offered only under *All groups*. It's a button that sets what's open, not a
mode: opening and closing single rows afterwards works as always and is remembered.

### 6. Titles you can read

- A title wraps to two lines instead of being cut off. (On the web the title is text until you rename it: a
  selected row's title, Enter or F2 turns it into the box, as on the Mac and Android. Clicking a title selects the
  row; clicking the selected row's title renames it.)
- **The parent's name isn't repeated.** When a subtask's title starts with its parent's title followed by `:`,
  ` -`, ` –` or ` —`, the tree shows only the rest ("Publisher accounts: Pick the contact phone number" under
  *Publisher accounts* reads "Pick the contact phone number"). The comparison ignores case; the rest must not be
  empty; leading spaces are dropped. Only the tree does this, only against the direct parent, and only when that
  parent is the row's parent on screen or the zoomed task. Renaming shows and saves the whole title. Nothing is
  changed in the task.
- Thin guide lines mark each level of indent.

## The rules, shared (`tests/fixtures/outline.json`)

The logic lives in one place per language and is checked against one fixture, generated by the C# core:

| | |
| --- | --- |
| C# | `src/OneThing.Core/Outline.cs` (new; also behind `ot tree`) |
| Web | `wwwroot/js/outlinemodel.js` |
| Swift | `OneThingKit/Sources/OneThingKit/Outline.swift` (the pure part moves out of `TasksOutlineView.swift`) |
| Kotlin | `dev/onething/core/Outline.kt` |

`Outline.rows(board, options, now)` returns the rows to draw, in order. Options: `groupId?`, `zoomId?`, `show`
(`todo|now|waiting|all`), `query`, `expanded` (task ids), `closedGroups` (group ids). Each row: `kind`
(`group|task`), `id`, `depth`, `title` (as shown), `fullTitle`, `hasChildren`, `expanded`, `done`, `total`
(leaves; 0 for a task without subtasks), `left` (group rows: unfinished leaves), `state`. Plus:

- `Outline.path(board, options)`: the parts of the path while zoomed (`kind`, `id`, `title`).
- `Outline.openTo(board, options, level)`: the `expanded` and `closedGroups` sets for a level.
- `Outline.resolveZoom(board, options)`: the zoom drawn; `Outline.rememberedZoom(board, options)`: the zoom kept
  (changes only when the task is really gone); `Outline.zoomUp(board, options)`: where Back and Escape go.
- `Outline.shownTitle(title, parentTitle)`.

The fixture has a board and cases (options in, rows / path / sets out) covering: group rows and their order, an
empty group with and without a filter, one group picked, each *Show* choice (a parent kept for a subtask; a
sequential parent's later steps under *Waiting*), search with rows opened on the way to a match, leaf counts at
three depths, open-to each level, zoom (rows, path, new-task place), zoom fallbacks (for now and for good), the
zoom one level up, and title trimming (each separator, case, an empty rest, a grandparent's name left alone, and one
comparison everywhere: C#'s OrdinalIgnoreCase, without normalization or locale). A `moves` section pins indent,
outdent, up/down, drops and New task (options and a move in, the tree afterwards or "refused" out): the C# side
writes the server changes each comes to, and the web, Swift and Kotlin plan the move with their own code.

The server's `/api/tree` reply doesn't change. The web app builds group rows from the groups it already has and
each node's `groupId`, and reads `state`, `doneCount` and `totalCount` from the nodes.

## Remembered on each device

| | Web (`localStorage`) | Apple (`@AppStorage`) | Android (`SharedPreferences`) |
| --- | --- | --- | --- |
| open rows (kept) | `onething.outline.expanded` | same key | `outline.expanded` |
| closed groups | `onething.outline.closedGroups` | same key | `outline.closedGroups` |
| show | `onething.outline.show` | same key | `outline.show` |
| zoom | the address (`#/tasks/<id>`) | `onething.outline.zoom` | `outline.zoom` |

The old `showDone` value is read once to pick the first *Show* choice and left in place.

## Agents

`ot tree` prints the same tree an agent can read in one call: `ot tree [--group <name>] [--under <task>]
[--show todo|now|waiting|all] [--depth <n>] [--json]`, with `done/total` on parents. (The MCP side is covered by
the tools that already return a task with its subtasks; if no tool returns the whole tree, `get_tree` joins them
with the same options.)

## Look

Nothing new in the palette: group rows use the group's color dot and the page's secondary text weight; the bar is
the Workstreams bar at 3 px, in the row's accent; guide lines are the border color at half strength. Rows keep
their height for one-line titles (no jump for boards that fit today). Motion: none added.
