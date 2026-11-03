package dev.onething.core

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID

// Planning: a block of time to work on a task (the Plan view's day and week grid). Same rules as TaskBoard.Plan.cs and
// PlanDays.cs in the C# core (tests/fixtures/plan.json).

object Planning {
    const val DEFAULT_MINUTES = 30
    const val MIN_MINUTES = 5
    const val MAX_MINUTES = 12 * 60

    internal val described: DateTimeFormatter = DateTimeFormatter.ofPattern("EEE d MMM HH:mm", Locale.US)
}

/** Plans [minutes] (null: what it had, else 30) from [start] to work on a task. */
fun TaskBoard.plan(id: UUID, start: Instant, minutes: Int?, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    if (item.isDone) throw BoardException("\"${item.title}\" is already done.")
    val length = minutes ?: item.plannedMinutes ?: Planning.DEFAULT_MINUTES
    if (length !in Planning.MIN_MINUTES..Planning.MAX_MINUTES) {
        throw BoardException("A planned block is ${Planning.MIN_MINUTES} minutes to ${Planning.MAX_MINUTES / 60} hours.")
    }
    item.plannedAt = start
    item.plannedMinutes = if (length == Planning.DEFAULT_MINUTES) null else length
    log(now, id, "scheduled", "Planned \"${item.title}\" ${Planning.described.format(start.atZone(zone))}, $length min", actor)
}

/** Takes a task off the plan (nothing happens when it isn't on it). */
fun TaskBoard.unplan(id: UUID, actor: Actor = Actor.USER, now: Instant) {
    val item = get(id)
    if (item.plannedAt == null) return
    item.plannedAt = null
    item.plannedMinutes = null
    log(now, id, "scheduled", "Took \"${item.title}\" off the plan", actor)
}

/** How long its planned block is, in minutes. */
val WorkItem.plannedLength: Int get() = plannedMinutes ?: Planning.DEFAULT_MINUTES

/** When its planned block ends (null: not planned). */
val WorkItem.plannedEnd: Instant? get() = plannedAt?.plusSeconds(60L * plannedLength)

/**
 * What the Plan view shows for some days (from a local date, in the user's time zone): the blocks planned in them, the
 * deadlines on them, the tasks to do that aren't planned yet (in Do now order), the ones put off (Later), and what each
 * day's blocks take in energy (every group). PlanDays.cs in the C# core.
 */
data class PlanDays(
    val from: LocalDate,
    val days: Int,
    val blocks: List<WorkItem>,
    val deadlines: List<WorkItem>,
    val unplanned: List<AgendaEntry>,
    val later: List<AgendaEntry>,
    val energy: Map<LocalDate, Double>,
) {
    companion object {
        const val MAX_DAYS = 31

        fun build(board: TaskBoard, first: LocalDate, days: Int, zone: ZoneId, now: Instant, groupId: UUID? = null): PlanDays {
            val count = days.coerceIn(1, MAX_DAYS)
            val start = midnight(first, zone)
            val end = midnight(first.plusDays(count.toLong()), zone)
            val items = board.allItems.filter { !it.isArchived && (groupId == null || it.groupId == groupId) }

            // Blocks that overlap the days (one crossing midnight shows on both), and deadlines in them; stable by time.
            val blocks = items.filter { i -> val at = i.plannedAt; at != null && at.isBefore(end) && i.plannedEnd!!.isAfter(start) }.sortedBy { it.plannedAt }
            val deadlines = items.filter { i -> val d = i.deadline; d != null && !d.isBefore(start) && d.isBefore(end) }.sortedBy { it.deadline }

            // Not planned: no block, or its block is over (planned again from here).
            val agenda = Agenda.build(board, now, groupId, recentNoteCount = 0)
            fun notPlanned(e: AgendaEntry) = e.item.plannedEnd?.let { !it.isAfter(now) } ?: true
            val energy = (0 until count).associate { i -> first.plusDays(i.toLong()).let { it to Energy.plannedOn(board, it, zone) } }
            return PlanDays(first, count, blocks, deadlines, agenda.now.filter(::notPlanned), agenda.waiting.filter(::notPlanned), energy)
        }

        /** Local midnight of [day] in [zone]. */
        fun midnight(day: LocalDate, zone: ZoneId): Instant = day.atStartOfDay(zone).toInstant()
    }
}
