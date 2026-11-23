package dev.onething.core

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID

// What the Board, Tasks and Done views show, and what a move on them turns into: the same as the iPhone's
// (BoardModel+Views.swift) and the web's (board.js, outline.js, done.js). The moves themselves are BoardMoves.kt.

/** A move worked out from the board: its changes, and what to say once it's made. */
data class PlannedMove(val moves: List<BoardMove>, val message: String?)

/** What the views show for the group on screen ([groupId]; null: every group) and the search ([query]). */
class BoardScope(val board: TaskBoard, val groupId: UUID?, val query: String = "") {
    private val search = TaskSearch.parse(query)

    // The top-level tasks the search finds (itself or one of its subtasks matches); null when not searching.
    private val found: Set<UUID>? by lazy { if (search.isEmpty) null else search.apply(board).map { it.root.id }.toSet() }

    /** Top-level tasks of the group (all groups when none), in tree order; archived ones are put away (Done › Archive). */
    val visibleRoots: List<WorkItem> get() = board.items.filter { it.archivedAt == null && (groupId == null || it.groupId == groupId) }

    /** Top-level tasks the search finds, or all of them when not searching. */
    val matchingRoots: List<WorkItem> get() = found?.let { f -> visibleRoots.filter { it.id in f } } ?: visibleRoots

    /** The Board's columns. */
    fun columns(now: Instant): Map<BoardColumn, List<WorkItem>> = BoardColumns.of(matchingRoots, now)

    /** Finished top-level tasks (not archived), newest first. */
    val finishedRoots: List<WorkItem> get() = matchingRoots.filter { it.isDone }.sortedByDescending { it.completedAt }

    /** Archived top-level tasks, newest first. */
    val archivedRoots: List<WorkItem>
        get() = board.items.filter { it.archivedAt != null && (groupId == null || it.groupId == groupId) && (found?.contains(it.id) ?: true) }
            .sortedByDescending { it.archivedAt }

    /**
     * A card to a column (Done finishes it; out of Done opens it again), before [before] (a card there; null: its place
     * in the shared order, or last within its own column). Only top-level tasks are cards (refused with why).
     */
    fun moveCard(id: UUID, column: BoardColumn, before: UUID? = null, now: Instant): PlannedMove? {
        val card = board.find(id) ?: return null
        if (card.parent != null) throw BoardException("Only top-level tasks are cards; move its task instead.")
        val columns = columns(now)
        val from = BoardColumns.column(card)
        var message: String? = if (column == BoardColumn.DONE) "Done 🎉" else if (from == column) null else "Moved to ${column.displayName}"
        if (column == BoardColumn.DOING && from != BoardColumn.DOING && columns[BoardColumn.DOING].orEmpty().size + 1 > BoardColumns.WIP_LIMIT) {
            message = BoardColumns.OVER_LIMIT_MESSAGE
        }
        return PlannedMove(BoardColumns.drop(card, column, before, columns), message)
    }

    /** A card one column left or right, or one up or down within its column (the Move menu, the keyboard). */
    fun moveCard(id: UUID, direction: BoardColumns.Direction, now: Instant): PlannedMove? {
        val card = board.find(id)?.takeIf { it.parent == null } ?: return null
        val columns = columns(now)
        val (column, before) = BoardColumns.keyboardMove(card, direction, columns) ?: return null
        return PlannedMove(BoardColumns.drop(card, column, before, columns), if (column == BoardColumn.DONE) "Done 🎉" else null)
    }

    /** Under the task above it on screen. */
    fun indent(id: UUID, showDone: Boolean = true): PlannedMove? =
        OutlineMoves.indent(board, id, showDone, matchingRoots)?.let { PlannedMove(it, null) }

    /**
     * Under the task above it in the Tasks tree: [shown] says which tasks are on screen ([Outline.keeps]); at the top level
     * only its own group's tasks are next to it.
     */
    fun indent(id: UUID, shown: (WorkItem) -> Boolean): PlannedMove? =
        OutlineMoves.indent(board, id, shown, besides(id))?.let { PlannedMove(it, null) }

    /** Up (-1) or down (1) past the task above or below in the Tasks tree ([shown]: as for [indent]). */
    fun step(id: UUID, delta: Int, shown: (WorkItem) -> Boolean): PlannedMove? =
        OutlineMoves.step(board, id, delta, shown, besides(id))?.let { PlannedMove(it, null) }

    // The top-level tasks next to a top-level task in the Tasks tree: its group's (each group has its own rows).
    private fun besides(id: UUID): List<WorkItem>? {
        val item = board.find(id)?.takeIf { it.parent == null } ?: return null
        return board.items.filter { it.archivedAt == null && it.groupId == item.groupId }
    }

    /** A task dropped on a group's row in the Tasks tree: to the top of that group, at its top level. */
    fun dropOnGroup(id: UUID, groupId: UUID): PlannedMove? {
        val group = board.groups.firstOrNull { it.id == groupId } ?: return null
        val item = board.find(id) ?: return null
        if (item.parent == null && item.groupId == groupId && board.items.firstOrNull { it.groupId == groupId && it.archivedAt == null } === item) return null
        return PlannedMove(listOf(BoardMove.Move(id, null, groupId = groupId, index = 0)), "Moved to ${group.name}")
    }

    /** Out one level, right after its parent. */
    fun outdent(id: UUID): PlannedMove? = OutlineMoves.outdent(board, id)?.let { PlannedMove(it, null) }

    /** Up (-1) or down (1) past the task above or below on screen. */
    fun step(id: UUID, delta: Int, showDone: Boolean = true): PlannedMove? =
        OutlineMoves.step(board, id, delta, showDone, matchingRoots)?.let { PlannedMove(it, null) }

    /** A task dropped before, after or into another (null: it can't go there, into itself). */
    fun drop(dragged: UUID, target: UUID, zone: OutlineMoves.Zone): PlannedMove? =
        OutlineMoves.drop(board, dragged, target, zone)?.let { PlannedMove(it, "Moved") }

    /** A task to another group (a subtask becomes a task of its own there, last). */
    fun toGroup(id: UUID, groupId: UUID): PlannedMove? {
        val name = board.groups.firstOrNull { it.id == groupId }?.name ?: "the group"
        return OutlineMoves.toGroup(board, id, groupId)?.let { PlannedMove(it, "Moved to $name") }
    }
}

/** Makes the changes of a planned move, in order. */
fun TaskBoard.applyPlanned(planned: PlannedMove, actor: Actor = Actor.USER, now: Instant) {
    for (move in planned.moves) applyMove(move, actor, now)
}

/**
 * Adds a task where it was typed: under [parentId] (first, or right after [after]), or at the top level of [groupId]
 * (in [stage], right after [after] when given; else last, or first of its group when [first]). [id] is chosen up front
 * (the same change made twice makes the same task).
 */
fun TaskBoard.addInPlace(title: String, parentId: UUID? = null, after: UUID? = null, stage: Stage? = null, groupId: UUID? = null, id: UUID, actor: Actor = Actor.USER, now: Instant, first: Boolean = false): WorkItem {
    val group = if (parentId == null) (after?.let { find(it)?.groupId } ?: groupId) else null
    val firstId = if (parentId != null) find(parentId)?.children?.firstOrNull()?.id
    else if (first && after == null) items.firstOrNull { it.archivedAt == null && it.groupId == (group ?: defaultGroupId) }?.id else null
    // Only a sibling can be the task it goes after.
    val sibling = after?.takeIf { find(it)?.parent?.id == parentId }
    val item = addTask(NewTask(title.trim(), parentId = parentId, groupId = group, stage = stage, id = id), actor, now)
    if (sibling != null) reorderAfter(item.id, sibling, actor, now) else if (firstId != null) reorder(item.id, firstId, actor, now)
    return item
}

/** What happened to a task and its subtasks (or the whole board), newest first, as the other apps' Timeline shows it. */
fun TaskBoard.timeline(id: UUID? = null): List<ActivityEntry> {
    val scope = id?.let { get(it).selfAndDescendants.map { i -> i.id }.toSet() }
    return activity.withIndex()
        .filter { scope == null || it.value.itemId in scope }
        .sortedWith(compareByDescending<IndexedValue<ActivityEntry>> { it.value.at }.thenByDescending { it.index })
        .map { it.value }
}

/** How a fact on a card looks. */
enum class ChipTone { DANGER, WARN, MUTED, INFO, STEP }

data class Chip(val text: String, val tone: ChipTone)

/** What a card says about its task, most urgent first: overdue or due, waiting, progress, time (boardmodel.js cardFacts). */
object CardFacts {
    fun chips(item: WorkItem, now: Instant): List<Chip> {
        val chips = mutableListOf<Chip>()
        val due = item.deadline
        if (due != null && !item.isDone) {
            val whenText = RelativeTime.format(due, now)
            chips.add(
                if (due.isBefore(now)) Chip("Overdue · $whenText", ChipTone.DANGER)
                else Chip("Due $whenText", if (Duration.between(now, due) < Duration.ofDays(2)) ChipTone.WARN else ChipTone.MUTED),
            )
        }
        val wake = item.nextActionAt
        if (!item.isDone && wake != null && wake.isAfter(now)) chips.add(Chip("Waiting", ChipTone.INFO))
        val leaves = item.selfAndDescendants.drop(1)
        if (leaves.isNotEmpty()) {
            val done = leaves.count { it.isDone }
            chips.add(Chip("$done/${leaves.size}", if (done == leaves.size) ChipTone.STEP else ChipTone.MUTED))
        }
        val spent = item.timeSpent(now)
        val timing = item.selfAndDescendants.any { i -> i.timeEntries.any { it.isRunning } }
        if (spent >= Duration.ofMinutes(1) || timing) chips.add(Chip("⏱ ${durationWords(spent)}", if (timing) ChipTone.STEP else ChipTone.MUTED))
        return chips
    }
}

/** "45 s", "25 min", "1 h", "2 h 05 min" (Presentation.duration in the other apps). */
fun durationWords(d: Duration): String {
    val s = Math.round(d.toMillis().coerceAtLeast(0) / 1000.0)
    if (s < 60) return "$s s"
    val minutes = Math.round(s / 60.0)
    if (minutes < 60) return "$minutes min"
    val h = minutes / 60
    val m = minutes % 60
    return if (m == 0L) "$h h" else "$h h ${m.toString().padStart(2, '0')} min"
}

/** Compact relative time: "in 5m", "in 1h 30m", "3d ago", or "Feb 20" (the same rules as C#, JS and Swift). */
object RelativeTime {
    private val months = listOf("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")

    fun format(target: Instant, now: Instant): String {
        val delta = Duration.between(now, target).toMillis() / 1000.0
        val minutes = Math.round(Math.abs(delta) / 60).toInt()
        if (minutes < 1) return "now"
        val text = when {
            minutes < 60 -> "${minutes}m"
            minutes < 180 -> if (minutes % 60 == 0) "${minutes / 60}h" else "${minutes / 60}h ${minutes % 60}m"
            minutes < 24 * 60 -> "${minutes / 60}h"
            minutes < 7 * 24 * 60 -> "${minutes / (24 * 60)}d"
            else -> {
                val d = target.atZone(ZoneId.of("UTC"))
                return "${months[d.monthValue - 1]} ${d.dayOfMonth}"
            }
        }
        return if (delta > 0) "in $text" else "$text ago"
    }
}

/** One day on Done: [day] keys it, [label] is what it says (Today, Yesterday, or the date). */
data class DoneDay(val day: LocalDate, val label: String, val items: List<WorkItem>)

/** Finished tasks by the day they were finished (Done), or archived ones by the day they were put away (Archive). */
object DoneDays {
    /** When [item] counts for its day: finished (Done), or archived ([archived]: the Archive tab). */
    fun at(item: WorkItem, archived: Boolean): Instant? =
        if (archived) item.archivedAt ?: item.completedAt else item.completedAt ?: item.archivedAt

    /**
     * Newest day first, each day once, the items in the order given. [dateLabel] names a day before yesterday; its
     * second argument says to add the year (a day from another year).
     */
    fun byDay(
        items: List<WorkItem>,
        now: Instant,
        zone: ZoneId,
        archived: Boolean = false,
        dateLabel: (LocalDate, Boolean) -> String,
    ): List<DoneDay> {
        val today = now.atZone(zone).toLocalDate()
        val days = linkedMapOf<LocalDate, MutableList<WorkItem>>()
        for (item in items) {
            val day = (at(item, archived) ?: now).atZone(zone).toLocalDate()
            days.getOrPut(day) { mutableListOf() }.add(item)
        }
        return days.entries.sortedByDescending { it.key }.map { (day, list) ->
            val label = when (day) {
                today -> "Today"
                today.minusDays(1) -> "Yesterday"
                else -> dateLabel(day, day.year != today.year)
            }
            DoneDay(day, label, list)
        }
    }
}
