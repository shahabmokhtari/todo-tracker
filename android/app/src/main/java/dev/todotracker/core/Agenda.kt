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

/** A top-level task summed up: how many of its leaves are done, how many to do now or waiting, when the next is back. */
data class OverviewEntry(
    val item: WorkItem,
    val doneLeaves: Int,
    val totalLeaves: Int,
    val actionableCount: Int,
    val waitingCount: Int,
    val nextWakeAt: Instant?,
    val topPriority: Priority,
)

data class RecentNote(val item: WorkItem, val note: Note)

data class Dashboard(
    val focus: AgendaEntry?,
    val now: List<AgendaEntry>,
    val waiting: List<AgendaEntry>,
    val groupCounts: Map<UUID, GroupCount>,
    val overview: List<OverviewEntry> = emptyList(),
    val recentNotes: List<RecentNote> = emptyList(),
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

    /**
     * What got done since midnight (in [zone]), newest first. A parent finished together with its subtasks is one entry
     * (the subtasks it closed aren't repeated); steps done on their own count. Same rule as the C# core.
     */
    fun doneToday(board: TaskBoard, now: Instant, zone: java.time.ZoneId, groupId: UUID? = null): List<WorkItem> {
        val start = now.atZone(zone).toLocalDate().atStartOfDay(zone).toInstant()
        // Finished by itself (its own "completed" entry), not closed along with its parent: the last step of an
        // in-order task counts though it finishes the task at the same moment.
        val own = board.activity.filter { it.kind == "completed" && !it.at.isBefore(start) }.map { it.itemId to it.at }.toSet()
        val finished = board.allItems
            .filter { item ->
                val at = item.completedAt ?: return@filter false
                !at.isBefore(start) && !at.isAfter(now) && !item.isArchived && (groupId == null || item.groupId == groupId) &&
                    (item.parent?.completedAt != at || (item.id to at) in own)
            }
            .map { it to it.completedAt!! }
        // A repeating task done today has moved on to its next time (open again): its "completed" entry tells.
        val moved = board.allItems
            .filter { !it.isArchived && board.wasRolledForward(it) && (groupId == null || it.groupId == groupId) }
            .mapNotNull { item -> doneAt(board, item)?.let { item to it } }
            .filter { (_, at) -> !at.isBefore(start) && !at.isAfter(now) }
        return (finished + moved).sortedByDescending { it.second }.map { it.first }
    }

    /** When it was done: when it was finished, or for a repeating task that moved on, when its last time was done. */
    fun doneAt(board: TaskBoard, item: WorkItem): Instant? =
        item.completedAt ?: if (board.wasRolledForward(item)) board.activity.lastOrNull { it.itemId == item.id && it.kind == "completed" }?.at else null

    /** A line to feel good about what got done today (null until something is done); the other apps say the same. */
    fun cheer(count: Int): String? = when {
        count <= 0 -> null
        count == 1 -> "First one done. Nice start!"
        count < 5 -> "$count done today. Keep it going!"
        count < 10 -> "$count done today. You are on a roll!"
        else -> "$count done today. What a day!"
    }

    fun effectivePriority(item: WorkItem): Priority =
        (item.ancestors.map { it.priority } + item.priority).maxBy { it.rank }

    fun build(board: TaskBoard, now: Instant, groupId: UUID? = null, recentNoteCount: Int = 5): Dashboard {
        val open = board.allItems.filter { !it.isDone && !it.isArchived }.mapIndexed { offset, item -> offset to describe(item, now) }
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
        // A reminder that just went off comes first: its task was never placed, or the reminder went off after Do now
        // was last arranged (one seen then stays where the task was put). Then what's planned for now (its block in
        // the Plan view is on), ahead of the arranged order.
        val arranged = board.nowArrangedAt
        fun fresh(e: AgendaEntry) = e.needsAttention &&
            (e.item.id !in rank || arranged == null || e.item.reminders.any { it.isDue(now) && it.dueAt.isAfter(arranged) })
        val first = scoped.filter { fresh(it.second) }
        val planned = scoped.filter { !it.second.needsAttention && it.second.item.isPlannedAt(now) }.sortedBy { it.second.item.plannedAt }
        val nowList = arrange(first, rank) + planned.map { it.second } +
            arrange(scoped.filter { it !in first && it !in planned }, rank)

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

        val overview = board.items.filter { !it.isDone && inScope(it) }.map { summarize(it, nowList, waitingList) }
        val notes = if (recentNoteCount <= 0) emptyList() else board.allItems.filter { !it.isArchived && inScope(it) }
            .flatMap { item -> item.notes.map { RecentNote(item, it) } }
            .sortedByDescending { it.note.at }
            .take(recentNoteCount)

        return Dashboard(nowList.firstOrNull(), nowList, waitingList, counts, overview, notes)
    }

    /** Workstreams are tasks with subtasks; single tasks already show in Do now and Waiting. */
    fun workstreams(dashboard: Dashboard): List<OverviewEntry> = dashboard.overview.filter { it.item.children.isNotEmpty() }

    private fun summarize(root: WorkItem, now: List<AgendaEntry>, waiting: List<AgendaEntry>): OverviewEntry {
        val subtree = root.selfAndDescendants
        val ids = subtree.map { it.id }.toSet()
        val leaves = subtree.filter { it.children.isEmpty() }
        return OverviewEntry(
            item = root,
            doneLeaves = leaves.count { it.isDone },
            totalLeaves = leaves.size,
            actionableCount = now.count { it.item.id in ids },
            waitingCount = waiting.count { it.item.id in ids },
            nextWakeAt = waiting.filter { it.item.id in ids }.mapNotNull { it.wakeAt }.minOrNull(),
            topPriority = subtree.filter { !it.isDone }.map { effectivePriority(it) }.maxByOrNull { it.rank } ?: root.priority,
        )
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
