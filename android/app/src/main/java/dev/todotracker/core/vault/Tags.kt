package dev.todotracker.core.vault

import dev.todotracker.core.Energy
import dev.todotracker.core.Recurrence

/** The board's rules for values read from files (TodoTracker.Core.TaskBoard's Clean and Read helpers). */
internal object Values {
    const val MAX_TAG_LENGTH = 50
    const val MAX_ICON_LENGTH = 16
    const val DEFAULT_PLAN_MINUTES = 30
    const val MIN_PLAN_MINUTES = 5
    const val MAX_PLAN_MINUTES = 12 * 60

    private val tagPattern = Net.rx("^[\\p{L}\\p{N}_\\-/]+( [\\p{L}\\p{N}_\\-/]+)*$")
    private val spaces = Net.rx("[${Net.WS}]+")

    /** A tag as typed: no #, no spaces at the edges, one space between words. */
    fun cleanTag(raw: String?): String = spaces.matcher(Net.trim(Net.trim(raw ?: "").trimStart('#'))).replaceAll(" ")

    /** Words of letters, numbers, - _ and / with single spaces between them, not only digits. */
    fun isValidTag(tag: String): Boolean =
        tag.length in 1..MAX_TAG_LENGTH && tagPattern.matcher(tag).find() && !tag.all { it.isDigit() || it == '/' || it == ' ' }

    /** Tags read from a file: cleaned, invalid ones left out, each once (case doesn't matter). */
    fun readTags(tags: List<String?>): List<String> {
        val seen = HashSet<String>()
        return tags.map(::cleanTag).filter(::isValidTag).filter { seen.add(Net.ci(it)) }
    }

    /** An icon (an emoji or a few characters); one that isn't valid is left out. */
    fun icon(text: String?): String? {
        val clean = Net.trim(text ?: return null)
        if (clean.isEmpty()) return null
        return if (clean.length <= MAX_ICON_LENGTH && clean.none { Character.isISOControl(it) || it == ' ' || it == ' ' }) clean else null
    }

    /** A color as #rrggbb (lower case); one that isn't valid is left out. */
    fun color(text: String?): String? {
        val clean = Net.trim(text ?: return null)
        if (clean.isEmpty()) return null
        return if (clean.length == 7 && clean[0] == '#' && clean.drop(1).all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) clean.lowercase() else null
    }

    fun repeat(text: String?): Recurrence? = Recurrence.parseOrNull(text)

    fun planMinutes(minutes: Long?): Int? =
        if (minutes != null && minutes in MIN_PLAN_MINUTES..MAX_PLAN_MINUTES && minutes != DEFAULT_PLAN_MINUTES.toLong()) minutes.toInt() else null

    fun energy(level: Long?): Int? = if (level != null && level in Energy.LEVELS.first..Energy.LEVELS.last) level.toInt() else null
}
