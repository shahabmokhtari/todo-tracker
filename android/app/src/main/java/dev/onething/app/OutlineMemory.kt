package dev.onething.app

import dev.onething.core.Outline
import dev.onething.core.OutlineLevel
import dev.onething.core.OutlineOptions
import dev.onething.core.OutlineShow
import dev.onething.core.TaskBoard
import java.time.Instant
import java.util.UUID

/** Where the Tasks tree's memory is kept: SharedPreferences in the app, a map in tests. */
interface Remembered {
    fun text(key: String): String?

    /** A true/false value, or null when there's none. */
    fun flag(key: String): Boolean?

    /** Keeps [value] under [key] (null: forgets it). */
    fun put(key: String, value: String?)
}

/**
 * What the Tasks tree remembers on this phone (docs/design/tasks-tree.md, "Remembered on each device"): the open tasks,
 * the closed groups, the Show choice and the zoom. Never anything in the tasks. [showForNow] is a Show choice made for
 * the moment ("Show in tree" on a finished task shows Everything) and isn't kept.
 */
data class OutlineMemory(
    val expanded: Set<UUID> = emptySet(),
    val closedGroups: Set<UUID> = emptySet(),
    val show: OutlineShow = OutlineShow.TODO,
    val zoomId: UUID? = null,
    val showForNow: OutlineShow? = null,
) {
    /** What the tree shows now. */
    val shown: OutlineShow get() = showForNow ?: show

    fun options(groupId: UUID?, query: String): OutlineOptions = OutlineOptions(groupId, zoomId, shown, query, expanded, closedGroups)

    /** A task opened or closed. */
    fun withOpen(id: UUID, open: Boolean): OutlineMemory = withExpanded(if (open) expanded - id + id else expanded - id)

    /** Exactly these tasks open (kept short: the newest few hundred, like the other apps). */
    fun withExpanded(ids: Set<UUID>): OutlineMemory = copy(expanded = ids.toList().takeLast(MAX_OPEN).toSet())

    /** A group opened or closed (under All groups). */
    fun withGroupOpen(id: UUID, open: Boolean): OutlineMemory = copy(closedGroups = if (open) closedGroups - id else closedGroups + id)

    /** A Show choice picked: kept, and the one for the moment is gone. */
    fun choosing(show: OutlineShow): OutlineMemory = copy(show = show, showForNow = null)

    /** Into a task's branch (null: out to the whole tree). */
    fun zoomedTo(id: UUID?): OutlineMemory = copy(zoomId = id)

    /** Out of the zoom to the whole tree, the task that was zoomed into in sight: its parents and its group open. */
    fun leavingZoom(board: TaskBoard): OutlineMemory {
        val zoom = zoomId?.let { board.find(it) } ?: return copy(zoomId = null)
        return copy(zoomId = null, expanded = (expanded + zoom.ancestors.map { it.id }).toList().takeLast(MAX_OPEN).toSet(), closedGroups = closedGroups - zoom.groupId)
    }

    /** "Open to" a level: only what's in view changes (under the zoom, the group picked, or every group). */
    fun openedTo(board: TaskBoard, groupId: UUID?, query: String, level: OutlineLevel, now: Instant): OutlineMemory {
        val open = Outline.openTo(board, options(groupId, query), level, now)
        return withExpanded(open.expanded).copy(closedGroups = open.closedGroups)
    }

    /**
     * "Show in tree" from a task's details: its parents and its group open, a zoom that doesn't hold it left, and a Show
     * choice that hides it set aside for now (finished: Everything; else To do). Also says which group to pick (the
     * task's, when another one is picked; else the one picked) and whether the search must go (it leaves the task out).
     */
    fun showingInTree(board: TaskBoard, id: UUID, groupId: UUID?, query: String): TreeReveal? {
        val item = board.find(id)?.takeIf { !it.isArchived } ?: return null
        val parents = item.ancestors.map { it.id }
        val zoom = zoomId?.takeIf { it in parents }
        val show = when {
            item.isDone -> OutlineShow.ALL
            shown == OutlineShow.NOW || shown == OutlineShow.WAITING -> OutlineShow.TODO
            else -> shown
        }
        val memory = withExpanded(expanded + parents).copy(
            closedGroups = closedGroups - item.groupId,
            zoomId = zoom,
            showForNow = if (show == this.show) null else show,
        )
        val group = if (groupId != null && groupId != item.groupId) item.groupId else groupId
        val clearSearch = query.isNotBlank() && id !in Outline.searchKeeps(board, query)
        return TreeReveal(memory, group, clearSearch)
    }

    /** Without tasks and groups that are no longer on [board] (the open rows keep their order: newest last). */
    fun keepingOnly(board: TaskBoard): OutlineMemory {
        val groups = board.groups.map { it.id }.toSet()
        return copy(expanded = expanded.filterTo(LinkedHashSet()) { board.find(it) != null }, closedGroups = closedGroups.filterTo(LinkedHashSet()) { it in groups })
    }

    /** Keeps what changed since [before] (all of it when null). The Show choice for the moment isn't kept. */
    fun write(store: Remembered, before: OutlineMemory? = null) {
        if (before == null || before.expanded != expanded) store.put(EXPANDED, expanded.joinToString(","))
        if (before == null || before.closedGroups != closedGroups) store.put(CLOSED_GROUPS, closedGroups.joinToString(","))
        if (before == null || before.show != show) store.put(SHOW, show.wire)
        if (before == null || before.zoomId != zoomId) store.put(ZOOM, zoomId?.toString())
    }

    companion object {
        const val EXPANDED = "outline.expanded"
        const val CLOSED_GROUPS = "outline.closedGroups"
        const val SHOW = "outline.show"
        const val ZOOM = "outline.zoom"

        /** The old "Show finished" box: read once to pick the first Show choice, and left in place. */
        const val OLD_SHOW_DONE = "outline.showDone"

        private const val MAX_OPEN = 500

        /** What this phone remembers (someone who had "Show finished" on starts at Everything). */
        fun read(store: Remembered): OutlineMemory = OutlineMemory(
            expanded = ids(store.text(EXPANDED)),
            closedGroups = ids(store.text(CLOSED_GROUPS)),
            show = OutlineShow.entries.firstOrNull { it.wire == store.text(SHOW) }
                ?: if (store.flag(OLD_SHOW_DONE) == true) OutlineShow.ALL else OutlineShow.TODO,
            zoomId = ids(store.text(ZOOM)).firstOrNull(),
        )

        private fun ids(text: String?): Set<UUID> =
            text.orEmpty().split(',').mapNotNull { runCatching { UUID.fromString(it.trim()) }.getOrNull() }.toCollection(LinkedHashSet())
    }
}

/** What "Show in tree" changes: the tree's memory, the group to pick, and whether the search is cleared. */
data class TreeReveal(val memory: OutlineMemory, val groupId: UUID?, val clearSearch: Boolean)
