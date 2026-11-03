package dev.onething.core

import java.time.Duration
import java.time.Instant
import java.util.UUID

// The Board (columns Inbox, Next, Doing, Done) and the Tasks outline: moving cards and tasks. Ports of OneThing.Core's
// SetStage, Move, Reorder and ReorderAfter, and of the web app's boardmodel.js and outlinemodel.js (what a drop, Tab or
// Shift+Tab turns into). tests/fixtures/board-moves.json holds the cases every app runs.

/** One change a move is made of. */
sealed interface BoardMove {
    data class Complete(val id: UUID) : BoardMove

    /** A card to a column (a finished card opens again). */
    data class SetStage(val id: UUID, val stage: Stage) : BoardMove

    /** Under [parentId] (null: the top level of [groupId], or of its own group), at [index] (null: last). */
    data class Move(val id: UUID, val parentId: UUID?, val groupId: UUID? = null, val index: Int? = null) : BoardMove

    /** Among its siblings, before [before] (null: last). */
    data class Reorder(val id: UUID, val before: UUID?) : BoardMove

    /** Right after a sibling. */
    data class ReorderAfter(val id: UUID, val after: UUID) : BoardMove
}

/** As the board says it ("Moved … to Doing"). */
val Stage.displayName: String get() = wire.replaceFirstChar { it.uppercase() }

/** Moves a card to another column. A finished card moved back is open again. Only top-level tasks are cards. */
fun TaskBoard.setStage(id: UUID, stage: Stage, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    if (item.parent != null) throw BoardException("Only top-level tasks are cards; subtasks move with their task.")
    if (item.isDone) reopen(id, actor, now)
    if (item.stage == stage) return
    item.stage = stage
    log(now, id, "moved", "Moved \"${item.title}\" to ${stage.displayName}", actor)
}

/**
 * Moves a task under [parentId] (or to the top level, in [groupId] or its current group) at [index] (null: last).
 * Subtasks move with it. A subtask that becomes a task of its own is a card in Next.
 */
fun TaskBoard.move(id: UUID, parentId: UUID?, index: Int? = null, groupId: UUID? = null, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    val parent = parentId?.let { get(it) }
    if (parent != null && (parent === item || parent.ancestors.any { it === item })) {
        throw BoardException("\"${item.title}\" can't move into itself or one of its subtasks.")
    }
    val group = if (parent == null) group(groupId ?: item.groupId) else null
    val wasRoot = item.parent == null
    (item.parent?.children ?: items).removeAll { it === item }
    val siblings = parent?.children ?: items
    siblings.add((index ?: siblings.size).coerceIn(0, siblings.size), item)
    item.parent = parent
    if (group != null) item.ownGroupId = group.id
    if (parent == null && !wasRoot) {
        item.stage = Stage.NEXT
    } else if (parent != null) {
        item.archivedAt = null
    }
    log(now, id, "moved", if (parent != null) "Moved \"${item.title}\" under \"${parent.title}\"" else "Moved \"${item.title}\" to ${group!!.name}", actor)
}

/** Moves a task among its siblings, before [before] (null: to the end). Its parent stays. */
fun TaskBoard.reorder(id: UUID, before: UUID?) {
    val item = get(id)
    val siblings = item.parent?.children ?: items
    if (before == id) return
    val target = before?.let { get(it) }
    if (target != null && siblings.none { it === target }) {
        throw BoardException("\"${target.title}\" isn't next to \"${item.title}\"; use move to put a task somewhere else.")
    }
    siblings.removeAll { it === item }
    siblings.add(if (target == null) siblings.size else siblings.indexOfFirst { it === target }, item)
}

/** Puts a task right after another one next to it (whatever is between them on screen). */
fun TaskBoard.reorderAfter(id: UUID, after: UUID) {
    val item = get(id)
    val target = get(after)
    val siblings = item.parent?.children ?: items
    if (after == id) return
    if (siblings.none { it === target }) {
        throw BoardException("\"${target.title}\" isn't next to \"${item.title}\"; use move to put a task somewhere else.")
    }
    siblings.removeAll { it === item }
    siblings.add(siblings.indexOfFirst { it === target } + 1, item)
}

/** Makes one change of a move. */
fun TaskBoard.applyMove(change: BoardMove, actor: Actor = Actor.USER, now: Instant) {
    when (change) {
        is BoardMove.Complete -> complete(change.id, actor, now)
        is BoardMove.SetStage -> setStage(change.id, change.stage, actor, now)
        is BoardMove.Move -> move(change.id, change.parentId, change.index, change.groupId, actor, now)
        is BoardMove.Reorder -> reorder(change.id, change.before)
        is BoardMove.ReorderAfter -> reorderAfter(change.id, change.after)
    }
}

/** A column of the Board: three stages, and Done (finished cards). */
enum class BoardColumn(val wire: String, val hint: String, val emptyText: String) {
    INBOX("inbox", "Captured, not decided yet", "Nothing new. Type below to add one."),
    NEXT("next", "Ready to pick up", "Pick something from the inbox."),
    DOING("doing", "In progress (keep it to 3)", "Start one thing."),
    DONE("done", "Finished recently", "Finished cards land here."),
    ;

    val displayName: String get() = wire.replaceFirstChar { it.uppercase() }

    val stage: Stage? get() = Stage.entries.firstOrNull { it.wire == wire }

    companion object {
        fun of(stage: Stage): BoardColumn = entries.first { it.wire == stage.wire }
    }
}

/** The Board's columns, how full Doing is, and what a drop or a keyboard move means (the web's boardmodel.js). */
object BoardColumns {
    /** Doing is kept small on purpose: more than this at once splits attention. */
    const val WIP_LIMIT = 3

    /** How long finished cards stay on the board (then they're in Done). */
    const val DONE_DAYS = 14L

    /** Said when Doing has more than three. */
    const val OVER_LIMIT_MESSAGE = "More than 3 at once splits attention. Finish one, or move one back to Next."

    enum class Fill { OK, FULL, OVER }

    /**
     * Cards per column, in tree order. Done shows cards finished in the last two weeks, newest first; archived cards
     * are on none.
     */
    fun of(roots: List<WorkItem>, now: Instant): Map<BoardColumn, List<WorkItem>> {
        val columns = BoardColumn.entries.associateWith { mutableListOf<WorkItem>() }
        val since = now.minus(Duration.ofDays(DONE_DAYS))
        for (root in roots) {
            if (root.archivedAt != null) continue
            val done = root.completedAt
            if (done != null) {
                if (!done.isBefore(since)) columns.getValue(BoardColumn.DONE).add(root)
            } else {
                columns.getValue(BoardColumn.of(root.stage)).add(root)
            }
        }
        // Newest first (equal times keep their order).
        val done = columns.getValue(BoardColumn.DONE).sortedByDescending { it.completedAt!! }
        columns.getValue(BoardColumn.DONE).apply {
            clear()
            addAll(done)
        }
        return columns
    }

    /** The column a card is in. */
    fun column(card: WorkItem): BoardColumn = if (card.isDone) BoardColumn.DONE else BoardColumn.of(card.stage)

    fun fill(count: Int, limit: Int = WIP_LIMIT): Fill = if (count > limit) Fill.OVER else if (count == limit) Fill.FULL else Fill.OK

    /**
     * The changes that move [card] to [column], before [before] (a card in that column; null: no place). Without a
     * place, a card from another column keeps its spot in the one shared order; within its column, no place means
     * last. Moving to Done finishes it; out of Done opens it again (by setting its column).
     */
    fun drop(card: WorkItem, column: BoardColumn, before: UUID?, columns: Map<BoardColumn, List<WorkItem>>): List<BoardMove> {
        val from = column(card)
        val stage = column.stage ?: return if (from == BoardColumn.DONE) emptyList() else listOf(BoardMove.Complete(card.id))
        val moves = mutableListOf<BoardMove>()
        if (from != column) moves.add(BoardMove.SetStage(card.id, stage))
        val list = columns[column].orEmpty()
        val target = before?.takeIf { b -> list.any { it.id == b && it.id != card.id } }
        val current = list.indexOfFirst { it.id == card.id }
        val currentNext = if (current >= 0 && current + 1 < list.size) list[current + 1].id else null
        if ((from == column && target != currentNext) || (from != column && target != null)) moves.add(BoardMove.Reorder(card.id, target))
        return moves
    }

    enum class Direction { LEFT, RIGHT, UP, DOWN }

    /** Where a keyboard move lands: left/right changes column, up/down moves within it. */
    fun keyboardMove(card: WorkItem, direction: Direction, columns: Map<BoardColumn, List<WorkItem>>): Pair<BoardColumn, UUID?>? {
        val order = BoardColumn.entries
        val from = column(card)
        val list = columns[from].orEmpty()
        val index = list.indexOfFirst { it.id == card.id }
        return when (direction) {
            Direction.LEFT, Direction.RIGHT -> {
                val to = order.indexOf(from) + if (direction == Direction.LEFT) -1 else 1
                if (to in order.indices) order[to] to null else null
            }
            Direction.UP -> if (from == BoardColumn.DONE || index <= 0) null else from to list[index - 1].id
            Direction.DOWN -> if (from == BoardColumn.DONE || index < 0 || index >= list.size - 1) null else from to (if (index + 2 < list.size) list[index + 2].id else null)
        }
    }
}

/** Moves in the Tasks outline (the web's outlinemodel.js): indent, outdent, up/down, and drops. */
object OutlineMoves {
    /** Where a drop lands on a row: before it, after it, or inside it. */
    enum class Zone { BEFORE, INSIDE, AFTER }

    /** A task's siblings: its parent's subtasks, or the top-level tasks on screen ([roots]; null: every top-level task). */
    private fun siblings(board: TaskBoard, item: WorkItem, roots: List<WorkItem>?): List<WorkItem> = item.parent?.children ?: roots ?: board.items

    /** The siblings shown (finished ones hidden unless [showDone]; the task itself always counts). */
    private fun shown(board: TaskBoard, item: WorkItem, showDone: Boolean, roots: List<WorkItem>?): List<WorkItem> =
        siblings(board, item, roots).filter { showDone || !it.isDone || it === item }

    /** Makes the task a subtask of the one above it (its previous sibling shown), last. Null when there's none. */
    fun indent(board: TaskBoard, id: UUID, showDone: Boolean = true, roots: List<WorkItem>? = null): List<BoardMove>? {
        val item = board.find(id) ?: return null
        val visible = shown(board, item, showDone, roots)
        val i = visible.indexOfFirst { it === item }
        if (i <= 0) return null
        return listOf(BoardMove.Move(id, visible[i - 1].id))
    }

    /** Moves the task out one level: right after its parent. Null at the top level. */
    fun outdent(board: TaskBoard, id: UUID): List<BoardMove>? {
        val item = board.find(id) ?: return null
        val parent = item.parent ?: return null
        val up = parent.parent?.let { BoardMove.Move(id, it.id) } ?: BoardMove.Move(id, null, groupId = parent.groupId)
        return listOf(up, BoardMove.ReorderAfter(id, parent.id))
    }

    /** Up (-1) or down (1) among the siblings shown (finished ones may be hidden: they're stepped over). */
    fun step(board: TaskBoard, id: UUID, delta: Int, showDone: Boolean = true, roots: List<WorkItem>? = null): List<BoardMove>? {
        val item = board.find(id) ?: return null
        val visible = shown(board, item, showDone, roots)
        val i = visible.indexOfFirst { it === item }
        if (i < 0 || i + delta !in visible.indices) return null
        val neighbour = visible[i + delta].id
        return listOf(if (delta < 0) BoardMove.Reorder(id, neighbour) else BoardMove.ReorderAfter(id, neighbour))
    }

    /** Puts [dragged] before, after or inside [target]; null when it can't go there (into itself or its own subtasks). */
    fun drop(board: TaskBoard, dragged: UUID, target: UUID, zone: Zone): List<BoardMove>? {
        if (dragged == target) return null
        val item = board.find(dragged) ?: return null
        val onto = board.find(target) ?: return null
        if (onto.ancestors.any { it === item }) return null
        if (zone == Zone.INSIDE) return listOf(BoardMove.Move(dragged, target))
        val place = if (zone == Zone.AFTER) BoardMove.ReorderAfter(dragged, target) else BoardMove.Reorder(dragged, target)
        val parent = onto.parent
        if (parent != null) return if (parent === item.parent) listOf(place) else listOf(BoardMove.Move(dragged, parent.id), place)
        return if (item.parent != null || item.groupId != onto.groupId) listOf(BoardMove.Move(dragged, null, groupId = onto.groupId), place) else listOf(place)
    }

    /** A task to another group: a top-level one changes group; a subtask becomes a task there, last. */
    fun toGroup(board: TaskBoard, id: UUID, groupId: UUID): List<BoardMove>? {
        val item = board.find(id) ?: return null
        if (item.parent == null && item.groupId == groupId) return null
        return listOf(BoardMove.Move(id, null, groupId = groupId))
    }
}
