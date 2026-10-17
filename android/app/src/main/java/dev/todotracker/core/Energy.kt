package dev.todotracker.core

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlin.math.roundToLong

/**
 * Energy: how much a task takes per hour (1 to 3 ⚡; a task that doesn't say counts as 1), and how much there is in a
 * day ([TaskBoard.dailyEnergy]). A day's energy is what its planned blocks take: each block's hours on that day times
 * its task's energy, done ones included. Energy.cs in the C# core, same rule (tests/fixtures/energy.json).
 */
object Energy {
    val LEVELS = 1..3
    const val UNSET = 1
    const val DEFAULT_PER_DAY = 10
    const val MAX_PER_DAY = 100

    fun of(item: WorkItem): Int = item.energy ?: UNSET

    /** What the blocks planned on [day] (a local date in [zone]) take, rounded to 0.01. */
    fun plannedOn(board: TaskBoard, day: LocalDate, zone: ZoneId): Double {
        val start = day.atStartOfDay(zone).toInstant()
        val end = day.plusDays(1).atStartOfDay(zone).toInstant()
        var total = 0.0
        for (item in board.allItems) {
            if (item.isArchived) continue
            val at = item.plannedAt ?: continue
            val blockEnd = at.plusSeconds(60L * (item.plannedMinutes ?: 30))
            val from = maxOf(at, start)
            val to = minOf(blockEnd, end)
            if (to.isAfter(from)) total += Duration.between(from, to).seconds / 3600.0 * of(item)
        }
        return (total * 100).roundToLong() / 100.0
    }

    /** "⚡ 4.5 of 10 planned today", and by how much it's past what there is (same words as the other apps). */
    fun line(planned: Double, perDay: Int): String {
        fun n(x: Double): String {
            val r = (x * 10).roundToLong() / 10.0
            return if (r == Math.rint(r)) r.toLong().toString() else r.toString()
        }
        val over = planned - perDay
        return if (over > 0) "⚡ ${n(planned)} of $perDay planned today: ${n(over)} too much." else "⚡ ${n(planned)} of $perDay planned today."
    }

    /** Today's line, once something is planned today (the same rule in every app). */
    fun today(board: TaskBoard, now: Instant, zone: ZoneId): String? {
        val planned = plannedOn(board, now.atZone(zone).toLocalDate(), zone)
        return if (planned > 0) line(planned, board.dailyEnergy) else null
    }
}
