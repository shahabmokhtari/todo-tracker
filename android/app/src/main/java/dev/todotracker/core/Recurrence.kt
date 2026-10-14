package dev.todotracker.core

import java.time.DayOfWeek
import java.time.LocalDate
import java.time.Month
import java.time.format.DateTimeFormatter
import java.time.format.DateTimeParseException
import java.util.Locale

enum class RepeatUnit { DAY, WEEK, MONTH, YEAR }

/**
 * How a task repeats, written like the Obsidian Tasks plugin's 🔁 rules ("every weekday", "every 2 weeks on Monday,
 * Thursday", "every month on the last Friday", "every year on March 5", + "when done", "until 2026-12-31",
 * "for 5 times"). Mirrors TodoTracker.Core.Recurrence (C#) and Recurrence (Swift); tests/fixtures/repeat.json keeps
 * them the same. [toString] is the canonical text.
 */
data class Recurrence(
    val unit: RepeatUnit,
    val interval: Int = 1,
    /** Weekly: the days it falls on, Monday first (none: the day of the week it started on). */
    val weekdays: List<DayOfWeek> = emptyList(),
    /** Monthly or yearly: the day of the month (1–31; a short month uses its last day), or -1 for the last day. */
    val monthDay: Int? = null,
    /** Monthly: which [nthDay] of the month (1–4, or -1 for the last one). */
    val nth: Int? = null,
    val nthDay: DayOfWeek? = null,
    /** Yearly: the month (1–12), with [monthDay]. */
    val month: Int? = null,
    val whenDone: Boolean = false,
    val until: LocalDate? = null,
    /** Occurrences left, the current one included (1: this is the last time). */
    val count: Int? = null,
) {
    fun countedDown(): Recurrence = if (count != null) copy(count = count - 1) else this

    fun countedUp(): Recurrence = if (count != null) copy(count = minOf(count + 1, MAX_COUNT)) else this

    /** The first day after [after] it falls on, counting from [anchor]; null when the rule has ended ([until]). */
    fun next(anchor: LocalDate, after: LocalDate): LocalDate? {
        var day = after.plusDays(1)
        val last = after.plusDays(limit())
        while (!day.isAfter(last)) {
            if (matches(day, anchor)) return if (until != null && day.isAfter(until)) null else day
            day = day.plusDays(1)
        }
        return null
    }

    /** The last day before [before] it falls on (to undo an occurrence), counting from [anchor]. */
    fun previous(anchor: LocalDate, before: LocalDate): LocalDate? {
        var day = before.minusDays(1)
        val first = before.minusDays(limit())
        while (!day.isBefore(first)) {
            if (matches(day, anchor)) return day
            day = day.minusDays(1)
        }
        return null
    }

    private fun limit(): Long = when (unit) {
        RepeatUnit.DAY -> interval.toLong()
        RepeatUnit.WEEK -> 7L * interval + 7
        RepeatUnit.MONTH -> 31L * interval + 31
        RepeatUnit.YEAR -> 366L * interval + 366
    }

    private fun matches(day: LocalDate, anchor: LocalDate): Boolean = when (unit) {
        RepeatUnit.DAY -> Math.floorMod(day.toEpochDay() - anchor.toEpochDay(), interval.toLong()) == 0L
        RepeatUnit.WEEK -> {
            val days = weekdays.ifEmpty { listOf(anchor.dayOfWeek) }
            // Epoch day 0 (1 Jan 1970) is a Thursday: +3 makes weeks start on Monday.
            day.dayOfWeek in days && Math.floorMod(Math.floorDiv(day.toEpochDay() + 3, 7L) - Math.floorDiv(anchor.toEpochDay() + 3, 7L), interval.toLong()) == 0L
        }
        RepeatUnit.MONTH -> Math.floorMod((day.year * 12 + day.monthValue) - (anchor.year * 12 + anchor.monthValue), interval) == 0 && dayMatches(day, anchor)
        RepeatUnit.YEAR -> Math.floorMod(day.year - anchor.year, interval) == 0 && day.monthValue == (month ?: anchor.monthValue) && dayMatches(day, anchor)
    }

    private fun dayMatches(day: LocalDate, anchor: LocalDate): Boolean {
        val length = day.lengthOfMonth()
        if (nth != null && nthDay != null) {
            return day.dayOfWeek == nthDay && if (nth == -1) day.dayOfMonth + 7 > length else (day.dayOfMonth - 1) / 7 + 1 == nth
        }
        val wanted = monthDay ?: anchor.dayOfMonth
        return day.dayOfMonth == if (wanted == -1) length else minOf(wanted, length)
    }

    override fun toString(): String {
        val unitName = unit.name.lowercase(Locale.ROOT)
        val weekday = unit == RepeatUnit.WEEK && interval == 1 && weekdays == WORK_WEEK
        var text = when {
            weekday -> "every weekday"
            interval == 1 -> "every $unitName"
            else -> "every $interval ${unitName}s"
        }
        text += when {
            unit == RepeatUnit.WEEK && weekdays.isNotEmpty() && !weekday -> " on " + weekdays.joinToString(", ") { dayName(it) }
            nth != null && nthDay != null -> " on the ${if (nth == -1) "last" else ordinal(nth)} ${dayName(nthDay)}"
            unit == RepeatUnit.YEAR && month != null -> " on ${monthName(month)} ${if (monthDay == -1) "last day" else monthDay}"
            monthDay != null -> if (monthDay == -1) " on the last day" else " on the ${ordinal(monthDay)}"
            else -> ""
        }
        if (whenDone) text += " when done"
        if (until != null) text += " until " + until.format(DateTimeFormatter.ISO_LOCAL_DATE)
        if (count != null) text += if (count == 1) " for 1 time" else " for $count times"
        return text
    }

    class RuleException(message: String) : IllegalArgumentException(message)

    companion object {
        const val MAX_INTERVAL = 999
        const val MAX_COUNT = 9999

        private val WORK_WEEK = listOf(DayOfWeek.MONDAY, DayOfWeek.TUESDAY, DayOfWeek.WEDNESDAY, DayOfWeek.THURSDAY, DayOfWeek.FRIDAY)

        private fun dayName(d: DayOfWeek) = d.name.lowercase(Locale.ROOT).replaceFirstChar { it.uppercase() }

        private fun monthName(m: Int) = Month.of(m).name.lowercase(Locale.ROOT).replaceFirstChar { it.uppercase() }

        private fun ordinal(n: Int): String = n.toString() + if (n % 100 in 11..13) "th" else when (n % 10) {
            1 -> "st"
            2 -> "nd"
            3 -> "rd"
            else -> "th"
        }

        /** The rule [text] means; throws [RuleException] (with examples) when it isn't understood. */
        fun parse(text: String?): Recurrence = try {
            read(text)
        } catch (e: RuleException) {
            throw RuleException(
                "${e.message} Try \"every day\", \"every weekday\", \"every 2 weeks on Monday, Thursday\", \"every month on the last Friday\" " +
                    "or \"every year on March 5\" (add \"when done\", \"until 2026-12-31\" or \"for 5 times\").",
            )
        }

        /** A rule written by hand (or by another app) that isn't understood is left out. */
        fun parseOrNull(text: String?): Recurrence? = try {
            read(text)
        } catch (e: RuleException) {
            null
        }

        private fun fail(message: String): Nothing = throw RuleException(message)

        private fun number(word: String): Int? = if (word.isNotEmpty() && word.all { it in '0'..'9' }) word.toIntOrNull() else null

        private fun read(text: String?): Recurrence {
            val clean = (text ?: "").replace("🔁", " ").replace("\uFE0F", "").replace(",", " ").trim().lowercase(Locale.ROOT)
            val words = clean.split(Regex("\\s+")).filter { it.isNotEmpty() && it != "and" && it != "&" }.toMutableList()
            if (words.isEmpty()) fail("Say how it repeats.")
            var whenDone = false
            var until: LocalDate? = null
            var count: Int? = null
            while (words.size > 1) {
                val lastWord = words[words.size - 1]
                val before = words[words.size - 2]
                if (before == "when" && lastWord == "done") {
                    whenDone = true
                    repeat(2) { words.removeAt(words.size - 1) }
                } else if (words.size > 2 && before == "until") {
                    until = try {
                        LocalDate.parse(lastWord, DateTimeFormatter.ISO_LOCAL_DATE)
                    } catch (e: DateTimeParseException) {
                        fail("\"$lastWord\" isn't a date (write it like 2026-12-31).")
                    }
                    repeat(2) { words.removeAt(words.size - 1) }
                } else if ((lastWord == "times" || lastWord == "time") && number(before) != null) {
                    val n = number(before)!!
                    count = if (n in 1..MAX_COUNT) n else fail("It can repeat 1 to $MAX_COUNT times.")
                    repeat(2) { words.removeAt(words.size - 1) }
                    if (words.size > 1 && words[words.size - 1] == "for") words.removeAt(words.size - 1)
                } else {
                    break
                }
            }
            return readRule(words).copy(whenDone = whenDone, until = until, count = count)
        }

        private fun readRule(words: List<String>): Recurrence {
            var at = 1
            fun peek() = words.getOrElse(at) { "" }
            var interval = 1
            val named = when (words[0]) {
                "daily" -> RepeatUnit.DAY
                "weekly" -> RepeatUnit.WEEK
                "monthly" -> RepeatUnit.MONTH
                "yearly", "annually" -> RepeatUnit.YEAR
                else -> null
            }
            val weekdays = mutableListOf<DayOfWeek>()
            val unit: RepeatUnit
            if (named != null) {
                unit = named
            } else {
                if (words[0] != "every") fail("\"${words.joinToString(" ")}\" isn't a repeat rule.")
                if (peek() == "other") {
                    interval = 2
                    at++
                } else {
                    val n = number(peek())
                    if (n != null) {
                        interval = if (n in 1..MAX_INTERVAL) n else fail("Repeat every 1 to $MAX_INTERVAL.")
                        at++
                    }
                }
                val word = peek()
                at++
                val first = weekdayOf(word)
                if ((word == "weekday" || word == "weekdays") && interval == 1) {
                    unit = RepeatUnit.WEEK
                    weekdays.addAll(WORK_WEEK)
                } else if (first != null) {
                    // "every Monday", "every Tuesday and Friday"
                    unit = RepeatUnit.WEEK
                    weekdays.add(first)
                    while (true) {
                        val more = weekdayOf(peek()) ?: break
                        weekdays.add(more)
                        at++
                    }
                } else {
                    unit = when (word.trimEnd('s')) {
                        "day" -> RepeatUnit.DAY
                        "week" -> RepeatUnit.WEEK
                        "month" -> RepeatUnit.MONTH
                        "year" -> RepeatUnit.YEAR
                        else -> fail(if (word.isEmpty()) "Every what?" else "\"$word\" isn't a day, week, month or year.")
                    }
                }
            }

            var monthDay: Int? = null
            var nth: Int? = null
            var month: Int? = null
            var nthDay: DayOfWeek? = null
            if (peek() == "on" && weekdays.isEmpty()) {
                at++
                if (peek() == "the") at++
                when (unit) {
                    RepeatUnit.WEEK -> {
                        while (true) {
                            val day = weekdayOf(peek()) ?: break
                            weekdays.add(day)
                            at++
                        }
                        if (weekdays.isEmpty()) fail("On which days of the week?")
                    }
                    RepeatUnit.MONTH -> {
                        val which = if (peek() == "last") -1 else dayNumber(peek())
                        at++
                        if (which == null) fail("On which day of the month (\"the 15th\", \"the last day\", \"the 2nd Tuesday\")?")
                        val weekday = weekdayOf(peek())
                        if (weekday != null) {
                            nth = if (which == -1 || which in 1..4) which else fail("Say the 1st to 4th or the last weekday of the month.")
                            nthDay = weekday
                            at++
                        } else {
                            if (peek() == "day") at++
                            monthDay = if (which == -1 || which in 1..31) which else fail("A day of the month is 1 to 31.")
                        }
                    }
                    RepeatUnit.YEAR -> {
                        // "March 5", "Mar 5th", "5 March", "the 5th of March"
                        val next = words.getOrElse(at + 1) { "" }
                        val of = next == "of"
                        val m1 = monthOf(peek())
                        val m = m1 ?: monthOf(if (of) words.getOrElse(at + 2) { "" } else next)
                        val d = if (m1 != null) dayNumber(next) else dayNumber(peek())
                        if (m == null || d == null || d < 1 || d > Month.of(m).length(true)) fail("On which day of the year (\"March 5\")?")
                        at += if (of) 3 else 2
                        month = m
                        monthDay = d
                    }
                    else -> fail("\"every day\" has no \"on\".")
                }
            }
            if (at < words.size) fail("\"${words.drop(at).joinToString(" ")}\" isn't understood.")
            return Recurrence(
                unit = unit,
                interval = interval,
                weekdays = weekdays.distinct().sortedBy { it.value },
                monthDay = monthDay,
                nth = nth,
                nthDay = nthDay,
                month = month,
            )
        }

        private fun weekdayOf(word: String): DayOfWeek? {
            val w = word.trimEnd('s')
            if (w.length < 2) return null
            return DayOfWeek.entries.firstOrNull { day ->
                val name = day.name.lowercase(Locale.ROOT)
                w == name || (w.length >= 3 && name.startsWith(w)) || ((w == "tu" || w == "th") && name.startsWith(w))
            }
        }

        private fun monthOf(word: String): Int? {
            if (word.length < 3) return null
            return Month.entries.firstOrNull { it.name.lowercase(Locale.ROOT).startsWith(word) }?.value
        }

        /** "15", "15th", "1st", "2nd", "3rd". */
        private fun dayNumber(word: String): Int? {
            val digits = if (word.length > 2 && word.takeLast(2) in setOf("st", "nd", "rd", "th")) word.dropLast(2) else word
            return number(digits)
        }
    }
}
