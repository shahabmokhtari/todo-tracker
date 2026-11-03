package dev.onething.core

import java.time.DayOfWeek
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.ChronoUnit
import java.time.temporal.TemporalAdjusters

/** A snooze the apps offer: [id] is the same in every app (C# Snooze.Choices, the web app, Swift, this one). */
data class SnoozeChoice(val id: String, val label: String, val at: Instant)

/**
 * When a snoozed task comes back: the quick choices every app offers, and how a time is said. Mirrors
 * OneThing.Core.Snooze; tests/fixtures/snooze.json checks both. (On the phone a date and time picker does what
 * typed rules do on the desktop.)
 */
object Snooze {
    const val MORNING_HOUR = 9
    const val EVENING_HOUR = 18

    /** "This evening" is offered until this hour. */
    private const val EVENING_OFFERED_UNTIL = 17

    fun choices(now: Instant, zone: ZoneId): List<SnoozeChoice> {
        val local = now.atZone(zone)
        val today = local.toLocalDate()
        val choices = mutableListOf(
            SnoozeChoice("15m", "In 15 minutes", now.plus(15, ChronoUnit.MINUTES)),
            SnoozeChoice("1h", "In an hour", now.plus(1, ChronoUnit.HOURS)),
            SnoozeChoice("3h", "In 3 hours", now.plus(3, ChronoUnit.HOURS)),
        )
        if (local.hour < EVENING_OFFERED_UNTIL) choices.add(SnoozeChoice("evening", "This evening", at(today, EVENING_HOUR, zone)))
        choices.add(SnoozeChoice("tomorrow", "Tomorrow morning", QuickCaptureParser.tomorrowMorning(now, zone)))
        choices.add(SnoozeChoice("2d", "In 2 days", at(today.plusDays(2), MORNING_HOUR, zone)))
        choices.add(SnoozeChoice("monday", "Next Monday", at(today.with(TemporalAdjusters.next(DayOfWeek.MONDAY)), MORNING_HOUR, zone)))
        choices.add(SnoozeChoice("week", "In a week", at(today.plusDays(7), MORNING_HOUR, zone)))
        choices.add(SnoozeChoice("month", "In a month", at(today.plusMonths(1), MORNING_HOUR, zone)))
        return choices
    }

    private val weekdays = listOf("Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun")
    private val months = listOf("Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec")

    /** "today 17:00", "tomorrow 9:00", "Fri 14:30", "Mon 12 Jan, 9:00" (and the year when it's another one). */
    fun describe(at: Instant, now: Instant, zone: ZoneId): String {
        val local = at.atZone(zone)
        val today = now.atZone(zone).toLocalDate()
        val time = "${local.hour}:${local.minute.toString().padStart(2, '0')}"
        val weekday = weekdays[local.dayOfWeek.value - 1]
        return when (ChronoUnit.DAYS.between(today, local.toLocalDate())) {
            0L -> "today $time"
            1L -> "tomorrow $time"
            in 2L..6L -> "$weekday $time"
            else -> {
                val year = if (local.year == today.year) "" else " ${local.year}"
                "$weekday ${local.dayOfMonth} ${months[local.monthValue - 1]}$year, $time"
            }
        }
    }

    private fun at(day: LocalDate, hour: Int, zone: ZoneId): Instant = day.atTime(hour, 0).atZone(zone).toInstant()

    private fun at(day: LocalDate, hour: Int, minute: Int, zone: ZoneId): Instant = day.atTime(hour, minute).atZone(zone).toInstant()

    /** The time a typed rule means ("3d", "2 weeks", "fri 14:30", "next week", "weekend", "tonight", "9am"…), or null when it isn't understood (or is already past). */
    fun parse(rule: String?, now: Instant, zone: ZoneId): Instant? = read(rule, now, zone)?.takeIf { it.isAfter(now) }

    private val spaces = Regex("\\s+")
    private val shortSpan = Regex("^([0-9]{1,4}) ?(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)$")
    private val longSpan = Regex("^([0-9]{1,3}) ?(d|day|days|w|week|weeks|mo|month|months)$")
    private val clock = Regex("^([0-9]{1,2})(?::([0-9]{2}))? ?(am|pm)?$")
    private val isoDate = Regex("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")

    /** What a rule says, even when that's already past (null: not understood). Snooze.Read in C#. */
    private fun read(rule: String?, now: Instant, zone: ZoneId): Instant? {
        var text = spaces.replace((rule ?: "").trim().lowercase(), " ")
        if (text.startsWith("in ")) text = text.substring(3)
        val leadingAt = text.startsWith("at ")
        if (leadingAt) text = text.substring(3)
        if (text.isEmpty()) return null

        // Spans count from now (as @3d always has): 45m, 2h, 3d, 2 weeks, 1 month.
        shortSpan.matchEntire(text)?.let { m ->
            val amount = m.groupValues[1].toInt()
            if (amount in 1 until 10_000) return if (m.groupValues[2][0] == 'h') now.plus(amount.toLong(), ChronoUnit.HOURS) else now.plus(amount.toLong(), ChronoUnit.MINUTES)
        }
        longSpan.matchEntire(text)?.let { m ->
            val count = m.groupValues[1].toInt()
            if (count in 1 until 1000) {
                return when (m.groupValues[2]) {
                    "d", "day", "days" -> now.plus(count.toLong(), ChronoUnit.DAYS)
                    "w", "week", "weeks" -> now.plus(7L * count, ChronoUnit.DAYS)
                    // A month later at the same offset, as DateTimeOffset.AddMonths does.
                    else -> java.time.OffsetDateTime.ofInstant(now, zone).plusMonths(count.toLong()).toInstant()
                }
            }
        }

        // "<day> <time>", "<day> at <time>", "<day>", or "<time>".
        var dayPart: String? = text
        var time: java.time.LocalTime? = null
        val atWord = text.lastIndexOf(" at ")
        val lastSpace = text.lastIndexOf(' ')
        val whole = if (leadingAt) atTime(text, evening = false) else readTime(text)
        if (whole != null) {
            time = whole
            dayPart = null
        } else if (atWord > 0) {
            dayPart = text.substring(0, atWord)
            time = atTime(text.substring(atWord + 4), evening = dayPart == "tonight" || dayPart == "this evening" || dayPart == "evening") ?: return null
        } else if (lastSpace > 0) {
            readTime(text.substring(lastSpace + 1))?.let {
                time = it
                dayPart = text.substring(0, lastSpace)
            }
        }

        val today = now.atZone(zone).toLocalDate()
        val part = dayPart
        if (part == null) {
            // Just a time: today, or tomorrow when it's already past.
            val t = time!!
            val result = at(today, t.hour, t.minute, zone)
            return if (!result.isAfter(now)) at(today.plusDays(1), t.hour, t.minute, zone) else result
        }
        val day = day(part, today) ?: return null
        val given = time
        // "mon 14:00" on a Monday morning means this afternoon (just "mon" means next week's).
        if (given != null && day.weekday && day.date == today.plusDays(7) && at(today, given.hour, given.minute, zone).isAfter(now)) {
            return at(today, given.hour, given.minute, zone)
        }
        return at(day.date, given?.hour ?: if (day.evening) EVENING_HOUR else MORNING_HOUR, given?.minute ?: 0, zone)
    }

    /**
     * A time after "at", the way people say it: a bare hour is a time ("at 9"); without am/pm, 1 to 7 o'clock is the
     * afternoon ("tomorrow at 3" isn't 3 in the night), and in the evening every hour is ("tonight at 9" is 21:00).
     */
    private fun atTime(text: String, evening: Boolean): java.time.LocalTime? {
        val time = readTime(text, bareHour = true) ?: return null
        if (text.endsWith("am") || text.endsWith("pm")) return time
        val afternoon = if (evening) time.hour in 1..11 else !text.contains(':') && time.hour in 1..7
        return if (afternoon) time.plusHours(12) else time
    }

    private class Day(val date: LocalDate, val evening: Boolean, val weekday: Boolean)

    /** The day a word means, whether it's "tonight", and whether it's a weekday's name; relative to [today]. */
    private fun day(text: String, today: LocalDate): Day? {
        when (text) {
            "today" -> return Day(today, false, false)
            "tonight", "this evening", "evening" -> return Day(today, true, false)
            "tomorrow", "tmrw" -> return Day(today.plusDays(1), false, false)
            "weekend", "this weekend", "the weekend" -> return Day(next(today, DayOfWeek.SATURDAY), false, false)
            "next week" -> return Day(next(today, DayOfWeek.MONDAY), false, false)
            "next month" -> return Day(today.withDayOfMonth(1).plusMonths(1), false, false)
        }
        val name = if (text.startsWith("next ")) text.substring(5) else text
        weekday(name)?.let { return Day(next(today, it), false, name == text) }
        if (!isoDate.matches(text)) return null
        return runCatching { LocalDate.parse(text) }.getOrNull()?.let { Day(it, false, false) }
    }

    // In .NET's order (Sunday first): "s" isn't a day, and "sun"/"sunday" is.
    private val dayNames = listOf(
        DayOfWeek.SUNDAY, DayOfWeek.MONDAY, DayOfWeek.TUESDAY, DayOfWeek.WEDNESDAY, DayOfWeek.THURSDAY, DayOfWeek.FRIDAY, DayOfWeek.SATURDAY,
    )

    private fun weekday(name: String): DayOfWeek? = dayNames.firstOrNull { day ->
        val full = day.name.lowercase()
        name == full || (name.length >= 3 && full.startsWith(name))
    }

    /** "14:30", "9:00", "2pm", "9am", "9:30pm". */
    private fun readTime(text: String, bareHour: Boolean = false): java.time.LocalTime? {
        val m = clock.matchEntire(text) ?: return null
        var hour = m.groupValues[1].toInt()
        val hasMinute = m.groups[2] != null
        val minute = if (hasMinute) m.groupValues[2].toInt() else 0
        val half = m.groupValues[3]
        if (half.isNotEmpty()) {
            if (hour !in 1..12) return null
            hour = hour % 12 + if (half == "pm") 12 else 0
        } else if (!hasMinute && !bareHour) {
            // A bare number isn't a time ("3" could be anything).
            return null
        }
        return if (hour < 24 && minute < 60) java.time.LocalTime.of(hour, minute) else null
    }

    /** The next such weekday after today (a week from today when today is that day). */
    private fun next(today: LocalDate, day: DayOfWeek): LocalDate = today.with(TemporalAdjusters.next(day))
}
