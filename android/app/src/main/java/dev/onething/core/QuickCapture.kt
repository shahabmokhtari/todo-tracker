package dev.onething.core

import dev.onething.core.vault.Values
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZonedDateTime

data class QuickCapture(val title: String, val priority: Priority, val nextActionAt: Instant?, val deadline: Instant?, val tags: List<String> = emptyList())

/**
 * One-line capture, the same as everywhere else: `!`/`!!`/`!low` priority, `@15m`/`@2h`/`@1d`/`@tomorrow` defer (and
 * anything "Pick a time…" understands: `@fri`, `@2w`, `@weekend`, `@14:00`, `@next-week`), `due:today`/`due:tomorrow`/
 * `due:3d`/`due:2026-02-01` deadline, `#tag` and `#"deep work"` tags (words like `#123` that can't be tags stay in the
 * title, as in Obsidian). QuickCaptureParser in C# (tests/fixtures/quick-capture.json).
 */
object QuickCaptureParser {
    private const val MORNING_HOUR = 9
    private const val END_OF_DAY_HOUR = 17

    // #"deep work": a tag of several words, in straight or curly quotes (phones curl them as you type).
    private val quotedTag = Regex("(?<=^|\\s)#[\"\u201C\u201D]([^\"\u201C\u201D]+)[\"\u201C\u201D]")

    fun parse(input: String, now: Instant, zone: ZoneId = ZoneId.systemDefault()): QuickCapture {
        var priority = Priority.NORMAL
        var nextAction: Instant? = null
        var deadline: Instant? = null
        val words = mutableListOf<String>()
        val tags = mutableListOf<String>()
        fun addTag(tag: String) {
            if (tags.none { it.equals(tag, ignoreCase = true) }) tags.add(tag)
        }
        val text = quotedTag.replace(input) { m ->
            val tag = Values.cleanTag(m.groupValues[1])
            if (!Values.isValidTag(tag)) {
                m.value
            } else {
                addTag(tag)
                " "
            }
        }
        for (token in text.split(' ').map { it.trim() }.filter { it.isNotEmpty() }) {
            val p = parsePriority(token)
            val at = if (p == null) parseDefer(token, now, zone) else null
            val due = if (p == null && at == null) parseDue(token, now, zone) else null
            when {
                p != null -> priority = p
                at != null -> nextAction = at
                due != null -> deadline = due
                token.length > 1 && token[0] == '#' && token[1] != '#' && Values.isValidTag(token.substring(1)) -> addTag(token.substring(1))
                else -> words.add(token)
            }
        }
        if (words.isEmpty()) throw BoardException("Type a task title.")
        return QuickCapture(words.joinToString(" "), priority, nextAction, deadline, tags)
    }

    fun parsePriority(token: String): Priority? = when (token.lowercase()) {
        "!", "!high" -> Priority.HIGH
        "!!", "!!!", "!critical", "!urgent" -> Priority.CRITICAL
        "!low" -> Priority.LOW
        "!normal" -> Priority.NORMAL
        else -> null
    }

    private fun parseDefer(token: String, now: Instant, zone: ZoneId): Instant? {
        if (!token.startsWith("@")) return null
        val value = token.drop(1).lowercase()
        if (value == "tomorrow") return tomorrowMorning(now, zone)
        // @15m, @2h, @3d count from now (as always); anything else as in "Pick a time…": @fri, @2w, @weekend, @14:00,
        // and words with spaces written with hyphens (@next-week, @tomorrow-14:00).
        return parseSpanSeconds(value)?.let { now.plusSeconds(it) } ?: Snooze.parse(value, now, zone) ?: Snooze.parse(value.replace('-', ' '), now, zone)
    }

    /** The next 9:00. Before 4:00 people still mean "when I wake up", i.e. this morning (same as C# and Swift). */
    fun tomorrowMorning(now: Instant, zone: ZoneId): Instant {
        val hour = now.atZone(zone).hour
        return localTime(now, zone, if (hour < 4) 0 else 1, MORNING_HOUR)
    }

    private fun parseDue(token: String, now: Instant, zone: ZoneId): Instant? {
        if (!token.lowercase().startsWith("due:")) return null
        val value = token.drop(4).lowercase()
        when (value) {
            "today" -> {
                val endOfDay = localTime(now, zone, 0, END_OF_DAY_HOUR)
                return if (endOfDay.isAfter(now)) endOfDay else localTime(now, zone, 0, 23).plusSeconds(59 * 60)
            }
            "tomorrow" -> return localTime(now, zone, 1, END_OF_DAY_HOUR)
        }
        if (value.endsWith("d")) {
            val days = strictInt(value.dropLast(1))
            if (days != null && days in 1..999) return localTime(now, zone, days, END_OF_DAY_HOUR)
        }
        val parts = value.split("-")
        if (parts.size == 3 && parts[0].length == 4 && parts[1].length == 2 && parts[2].length == 2) {
            val y = digitsInt(parts[0]) ?: return null
            val m = digitsInt(parts[1]) ?: return null
            val d = digitsInt(parts[2]) ?: return null
            // Dates that don't exist (month 13, Feb 30) are words, as in C# and Swift.
            val date = runCatching { LocalDate.of(y, m, d) }.getOrNull() ?: return null
            return date.atTime(END_OF_DAY_HOUR, 0).atZone(zone).toInstant()
        }
        return null
    }

    /** Digits only: no sign, no leading zero. */
    private fun strictInt(text: String): Int? = if (text.isEmpty() || text[0] == '0') null else digitsInt(text)

    /** ASCII digits only (leading zeros allowed, as in "02"). */
    private fun digitsInt(text: String): Int? =
        if (text.isEmpty() || text.length > 9 || !text.all { it in '0'..'9' }) null else text.toInt()

    private fun parseSpanSeconds(value: String): Long? {
        val unit = value.lastOrNull() ?: return null
        if (unit !in "mhd") return null
        val amount = strictInt(value.dropLast(1)) ?: return null
        if (amount >= 10_000) return null
        return when (unit) {
            'm' -> amount * 60L
            'h' -> amount * 3600L
            else -> amount * 86_400L
        }
    }

    private fun localTime(now: Instant, zone: ZoneId, addDays: Int, hour: Int): Instant {
        val day = now.atZone(zone).toLocalDate().plusDays(addDays.toLong())
        return ZonedDateTime.of(day.atTime(hour, 0), zone).toInstant()
    }
}
