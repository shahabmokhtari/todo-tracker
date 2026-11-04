package dev.onething.core

import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * How the Plan view looks: a day or a week, work hours or the whole day, and what work hours and work days are (minutes
 * after midnight; days 1 = Monday … 7 = Sunday). PlanSettings in the server (PlanEndpoints.cs) and PlanGrid.swift, the
 * same checks. Each group keeps its own, on this phone.
 */
data class PlanSettings(
    val view: String = WEEK,
    val hours: String = WORK,
    val workStart: Int = 9 * 60,
    val workEnd: Int = 17 * 60,
    val workDays: List<Int> = listOf(1, 2, 3, 4, 5),
    /** 1: Monday, 7: Sunday. */
    val weekStart: Int = 1,
) {
    /** Why these work hours and days can't be kept (null: they can). */
    val problem: String?
        get() {
            if (workStart < 0 || workStart >= 24 * 60 || workEnd <= 0 || workEnd > 24 * 60 || workEnd - workStart < 60) {
                return "Work hours are within a day and at least an hour long."
            }
            if (workDays.isEmpty() || workDays.any { it !in 1..7 }) return "Pick at least one work day."
            return if (weekStart == 1 || weekStart == 7) null else "A week starts on Monday or Sunday."
        }

    val isWeek: Boolean get() = view == WEEK
    val isWorkHours: Boolean get() = hours == WORK

    /** As kept on this phone (JSON, the same fields as the server's). */
    fun encode(): String = JsonObject(
        mapOf(
            "view" to JsonPrimitive(view),
            "hours" to JsonPrimitive(hours),
            "workStart" to JsonPrimitive(workStart),
            "workEnd" to JsonPrimitive(workEnd),
            "workDays" to JsonArray(workDays.map { JsonPrimitive(it) }),
            "weekStart" to JsonPrimitive(weekStart),
        ),
    ).toString()

    companion object {
        /** Settings kept before (anything unreadable or not allowed: the defaults). */
        fun decode(text: String?): PlanSettings {
            if (text.isNullOrBlank()) return PlanSettings()
            return runCatching {
                val o = Json.parseToJsonElement(text).jsonObject
                val d = PlanSettings()
                PlanSettings(
                    view = o["view"]?.jsonPrimitive?.content?.takeIf { it == DAY || it == WEEK } ?: d.view,
                    hours = o["hours"]?.jsonPrimitive?.content?.takeIf { it == WORK || it == FULL } ?: d.hours,
                    workStart = o["workStart"]?.jsonPrimitive?.int ?: d.workStart,
                    workEnd = o["workEnd"]?.jsonPrimitive?.int ?: d.workEnd,
                    workDays = o["workDays"]?.jsonArray?.map { it.jsonPrimitive.int } ?: d.workDays,
                    weekStart = o["weekStart"]?.jsonPrimitive?.int ?: d.weekStart,
                ).takeIf { it.problem == null }
            }.getOrNull() ?: PlanSettings()
        }

        const val DAY = "day"
        const val WEEK = "week"
        const val WORK = "work"
        const val FULL = "full"
    }
}

/** A part of a block on one day: a block crossing midnight goes on until the end of its day and carries on the next. */
data class PlanSegment(
    val day: LocalDate,
    /** Minutes after that day's midnight. */
    val from: Int,
    val to: Int,
    /** It started on an earlier day. */
    val continues: Boolean,
    /** The block ends on this day (where its length is changed). */
    val isLast: Boolean,
)

/** The Plan view's grid: the days shown, which hours, where blocks go and how dragging snaps (the web's plan.js, PlanGrid.swift). */
object PlanGrid {
    /** Times snap to a quarter of an hour. */
    const val SNAP = 15

    /** The first day shown: the day itself (day view), or the start of its week (Monday or Sunday). */
    fun firstDay(day: LocalDate, week: Boolean, weekStart: Int = 1): LocalDate {
        if (!week) return day
        val iso = day.dayOfWeek.value
        return day.minusDays((if (weekStart == 7) iso % 7 else iso - 1).toLong())
    }

    /** The days shown: in a week with work hours, only the work days (all of them if none is). */
    fun shownDays(first: LocalDate, settings: PlanSettings): List<LocalDate> {
        val all = (0 until if (settings.isWeek) 7 else 1).map { first.plusDays(it.toLong()) }
        if (all.size <= 1 || !settings.isWorkHours) return all
        val kept = all.filter { it.dayOfWeek.value in settings.workDays }
        return kept.ifEmpty { all }
    }

    /** Minutes after local midnight, snapped to the grid (and kept within the day). */
    fun snapped(minutes: Double, step: Int = SNAP): Int {
        val rounded = Math.floor(minutes / step + 0.5).toInt() * step
        return rounded.coerceIn(0, 24 * 60 - step)
    }

    /** A block's new length after its bottom edge moved by [delta] minutes: snapped, a quarter of an hour to 12 hours. */
    fun resized(minutes: Int, delta: Double, step: Int = SNAP): Int {
        val rounded = Math.floor((minutes + delta) / step + 0.5).toInt() * step
        return rounded.coerceIn(step, Planning.MAX_MINUTES)
    }

    /** Where a block moved by [minutes] (and [days]) starts: snapped to the grid on its new day (the same wall-clock time). */
    fun moved(start: Instant, minutes: Double, days: Int, zone: ZoneId): Instant {
        val day = start.atZone(zone).toLocalDate()
        val at = Duration.between(PlanDays.midnight(day, zone), start).seconds / 60.0 + minutes
        return time(snapped(at), day.plusDays(days.toLong()), zone)
    }

    /** The moment [minutes] after midnight on [day] (wall-clock time there). */
    fun time(minutes: Int, day: LocalDate, zone: ZoneId): Instant =
        day.atStartOfDay().plusMinutes(minutes.toLong()).atZone(zone).toInstant()

    /** Minutes after midnight on its own day. */
    fun minutes(at: Instant, zone: ZoneId): Int = at.atZone(zone).let { it.hour * 60 + it.minute }

    /** A block, cut at midnights into a part per day. */
    fun segments(start: Instant, minutes: Int, zone: ZoneId): List<PlanSegment> {
        val end = start.plusSeconds(minutes * 60L)
        val parts = mutableListOf<PlanSegment>()
        var day = start.atZone(zone).toLocalDate()
        var midnight = PlanDays.midnight(day, zone)
        while (midnight.isBefore(end)) {
            val next = PlanDays.midnight(day.plusDays(1), zone)
            val from = maxOf(start, midnight)
            val to = minOf(end, next)
            parts.add(
                PlanSegment(
                    day,
                    Math.round(Duration.between(midnight, from).seconds / 60.0).toInt(),
                    Math.round(Duration.between(midnight, to).seconds / 60.0).toInt(),
                    continues = start.isBefore(midnight),
                    isLast = !end.isAfter(next),
                ),
            )
            day = day.plusDays(1)
            midnight = next
        }
        return parts
    }

    /** The hours shown (minutes after midnight): the whole day, or work hours stretched to show any block outside them on the days shown. */
    fun hours(settings: PlanSettings, segments: List<PlanSegment>, days: List<LocalDate>): Pair<Int, Int> {
        if (!settings.isWorkHours) return 0 to 24 * 60
        val shown = days.toSet()
        var from = settings.workStart
        var to = settings.workEnd
        for (segment in segments) {
            if (segment.day !in shown) continue
            from = minOf(from, segment.from / 60 * 60)
            to = maxOf(to, minOf(24 * 60, (segment.to + 59) / 60 * 60))
        }
        return from to to
    }

    /** Side by side where blocks overlap: each block's lane, and how many lanes the day needs. */
    fun lanes(segments: List<PlanSegment>): Pair<List<Int>, Int> {
        val ends = mutableListOf<Int>()
        val lane = IntArray(segments.size)
        for (i in segments.indices.sortedBy { segments[it].from }) {
            val free = ends.indexOfFirst { it <= segments[i].from }
            if (free >= 0) {
                lane[i] = free
                ends[free] = segments[i].to
            } else {
                lane[i] = ends.size
                ends.add(segments[i].to)
            }
        }
        return lane.toList() to maxOf(1, ends.size)
    }

    /** The next quarter of an hour from [now] (where "Plan…" starts a block). */
    fun nextQuarter(now: Instant, zone: ZoneId): Instant {
        val minutes = minutes(now, zone)
        val day = now.atZone(zone).toLocalDate()
        val next = (minutes / SNAP + 1) * SNAP
        return if (next >= 24 * 60) time(0, day.plusDays(1), zone) else time(next, day, zone)
    }

    /** "15 min", "1 h", "1 h 30 min". */
    fun length(minutes: Int): String =
        if (minutes < 60) "$minutes min" else if (minutes % 60 == 0) "${minutes / 60} h" else "${minutes / 60} h ${minutes % 60} min"

    /** "09:30". */
    fun hhmm(minutes: Int): String = "%02d:%02d".format(minutes / 60, minutes % 60)
}
