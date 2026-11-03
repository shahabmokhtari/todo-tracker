package dev.onething.core

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.ChronoUnit
import java.util.UUID

/** One day in a report: time tracked (and how much of it was focus sessions), tasks finished and created. */
data class ReportDay(val date: LocalDate, val trackedSeconds: Long, val focusSeconds: Long, val completed: Int, val created: Int)

data class ReportGroup(val groupId: UUID, val name: String, val color: String?, val trackedSeconds: Long)

data class ReportTask(val id: UUID, val title: String, val groupId: UUID, val done: Boolean, val trackedSeconds: Long)

data class ReportSpan(val start: Instant, val end: Instant, val source: TimeSource)

/** A task on the timeline: when it was created and finished (or is due), and when it was worked on. */
data class ReportRow(
    val id: UUID,
    val title: String,
    val groupId: UUID,
    val createdAt: Instant,
    val completedAt: Instant?,
    val deadline: Instant?,
    val work: List<ReportSpan>,
)

/**
 * Where the time went between two local dates (inclusive): per day, group, task, and weekday-hour, what got done, and
 * a timeline of the tasks worked on. Time is the tracked time entries (a running timer counts until now), split at local
 * midnights; archived tasks count too (the work happened). TimeReport.cs in the C# core, the same numbers
 * (tests/fixtures/reports.json).
 */
data class TimeReport(
    val from: LocalDate,
    val to: LocalDate,
    val trackedSeconds: Long,
    val focusSessions: Int,
    val completed: Int,
    val created: Int,
    val activeDays: Int,
    val longestStreak: Int,
    val days: List<ReportDay>,
    val groups: List<ReportGroup>,
    val tasks: List<ReportTask>,
    /** [weekday: 0 = Sunday … 6 = Saturday][hour 0…23], seconds. */
    val hourGrid: List<List<Long>>,
    val timeline: List<ReportRow>,
) {
    companion object {
        const val MAX_DAYS = 732
        const val MAX_TASKS = 15
        const val MAX_TIMELINE_ROWS = 40

        // Time is summed in ticks (100 ns, as .NET counts), whole seconds at the end: the days add up to the total.
        private const val TICKS_PER_SECOND = 10_000_000L

        private fun ticks(at: Instant): Long = at.epochSecond * TICKS_PER_SECOND + at.nano / 100

        private fun seconds(ticks: Long): Long = ticks / TICKS_PER_SECOND

        private fun instant(ticks: Long): Instant = Instant.ofEpochSecond(Math.floorDiv(ticks, TICKS_PER_SECOND), Math.floorMod(ticks, TICKS_PER_SECOND) * 100)

        fun build(board: TaskBoard, from: LocalDate, to: LocalDate, zone: ZoneId, now: Instant, groupId: UUID? = null): TimeReport {
            if (to.isBefore(from)) throw BoardException("The end date must be on or after the start date.")
            val dayCount = (ChronoUnit.DAYS.between(from, to) + 1).toInt()
            if (dayCount > MAX_DAYS) throw BoardException("A report can cover at most two years.")

            val rangeStart = PlanDays.midnight(from, zone)
            val rangeEnd = PlanDays.midnight(to.plusDays(1), zone)
            val tracked = LongArray(dayCount)
            val focus = LongArray(dayCount)
            val completed = IntArray(dayCount)
            val created = IntArray(dayCount)
            val grid = Array(7) { LongArray(24) }
            val byGroup = LinkedHashMap<UUID, Long>()
            // By task, in the order first seen (ties keep it, as in C#).
            val byTask = LinkedHashMap<UUID, Long>()
            val rows = mutableListOf<Pair<ReportRow, Long>>()

            fun dayIndex(at: Instant) = ChronoUnit.DAYS.between(from, at.atZone(zone).toLocalDate()).toInt()
            fun inRange(at: Instant) = !at.isBefore(rangeStart) && at.isBefore(rangeEnd)

            // Adds a stretch of time to the days and weekday-hours it covers (split at local hours).
            fun spread(start: Instant, end: Instant, source: TimeSource) {
                var cursor = ticks(start)
                val stopAt = ticks(end)
                while (cursor < stopAt) {
                    val at = instant(cursor)
                    val local = at.atZone(zone)
                    val millis = (local.nano / 1_000_000).toLong()
                    val nextHour = cursor + (60 - local.minute) * 60L * TICKS_PER_SECOND - local.second * TICKS_PER_SECOND - millis * 10_000
                    val stop = minOf(nextHour, stopAt)
                    val part = stop - cursor
                    val index = dayIndex(at)
                    if (index in 0 until dayCount) {
                        tracked[index] += part
                        if (source == TimeSource.FOCUS) focus[index] += part
                    }
                    grid[local.dayOfWeek.value % 7][local.hour] += part
                    cursor = stop
                }
            }

            for (root in board.items) {
                if (groupId != null && root.groupId != groupId) continue
                val work = mutableListOf<ReportSpan>()
                for (item in root.selfAndDescendants) {
                    item.completedAt?.takeIf(::inRange)?.let { completed[dayIndex(it)]++ }
                    if (inRange(item.createdAt)) created[dayIndex(item.createdAt)]++
                    for (entry in item.timeEntries) {
                        val start = maxOf(entry.start, rangeStart)
                        val end = minOf(entry.end ?: now, rangeEnd)
                        if (!end.isAfter(start)) continue
                        work.add(ReportSpan(start, end, entry.source))
                        val part = ticks(end) - ticks(start)
                        byGroup[root.groupId] = (byGroup[root.groupId] ?: 0L) + part
                        byTask[root.id] = (byTask[root.id] ?: 0L) + part
                        spread(start, end, entry.source)
                    }
                }
                val touched = work.isNotEmpty() || root.completedAt?.let(::inRange) == true || inRange(root.createdAt)
                if (touched) {
                    val sorted = work.sortedBy { it.start }
                    rows.add(ReportRow(root.id, root.title, root.groupId, root.createdAt, root.completedAt, root.deadline, sorted) to sorted.sumOf { ticks(it.end) - ticks(it.start) })
                }
            }

            val reportDays = (0 until dayCount).map { ReportDay(from.plusDays(it.toLong()), seconds(tracked[it]), seconds(focus[it]), completed[it], created[it]) }
            val active = reportDays.map { it.trackedSeconds > 0 || it.completed > 0 }
            var streak = 0
            var longest = 0
            for (on in active) {
                streak = if (on) streak + 1 else 0
                longest = maxOf(longest, streak)
            }

            val focusSessions = board.activity.count { a ->
                a.kind == "focusCompleted" && inRange(a.at) && (groupId == null || board.find(a.itemId)?.groupId == groupId)
            }
            val groups = board.groups.filter { byGroup.containsKey(it.id) }
                .map { ReportGroup(it.id, it.name, it.color, seconds(byGroup.getValue(it.id))) }
                .sortedByDescending { it.trackedSeconds }
            val tasks = byTask.entries.sortedByDescending { it.value }.take(MAX_TASKS).map { (id, t) ->
                val r = board.get(id)
                ReportTask(r.id, r.title, r.groupId, r.isDone, seconds(t))
            }

            // The most worked rows (then the latest finished or made), shown by when their work (else finish or start) began.
            val timeline = rows
                .sortedWith(compareByDescending<Pair<ReportRow, Long>> { it.second }.thenByDescending { it.first.completedAt ?: it.first.createdAt })
                .take(MAX_TIMELINE_ROWS)
                .map { it.first }
                .sortedBy { it.work.firstOrNull()?.start ?: it.completedAt ?: it.createdAt }

            return TimeReport(
                from, to,
                trackedSeconds = seconds(byTask.values.sum()),
                focusSessions = focusSessions,
                completed = reportDays.sumOf { it.completed },
                created = reportDays.sumOf { it.created },
                activeDays = active.count { it },
                longestStreak = longest,
                days = reportDays,
                groups = groups,
                tasks = tasks,
                hourGrid = grid.map { row -> row.map(::seconds) },
                timeline = timeline,
            )
        }
    }
}
