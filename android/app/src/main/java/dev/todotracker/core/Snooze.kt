package dev.todotracker.core

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
 * TodoTracker.Core.Snooze; tests/fixtures/snooze.json checks both. (On the phone a date and time picker does what
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
}
