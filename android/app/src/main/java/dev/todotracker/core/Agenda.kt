package dev.todotracker.core

import java.time.Instant
import java.util.UUID

data class AgendaEntry(
    val item: WorkItem,
    val state: ItemState,
    val effectivePriority: Priority,
    val needsAttention: Boolean,
    val dueReminder: Reminder?,
    val wakeAt: Instant?,
    val isOverdue: Boolean,
    val breadcrumb: List<String>,
    /** The open task it waits for ("after task X"), itself or through a parent. */
    val waitingFor: WorkItem? = null,
) {
    /** "Step 2 of 10" for steps of a sequential parent. */
    val stepLabel: String?
        get() {
            val parent = item.parent ?: return null
            if (!parent.sequential) return null
            val index = parent.children.indexOfFirst { it === item }
            return if (index < 0) null else "Step ${index + 1} of ${parent.children.size}"
        }
}

data class GroupCount(val now: Int, val waiting: Int, val attention: Int)

data class Dashboard(
    val focus: AgendaEntry?,
    val now: List<AgendaEntry>,
    val waiting: List<AgendaEntry>,
    val groupCounts: Map<UUID, GroupCount>,
)

/** Time-aware projection of the board. Must match TodoTracker.Core.Agenda and the Swift one (see shared scenarios). */
object Agenda {
    fun stateOf(item: WorkItem, now: Instant): ItemState = when {
        item.isDone -> ItemState.DONE
        TaskBoard.findBlockingStep(item) != null -> ItemState.LOCKED
        waits(item, now) || item.ancestors.any { waits(it, now) } -> ItemState.WAITING
        item.hasOpenChildren -> ItemState.CONTAINER
        else -> ItemState.ACTIONABLE
    }

    fun effectivePriority(item: WorkItem): Priority =
        (item.ancestors.map { it.priority } + item.priority).maxBy { it.rank }

    fun build(board: TaskBoard, now: Instant, groupId: UUID? = null): Dashboard {
        val open = board.allItems.filter { !it.isDone }.mapIndexed { offset, item -> offset to describe(item, now) }
        val nowAll = open.filter { (_, e) ->
            e.state == ItemState.ACTIONABLE || (e.needsAttention && (e.state == ItemState.WAITING || e.state == ItemState.CONTAINER))
        }
        val waitingAll = open.filter { (_, e) ->
            e.state == ItemState.WAITING && !e.needsAttention && waits(e.item, now) &&
                e.item.ancestors.none { waits(it, now) }
        }

        val counts = board.groups.associate { g ->
            g.id to GroupCount(
                now = nowAll.count { it.second.item.groupId == g.id },
                waiting = waitingAll.count { it.second.item.groupId == g.id },
                attention = nowAll.count { it.second.item.groupId == g.id && it.second.needsAttention },
            )
        }

        fun inScope(item: WorkItem) = groupId == null || item.groupId == groupId

        // A due reminder comes first; then the person's own order; then tasks never placed, by the automatic rules
        // (priority, overdue, deadline, age). Same rules as the other apps.
        val rank = HashMap<UUID, Int>()
        board.nowOrder.forEachIndexed { i, id -> rank.putIfAbsent(id, i) }
        val scoped = nowAll.filter { inScope(it.second.item) }
        val nowList = arrange(scoped.filter { it.second.needsAttention }, rank) + arrange(scoped.filter { !it.second.needsAttention }, rank)

        val waitingList = waitingAll.filter { inScope(it.second.item) }.sortedWith { a, b ->
            val x = a.second
            val y = b.second
            val wx = x.wakeAt ?: Instant.MAX
            val wy = y.wakeAt ?: Instant.MAX
            when {
                wx != wy -> wx.compareTo(wy)
                x.effectivePriority != y.effectivePriority -> y.effectivePriority.rank.compareTo(x.effectivePriority.rank)
                x.item.createdAt != y.item.createdAt -> x.item.createdAt.compareTo(y.item.createdAt)
                else -> a.first.compareTo(b.first)
            }
        }.map { it.second }

        return Dashboard(nowList.firstOrNull(), nowList, waitingList, counts)
    }

    /** Negative when x comes first by the automatic rules, zero when they tie. */
    private fun compareAutomatic(x: AgendaEntry, y: AgendaEntry): Int = when {
        x.effectivePriority != y.effectivePriority -> y.effectivePriority.rank.compareTo(x.effectivePriority.rank)
        x.isOverdue != y.isOverdue -> if (x.isOverdue) -1 else 1
        (x.item.deadline ?: Instant.MAX) != (y.item.deadline ?: Instant.MAX) -> (x.item.deadline ?: Instant.MAX).compareTo(y.item.deadline ?: Instant.MAX)
        x.item.createdAt != y.item.createdAt -> x.item.createdAt.compareTo(y.item.createdAt)
        else -> 0
    }

    /** Placed tasks in the person's order, then the others in the automatic order. */
    private fun arrange(entries: List<Pair<Int, AgendaEntry>>, rank: Map<UUID, Int>): List<AgendaEntry> {
        val placed = entries.filter { rank.containsKey(it.second.item.id) }.sortedBy { rank[it.second.item.id] ?: 0 }
        val others = entries.filter { !rank.containsKey(it.second.item.id) }.sortedWith { a, b ->
            val c = compareAutomatic(a.second, b.second)
            if (c != 0) c else a.first.compareTo(b.first)
        }
        return placed.map { it.second } + others.map { it.second }
    }

    private fun isAfter(date: Instant?, now: Instant) = date != null && date.isAfter(now)

    /** Snoozed until a later time, or until another task is done. */
    private fun waits(item: WorkItem, now: Instant) = isAfter(item.nextActionAt, now) || item.waitingFor != null

    private fun describe(item: WorkItem, now: Instant): AgendaEntry {
        val state = stateOf(item, now)
        val due = if (state == ItemState.LOCKED || state == ItemState.DONE) null else item.reminders.filter { it.isDue(now) }.minByOrNull { it.dueAt }
        val wake = if (state == ItemState.WAITING) {
            (item.ancestors.mapNotNull { it.nextActionAt } + listOfNotNull(item.nextActionAt)).filter { it.isAfter(now) }.maxOrNull()
        } else {
            null
        }
        return AgendaEntry(
            item = item,
            state = state,
            effectivePriority = effectivePriority(item),
            needsAttention = due != null,
            dueReminder = due,
            wakeAt = wake,
            isOverdue = item.deadline?.isBefore(now) ?: false,
            breadcrumb = item.ancestors.reversed().map { it.title },
            waitingFor = if (state == ItemState.WAITING) item.waitingFor ?: item.ancestors.firstNotNullOfOrNull { it.waitingFor } else null,
        )
    }
}
