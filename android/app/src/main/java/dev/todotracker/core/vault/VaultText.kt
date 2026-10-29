package dev.todotracker.core.vault

import dev.todotracker.core.Actor
import dev.todotracker.core.ActorKind
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale
import java.util.UUID
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonPrimitive

/** A task file that can't be understood safely; the store keeps the last good version and never writes it. */
class VaultFormatException(message: String, cause: Throwable? = null) : java.io.IOException(message, cause)

/** .NET's FormatException: a value that isn't a date or duration (turned into a [VaultFormatException] by the parser). */
internal class NetFormatException(message: String) : IllegalArgumentException(message)

/**
 * How dates are shown (the local time zone), what "now" is (for times the file doesn't have yet), the file's folder
 * in the vault (to resolve relative attachment links), and the seed (the file's path) that makes ids for hand-written
 * lines stable across reads, so files are never rewritten just to add ids.
 */
data class TaskMarkdownContext(val zone: ZoneId, val now: Instant, val fileDirectory: String, val idSeed: String? = null)

/** Shared formatting of the tasks folder (dates, durations, YAML scalars, hidden JSON), as TodoTracker.Core.Vault.VaultText. */
internal object VaultText {
    const val DEFAULT_DUE_TIME = "17:00"
    const val DEFAULT_SCHEDULED_TIME = "09:00"
    const val DEFAULT_DONE_TIME = "12:00"

    private val utcFormat = DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm:ss.SSS'Z'", Locale.ROOT).withZone(ZoneOffset.UTC)
    private val minuteFormat = DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm", Locale.ROOT)
    private val secondFormat = DateTimeFormatter.ofPattern("uuuu-MM-dd'T'HH:mm:ss", Locale.ROOT)
    private val dateFormat = DateTimeFormatter.ofPattern("uuuu-MM-dd", Locale.ROOT)
    private val shownMinute = DateTimeFormatter.ofPattern("uuuu-MM-dd HH:mm", Locale.ROOT)

    /** UTC with milliseconds: 2026-01-05T09:00:00.000Z. */
    fun utc(at: Instant): String = utcFormat.format(at)

    fun toLocal(at: Instant, zone: ZoneId): LocalDateTime = LocalDateTime.ofInstant(at, zone)

    /** Local date-time the way Obsidian's date & time property writes it (seconds only when needed). */
    fun localDateTime(at: Instant, zone: ZoneId): String {
        val local = toLocal(at, zone)
        return (if (local.second == 0 && local.nano / 1_000_000 == 0) minuteFormat else secondFormat).format(local)
    }

    fun localDate(at: Instant, zone: ZoneId): String = dateFormat.format(toLocal(at, zone))

    fun localMinute(at: Instant, zone: ZoneId): String = shownMinute.format(toLocal(at, zone))

    fun localMinute(local: LocalDateTime): String = shownMinute.format(local)

    /**
     * The instant of a local time as .NET's `TimeZoneInfo.GetUtcOffset(local)` places it: a time in the hour that's
     * skipped, or repeated, gets the standard offset (java.time would shift the first and take summer time for the second).
     */
    fun atLocal(local: LocalDateTime, zone: ZoneId): Instant {
        val rules = zone.rules
        val offsets = rules.getValidOffsets(local)
        if (offsets.size == 1) return local.toInstant(offsets[0])
        val transition = rules.getTransition(local)
        val standard = if (transition != null) rules.getStandardOffset(transition.instant) else rules.getStandardOffset(local.toInstant(ZoneOffset.UTC))
        return local.toInstant(standard)
    }

    fun atLocal(date: LocalDate, time: String, zone: ZoneId): Instant =
        atLocal(LocalDateTime.of(date, LocalTime.of(time.substring(0, 2).toInt(), time.substring(3, 5).toInt())), zone)

    private val dateOnly = Regex("[0-9]{4}-[0-9]{2}-[0-9]{2}")

    /** DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd"). */
    fun parseDate(text: String?): LocalDate? {
        val value = Net.trim(text ?: return null)
        if (!dateOnly.matches(value)) return null
        return try {
            LocalDate.of(value.substring(0, 4).toInt(), value.substring(5, 7).toInt(), value.substring(8, 10).toInt())
        } catch (e: java.time.DateTimeException) {
            null
        }
    }

    private val hasZone = Net.rx("(Z|[+-]\\p{Nd}{2}:?\\p{Nd}{2})$", ignoreCase = true)

    private val localFormats = listOf(
        Regex("([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2})"),
        Regex("([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})"),
        Regex("([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})\\.([0-9]{3})"),
        Regex("([0-9]{4})-([0-9]{2})-([0-9]{2}) ([0-9]{2}):([0-9]{2})"),
        Regex("([0-9]{4})-([0-9]{2})-([0-9]{2}) ([0-9]{2}):([0-9]{2}):([0-9]{2})"),
    )

    /** An ISO instant (with Z or an offset), a local date-time, or a date (at [defaultTime]). */
    fun parseTime(text: String?, zone: ZoneId, defaultTime: String): Instant? {
        val value = Net.trim(text ?: return null).trim('"', '\'')
        if (value.isEmpty()) return null
        if (hasZone.matcher(value).find()) parseOffset(value, assumeUtc = true)?.let { return it }
        parseDate(value)?.let { return atLocal(it, defaultTime, zone) }
        for (format in localFormats) {
            val m = format.matchEntire(value) ?: continue
            val g = m.groupValues
            val local = try {
                LocalDateTime.of(g[1].toInt(), g[2].toInt(), g[3].toInt(), g[4].toInt(), g[5].toInt(), g.getOrNull(6)?.toInt() ?: 0, (g.getOrNull(7)?.toInt() ?: 0) * 1_000_000)
            } catch (e: java.time.DateTimeException) {
                break
            }
            return atLocal(local, zone)
        }
        throw NetFormatException("\"$text\" isn't a date (use 2026-01-31, 2026-01-31T17:00, or an ISO time).")
    }

    private val instantPattern = Regex(
        "([0-9]{4})-([0-9]{1,2})-([0-9]{1,2})(?:[Tt ]([0-9]{1,2}):([0-9]{2})(?::([0-9]{2})(?:\\.([0-9]{1,7}))?)?)? *([Zz]|[+-][0-9]{2}(?::?[0-9]{2})?)?",
    )

    /** DateTimeOffset.TryParse(text, Invariant, AssumeUniversal) for the ISO forms apps write. */
    fun parseOffset(text: String, assumeUtc: Boolean): Instant? {
        val m = instantPattern.matchEntire(Net.trim(text)) ?: return null
        val g = m.groupValues
        return try {
            val fraction = g[7].padEnd(9, '0').toInt()
            val local = LocalDateTime.of(g[1].toInt(), g[2].toInt(), g[3].toInt(), g[4].ifEmpty { "0" }.toInt(), g[5].ifEmpty { "0" }.toInt(), g[6].ifEmpty { "0" }.toInt(), fraction)
            val zone = g[8]
            val offset = when {
                zone.isEmpty() -> if (assumeUtc) ZoneOffset.UTC else return null
                zone == "Z" || zone == "z" -> ZoneOffset.UTC
                else -> {
                    val digits = zone.substring(1).replace(":", "")
                    val hours = digits.substring(0, 2).toInt()
                    val minutes = if (digits.length >= 4) digits.substring(2, 4).toInt() else 0
                    val total = hours * 3600 + minutes * 60
                    ZoneOffset.ofTotalSeconds(if (zone[0] == '-') -total else total)
                }
            }
            local.toInstant(offset)
        } catch (e: java.time.DateTimeException) {
            null
        }
    }

    /** Exact instants written by the app (hidden data); null when missing or unreadable. */
    fun parseInstant(text: String?): Instant? = if (Net.isBlank(text)) null else parseOffset(text!!, assumeUtc = true)

    private fun roundMinutes(d: Duration): Long = Math.rint(d.toNanos() / 60e9).toLong()

    /** 90m, 24h, 1d. */
    fun duration(d: Duration): String {
        val minutes = roundMinutes(d)
        return if (minutes % 1440 == 0L) "${minutes / 1440}d" else if (minutes % 60 == 0L) "${minutes / 60}h" else "${minutes}m"
    }

    private val durationPart = Net.rx("(\\p{Nd}+)[${Net.WS}]*([dhm])", ignoreCase = true)
    private val plainNumber = Regex("[${Net.WS}]*[+-]?[0-9]+[${Net.WS}]*")

    fun parseDuration(text: String?): Duration? {
        val value = Net.trim(text ?: return null)
        if (value.isEmpty()) return null
        if (plainNumber.matches(value)) value.trim().toLongOrNull()?.let { return Duration.ofMinutes(it) }
        val m = durationPart.matcher(value)
        val parts = mutableListOf<Pair<Long, String>>()
        val matched = StringBuilder()
        while (m.find()) {
            matched.append(m.group().replace(" ", ""))
            parts.add(m.group(1)!!.toLongOrNull()?.let { it to m.group(2)!! } ?: throw NetFormatException("\"$text\" isn't a duration (use 90m, 24h, or 1d)."))
        }
        if (parts.isEmpty() || matched.length != value.replace(" ", "").length) throw NetFormatException("\"$text\" isn't a duration (use 90m, 24h, or 1d).")
        var total = Duration.ZERO
        for ((n, unit) in parts) {
            total = total.plus(
                when (Net.lower(unit)) {
                    "d" -> Duration.ofDays(n)
                    "h" -> Duration.ofHours(n)
                    else -> Duration.ofMinutes(n)
                },
            )
        }
        return total
    }

    /** "25 min", or "2 h 05 min". */
    fun durationLabel(d: Duration): String {
        val minutes = roundMinutes(d).toInt()
        return if (minutes < 60) "$minutes min" else "${minutes / 60} h ${String.format(Locale.ROOT, "%02d", minutes % 60)} min"
    }

    private val safePlain = Net.rx("^[\\p{L}\\p{N}][\\p{L}\\p{N} _./+\\-]*$")
    private val looksTyped = Net.rx("^(true|false|yes|no|on|off|null|~|[-+]?[\\p{Nd}._]+([eE][-+]?\\p{Nd}+)?|\\p{Nd}{4}-\\p{Nd}{2}-\\p{Nd}{2}.*)$", ignoreCase = true)

    /** A YAML scalar: plain when unambiguous, otherwise a double-quoted (JSON-compatible) string. */
    fun yamlScalar(value: String): String =
        if (safePlain.matcher(value).find() && !looksTyped.matcher(value).find()) value else NetJson.string(value, JsonEscaping.RELAXED)

    /** Compact JSON for %%…%% comments; a literal % is escaped so the comment can't end early. */
    fun hidden(value: JsonElement): String = NetJson.write(value, JsonEscaping.RELAXED).replace("%", "\\u0025")

    fun hidden(value: String): String = hidden(JsonPrimitive(value))

    /** Who an author line names: "You", "Agent: claude", "Edited in files"… */
    fun parseActor(text: String?): Actor {
        val value = Net.trim(text ?: "")
        if (value.isEmpty() || Net.equalsCi(value, "You")) return Actor.USER
        if (Net.equalsCi(value, "Edited in files")) return Actor(ActorKind.VAULT)
        val colon = value.indexOf(':')
        val kindText = if (colon < 0) value else value.substring(0, colon)
        val kind = ActorKind.entries.firstOrNull { Net.equalsCi(it.wire, Net.trim(kindText)) }
        if (kind != null) {
            val name = if (colon < 0) null else Net.trim(value.substring(colon + 1))
            return Actor(kind, if (name.isNullOrEmpty()) null else name)
        }
        return Actor(ActorKind.USER, value)
    }

    /** Actor.DisplayName as TodoTracker.Core writes it in files ("You", "Edited in files", "Agent: copilot"). */
    fun displayName(actor: Actor): String {
        val kind = actor.kind.wire.replaceFirstChar { it.uppercaseChar() }
        return when {
            actor.kind == ActorKind.USER -> "You"
            actor.kind == ActorKind.VAULT -> "Edited in files"
            !Net.isBlank(actor.name) -> "$kind: ${actor.name}"
            else -> kind
        }
    }

    /** A task's stable, short Obsidian block id (unless the file already gave it one). */
    fun blockId(id: UUID): String = "t" + Net.n(id).takeLast(7)

    /** Tracks fenced code blocks like CommonMark: a fence closes only with the same character, at least as long. */
    class Fence {
        private var marker = ' '
        private var length = 0

        val isOpen: Boolean get() = length > 0

        /** Feeds one line; true when it is part of a code block (fence lines included). */
        fun accept(line: String): Boolean {
            val trimmed = line.trimStart(' ', '\t')
            if (isOpen) {
                val closing = Net.trimEnd(trimmed)
                if (closing.length >= length && closing.all { it == marker }) length = 0
                return true
            }
            if (trimmed.length >= 3 && (trimmed[0] == '`' || trimmed[0] == '~')) {
                var run = 0
                while (run < trimmed.length && trimmed[run] == trimmed[0]) run++
                if (run >= 3 && !(trimmed[0] == '`' && trimmed.substring(run).contains('`'))) {
                    marker = trimmed[0]
                    length = run
                    return true
                }
            }
            return false
        }

        val closer: String get() = marker.toString().repeat(length)
    }

    /** Adds a closing fence if [text] ends inside a code block, so it can't swallow what follows. */
    fun closeFences(text: String): String {
        val fence = Fence()
        for (line in text.split('\n')) fence.accept(line)
        return if (fence.isOpen) text + "\n" + fence.closer else text
    }
}
