package dev.onething.core

import java.time.Instant
import java.util.UUID

// The Tasks tree as rows: the same rules as the C# core (Outline.cs), the web app (outlinemodel.js) and Swift, checked
// against tests/fixtures/outline.json. Only how the tree is shown; nothing in the tasks changes.

/** What the Tasks tree shows: unfinished tasks, what can be done now, what's waiting, or everything. */
enum class OutlineShow(val wire: String, val label: String) {
    /** Every task that isn't finished (the default). */
    TODO("todo", "To do"),

    /** Tasks that can be done now, with their parents. */
    NOW("now", "Can do now"),

    /** Tasks that wait (snoozed, waiting for another task) or come after an unfinished step, with their parents. */
    WAITING("waiting", "Waiting"),

    /** Finished tasks too. */
    ALL("all", "Everything"),
}

/** How deep "Open to" opens the tree. */
enum class OutlineLevel(val label: String) {
    /** Only the group rows (under All groups). */
    GROUPS("Groups"),

    /** The top-level tasks, closed. */
    TASKS("Tasks"),

    /** One level of subtasks. */
    SUBTASKS("Subtasks"),

    /** Everything. */
    ALL("All"),
}

enum class OutlineRowKind { GROUP, TASK }

enum class OutlinePartKind {
    /** "All groups": the way out of the zoom. */
    ALL,
    GROUP,
    TASK,
}

/**
 * How the Tasks tree is looked at (per device, never saved in the tasks): the group picked (null: all groups), the task
 * zoomed into, what's shown, the search text, the tasks that are open and the groups that are closed.
 */
data class OutlineOptions(
    val groupId: UUID? = null,
    val zoomId: UUID? = null,
    val show: OutlineShow = OutlineShow.TODO,
    val query: String = "",
    val expanded: Set<UUID> = emptySet(),
    val closedGroups: Set<UUID> = emptySet(),
) {
    /** There's search text: the tree is narrowed to it and shown open on the way to what matched. */
    val searching: Boolean get() = query.isNotBlank()
}

/**
 * A row of the Tasks tree. [title] is what's shown (without the parent's name it repeats); [done]/[total] count the
 * leaves under it (0/0 for a task without subtasks; a group adds up its top-level tasks, one each for a task without
 * subtasks); [left] is `total - done`. [canOutdent]: it can move out one level here (not a top-level task, nor one at the
 * top of a zoom). [item] is the task (task rows), [group] the group (group rows).
 */
data class OutlineRow(
    val kind: OutlineRowKind,
    val id: UUID,
    val depth: Int,
    val title: String,
    val fullTitle: String,
    val hasChildren: Boolean,
    val expanded: Boolean,
    val done: Int,
    val total: Int,
    val left: Int,
    val state: ItemState?,
    val canOutdent: Boolean,
    val item: WorkItem? = null,
    val group: TaskGroup? = null,
) {
    val isGroup: Boolean get() = kind == OutlineRowKind.GROUP
}

/** A part of the path above a zoomed tree: All groups › group › each parent › the zoomed task. */
data class OutlinePathPart(val kind: OutlinePartKind, val id: UUID?, val title: String, val done: Int = 0, val total: Int = 0)

/** What "Open to" leaves open: the tasks open and the groups closed. */
data class OutlineOpen(val expanded: Set<UUID>, val closedGroups: Set<UUID>)

/** Where New task adds: inside [parentId] (the zoom), else at the top of [groupId]. */
data class NewTaskPlace(val parentId: UUID?, val groupId: UUID)

object Outline {
    /** The first part of the path. */
    const val ALL_GROUPS_TITLE = "All groups"

    private val separators = listOf(":", " -", " –", " —")

    /** The rows to draw, in order. */
    fun rows(board: TaskBoard, options: OutlineOptions, now: Instant): List<OutlineRow> {
        val shown = Shown(board, options, now)
        val rows = mutableListOf<OutlineRow>()
        val zoom = resolveZoom(shown)

        fun walk(items: List<WorkItem>, depth: Int, parentShown: String?) {
            for (item in items) {
                if (!shown.kept(item)) continue
                val children = item.children.filter { shown.kept(it) }
                val open = children.isNotEmpty() && (options.searching || item.id in options.expanded)
                val (done, total) = leaves(item)
                val parent = item.parent
                val title = shownTitle(item.title, parent?.title, parentShown)
                rows.add(
                    OutlineRow(
                        OutlineRowKind.TASK, item.id, depth, title, item.title, children.isNotEmpty(), open, done, total, total - done,
                        Agenda.stateOf(item, now), parent != null && parent !== zoom, item = item,
                    ),
                )
                if (open) walk(children, depth + 1, title)
            }
        }

        if (zoom != null) {
            walk(zoom.children, 0, chain(zoom).last().second)
            return rows
        }
        val roots = shown.roots()
        val picked = options.groupId
        if (picked != null) {
            walk(roots.filter { it.groupId == picked }, 0, null)
            return rows
        }

        // Empty groups stay (somewhere to drop into) unless a search or a narrowing choice is on.
        val hideEmpty = options.searching || options.show == OutlineShow.NOW || options.show == OutlineShow.WAITING
        for (group in board.groups) {
            val tasks = roots.filter { it.groupId == group.id }
            val hasChildren = tasks.any { shown.kept(it) }
            if (!hasChildren && hideEmpty) continue
            val (done, total) = groupLeaves(tasks)
            val open = options.searching || group.id !in options.closedGroups
            rows.add(OutlineRow(OutlineRowKind.GROUP, group.id, 0, group.name, group.name, hasChildren, open, done, total, total - done, null, false, group = group))
            if (open) walk(tasks, 1, null)
        }
        return rows
    }

    /** The path above the tree while zoomed (empty when not). */
    fun path(board: TaskBoard, options: OutlineOptions, now: Instant): List<OutlinePathPart> {
        val zoom = resolveZoom(Shown(board, options, now)) ?: return emptyList()
        val group = board.group(zoom.groupId)
        val parts = mutableListOf(OutlinePathPart(OutlinePartKind.ALL, null, ALL_GROUPS_TITLE), OutlinePathPart(OutlinePartKind.GROUP, group.id, group.name))
        for ((item, title) in chain(zoom)) {
            val (done, total) = leaves(item)
            parts.add(OutlinePathPart(OutlinePartKind.TASK, item.id, title, done, total))
        }
        return parts
    }

    /**
     * The zoom to use: the asked task when it's shown and has subtasks; else its nearest parent that is; else null (the
     * top). A task that's gone (deleted, archived, in another group than the one picked) has none.
     */
    fun resolveZoom(board: TaskBoard, options: OutlineOptions, now: Instant): UUID? = resolveZoom(Shown(board, options, now))?.id

    /**
     * "Open to" a level: which tasks are open and which groups closed afterwards. It changes only what's in view (under
     * the zoom, the group picked, or every group); tasks elsewhere stay as they were. Groups closes every group (under
     * All groups; elsewhere it's the same as Tasks); the other levels open every group.
     */
    fun openTo(board: TaskBoard, options: OutlineOptions, level: OutlineLevel, now: Instant): OutlineOpen {
        val shown = Shown(board, options, now)
        val zoom = resolveZoom(shown)
        val top = zoom?.children?.filter { shown.inQuery(it) }
            ?: shown.roots().filter { options.groupId == null || it.groupId == options.groupId }
        val expanded = options.expanded.toMutableSet()
        fun each(items: List<WorkItem>, depth: Int) {
            for (item in items) {
                val children = item.children.filter { shown.inQuery(it) }
                val open = children.isNotEmpty() && when (level) {
                    OutlineLevel.ALL -> true
                    OutlineLevel.SUBTASKS -> depth == 0
                    else -> false
                }
                if (open) expanded.add(item.id) else expanded.remove(item.id)
                each(children, depth + 1)
            }
        }
        each(top, 0)
        val closed = if (zoom != null || options.groupId != null) options.closedGroups
        else if (level == OutlineLevel.GROUPS) board.groups.map { it.id }.toSet() else emptySet()
        return OutlineOpen(expanded, closed)
    }

    /** Where New task adds: inside the zoomed task, else at the top of the group picked (all groups: the first group). */
    fun newTaskPlace(board: TaskBoard, options: OutlineOptions, now: Instant): NewTaskPlace {
        val zoom = resolveZoom(Shown(board, options, now))
        return NewTaskPlace(zoom?.id, zoom?.groupId ?: options.groupId ?: board.defaultGroupId)
    }

    /** Whether a task is on screen with these options (the search and the Show choice keep it), for moves among siblings. */
    fun keeps(board: TaskBoard, options: OutlineOptions, now: Instant): (WorkItem) -> Boolean {
        val shown = Shown(board, options, now)
        return { shown.kept(it) }
    }

    /** What a search keeps (as /api/tree?q= does): the matches, their parents (for context) and their subtasks. */
    fun searchKeeps(board: TaskBoard, query: String): Set<UUID> {
        val keep = mutableSetOf<UUID>()
        for (match in TaskSearch.parse(query).apply(board)) {
            match.selfAndDescendants.forEach { keep.add(it.id) }
            match.ancestors.forEach { keep.add(it.id) }
        }
        return keep
    }

    /**
     * A subtask's title without its parent's name in front: when it starts with the parent's title (any case) followed
     * by `:`, ` -`, ` –` or ` —`, the rest (leading spaces dropped), unless that's empty. When the parent's own title is
     * shown shorter ([parentShownTitle]), that name counts too.
     */
    fun shownTitle(title: String, parentTitle: String?, parentShownTitle: String? = null): String {
        val shown = trim(title, parentTitle)
        return if (shown == title && parentShownTitle != parentTitle) trim(title, parentShownTitle) else shown
    }

    private fun trim(title: String, parentTitle: String?): String {
        if (parentTitle.isNullOrEmpty() || !title.startsWith(parentTitle, ignoreCase = true)) return title
        val after = title.substring(parentTitle.length)
        val separator = separators.firstOrNull { after.startsWith(it) } ?: return title
        val rest = after.substring(separator.length).trimStart()
        return rest.ifEmpty { title }
    }

    /** Parses todo, now, waiting or all (any case); null for anything else. */
    fun parseShow(text: String?): OutlineShow? = when (text?.trim()?.lowercase()) {
        "todo", "to do" -> OutlineShow.TODO
        "now" -> OutlineShow.NOW
        "waiting" -> OutlineShow.WAITING
        "all", "everything" -> OutlineShow.ALL
        else -> null
    }

    /** Finished leaves and all leaves under the task (not counting the task itself). */
    fun leaves(item: WorkItem): Pair<Int, Int> {
        val leaves = item.selfAndDescendants.drop(1).filter { it.children.isEmpty() }
        return leaves.count { it.isDone } to leaves.size
    }

    /** The task and its parents, outermost first, each with its title as shown under the one before. */
    private fun chain(item: WorkItem): List<Pair<WorkItem, String>> {
        val chain = mutableListOf<Pair<WorkItem, String>>()
        for (at in item.ancestors.reversed() + item) {
            val before = chain.lastOrNull()
            chain.add(at to (if (before == null) at.title else shownTitle(at.title, before.first.title, before.second)))
        }
        return chain
    }

    private fun groupLeaves(roots: List<WorkItem>): Pair<Int, Int> {
        var done = 0
        var total = 0
        for (root in roots) {
            val (d, t) = leaves(root)
            if (t > 0) {
                done += d
                total += t
            } else {
                if (root.isDone) done++
                total++
            }
        }
        return done to total
    }

    private fun resolveZoom(shown: Shown): WorkItem? {
        val id = shown.options.zoomId ?: return null
        val item = shown.board.find(id)?.takeIf { !it.isArchived } ?: return null
        if (shown.options.groupId != null && item.groupId != shown.options.groupId) return null
        var at: WorkItem? = item
        while (at != null) {
            if (at.children.isNotEmpty() && shown.kept(at)) return at
            at = at.parent
        }
        return null
    }

    /** Which tasks the search and the Show choice keep (each kept task's parents are kept too). */
    private class Shown(val board: TaskBoard, val options: OutlineOptions, private val now: Instant) {
        private val query: Set<UUID>? = if (options.searching) searchKeeps(board, options.query) else null
        private val memo = HashMap<UUID, Boolean>()

        /** Top-level tasks that aren't archived and that the search keeps. */
        fun roots(): List<WorkItem> = board.items.filter { it.archivedAt == null && inQuery(it) }

        fun inQuery(item: WorkItem): Boolean = query == null || item.id in query

        fun kept(item: WorkItem): Boolean = memo[item.id] ?: (!item.isArchived && inQuery(item) && (itself(item) || item.children.any { kept(it) }))
            .also { memo[item.id] = it }

        private fun itself(item: WorkItem): Boolean = when (options.show) {
            OutlineShow.ALL -> true
            OutlineShow.NOW -> Agenda.stateOf(item, now) == ItemState.ACTIONABLE
            OutlineShow.WAITING -> Agenda.stateOf(item, now).let { it == ItemState.WAITING || it == ItemState.LOCKED }
            OutlineShow.TODO -> !item.isDone
        }
    }
}
