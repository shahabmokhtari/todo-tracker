package dev.todotracker.core.vault

import java.math.BigDecimal
import java.security.MessageDigest
import java.util.UUID
import java.util.regex.Pattern
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/**
 * How TodoTracker.Core (.NET) treats text, so the files and hashes this app makes are the ones a computer makes:
 * white space, trimming, invariant (simple) case mapping, ordinal case-insensitive keys, GUID text and the ids
 * derived from text. See docs/design/phone-sync.md, "Hard to port faithfully".
 */
internal object Net {
    /** .NET's char.IsWhiteSpace (Kotlin's isWhitespace also counts U+001C–U+001F and misses U+0085). */
    fun isWhite(c: Char): Boolean = when {
        c in '\t'..'\r' || c == ' ' || c == '\u0085' || c == ' ' -> true
        c.code < 0x80 -> false
        else -> when (Character.getType(c).toByte()) {
            Character.SPACE_SEPARATOR, Character.LINE_SEPARATOR, Character.PARAGRAPH_SEPARATOR -> true
            else -> false
        }
    }

    fun trim(s: String): String = trimEnd(trimStart(s))

    fun trimStart(s: String): String {
        var i = 0
        while (i < s.length && isWhite(s[i])) i++
        return s.substring(i)
    }

    fun trimEnd(s: String): String {
        var i = s.length
        while (i > 0 && isWhite(s[i - 1])) i--
        return s.substring(0, i)
    }

    /** string.IsNullOrWhiteSpace. */
    fun isBlank(s: String?): Boolean = s == null || s.all(::isWhite)

    /** ToUpperInvariant: each code point's simple mapping (ß stays ß, ǅ becomes Ǆ); .NET keeps the dotless ı as it is. */
    fun upper(s: String): String = map(s) { if (it == 0x131) it else Character.toUpperCase(it) }

    /** ToLowerInvariant: each code point's simple mapping; .NET keeps İ (U+0130) as it is (see vault/parse.json). */
    fun lower(s: String): String = map(s) { if (it == 0x130) it else Character.toLowerCase(it) }

    private inline fun map(s: String, f: (Int) -> Int): String {
        val sb = StringBuilder(s.length)
        var i = 0
        while (i < s.length) {
            val cp = s.codePointAt(i)
            sb.appendCodePoint(f(cp))
            i += Character.charCount(cp)
        }
        return sb.toString()
    }

    /** The key StringComparer.OrdinalIgnoreCase compares by. */
    fun ci(s: String): String = upper(s)

    fun equalsCi(a: String?, b: String?): Boolean = if (a == null || b == null) a == b else a.length == b.length && ci(a) == ci(b)

    fun startsWithCi(s: String, prefix: String): Boolean = s.length >= prefix.length && equalsCi(s.substring(0, prefix.length), prefix)

    fun endsWithCi(s: String, suffix: String): Boolean = s.length >= suffix.length && equalsCi(s.substring(s.length - suffix.length), suffix)

    /** Ordinal (UTF-16 unit) order: Kotlin's String.compareTo already is. */
    val ordinal: Comparator<String> = Comparator { a, b -> a.compareTo(b) }

    val ordinalIgnoreCase: Comparator<String> = Comparator { a, b -> ci(a).compareTo(ci(b)) }

    // ---- GUIDs ----

    private val hex = "0123456789abcdef"

    /** Guid.TryParse: the D, N, B and P forms (any case, white space around). */
    fun parseGuid(text: String?): UUID? {
        var s = trim(text ?: return null)
        if (s.length == 38 && ((s[0] == '{' && s[37] == '}') || (s[0] == '(' && s[37] == ')'))) s = s.substring(1, 37)
        val digits = when (s.length) {
            36 -> if (s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-') s.replace("-", "") else return null
            32 -> s
            else -> return null
        }
        if (digits.length != 32 || !digits.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) return null
        return UUID(java.lang.Long.parseUnsignedLong(digits.substring(0, 16), 16), java.lang.Long.parseUnsignedLong(digits.substring(16), 16))
    }

    /** Guid.TryParseExact(text, "N"). */
    fun parseGuidN(text: String): UUID? =
        if (text.length == 32 && text.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' }) parseGuid(text) else null

    /** The 32-hex form (Guid.ToString("N")). */
    fun n(id: UUID): String = id.toString().replace("-", "")

    /**
     * VaultText.StableGuid: SHA-256 of the key's UTF-8, its first 16 bytes with the version-8 bits, read the way
     * `new Guid(bytes)` does (the first three fields little-endian).
     */
    fun stableGuid(key: String): UUID {
        val b = MessageDigest.getInstance("SHA-256").digest(key.toByteArray(Charsets.UTF_8)).copyOf(16)
        b[7] = ((b[7].toInt() and 0x0F) or 0x80).toByte()
        b[8] = ((b[8].toInt() and 0x3F) or 0x80).toByte()
        val order = intArrayOf(3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15)
        val sb = StringBuilder(32)
        for (i in order) {
            sb.append(hex[(b[i].toInt() shr 4) and 0xF]).append(hex[b[i].toInt() and 0xF])
        }
        return parseGuid(sb.toString())!!
    }

    fun sha256Hex(bytes: ByteArray): String {
        val d = MessageDigest.getInstance("SHA-256").digest(bytes)
        val sb = StringBuilder(64)
        for (x in d) sb.append(hex[(x.toInt() shr 4) and 0xF]).append(hex[x.toInt() and 0xF])
        return sb.toString()
    }

    // ---- Regexes ----

    /** White space as .NET's \s (inside a character class). */
    const val WS = "\\f\\n\\r\\t\\u000B\\u0085\\p{Z}"

    /**
     * A .NET-like pattern: '.' is anything but '\n' and '$' is the end or before a final '\n' (UNIX_LINES). Write
     * \s, \d and \w as [WS], \p{Nd} and [\p{L}\p{Mn}\p{Nd}\p{Pc}]: Java's own differ from .NET's.
     */
    fun rx(pattern: String, ignoreCase: Boolean = false, multiline: Boolean = false): Pattern {
        var flags = Pattern.UNIX_LINES
        if (ignoreCase) flags = flags or Pattern.CASE_INSENSITIVE or Pattern.UNICODE_CASE
        if (multiline) flags = flags or Pattern.MULTILINE
        return Pattern.compile(pattern, flags)
    }

    /** Regex.Replace with a function of the match. */
    fun replace(p: Pattern, s: String, f: (java.util.regex.Matcher) -> String): String {
        val m = p.matcher(s)
        val sb = StringBuilder()
        var last = 0
        while (m.find()) {
            sb.append(s, last, m.start()).append(f(m))
            last = m.end()
        }
        return sb.append(s, last, s.length).toString()
    }

    /** string.Split(c) (empty parts kept). */
    fun split(s: String, c: Char): List<String> = s.split(c)

    /** Path.GetExtension of a vault path ("" when none, or when the name ends with a dot). */
    fun extension(path: String): String {
        val slash = path.lastIndexOf('/')
        val dot = path.lastIndexOf('.')
        return if (dot <= slash || dot == path.length - 1) "" else path.substring(dot)
    }

    /** Path.GetFileNameWithoutExtension of a vault path. */
    fun stem(path: String): String {
        val name = path.substring(path.lastIndexOf('/') + 1)
        val ext = extension(name)
        return name.substring(0, name.length - ext.length)
    }
}

/** How a string is escaped in JSON, exactly like System.Text.Json's two encoders. */
internal enum class JsonEscaping {
    /** JavaScriptEncoder.Default: only printable ASCII as is, without " & ' + < > \ and backtick (config, state, activity, manifests). */
    DEFAULT,

    /** UnsafeRelaxedJsonEscaping: most of Unicode as is (the hidden %%{…}%% data, YAML strings). */
    RELAXED,
}

/**
 * JSON written byte for byte like System.Text.Json (compact, or indented as WriteIndented does), from kotlinx
 * JsonElements (which keep property order and numbers as they were typed).
 */
internal object NetJson {
    private val parser = Json

    fun parse(text: String): JsonElement = parser.parseToJsonElement(text)

    fun parseOrNull(text: String): JsonElement? = try {
        parse(text)
    } catch (e: Exception) {
        null
    }

    fun string(s: String, escaping: JsonEscaping): String = StringBuilder().also { appendString(it, s, escaping) }.toString()

    fun write(e: JsonElement?, escaping: JsonEscaping = JsonEscaping.DEFAULT, indented: Boolean = false): String =
        StringBuilder().also { write(it, e ?: JsonNull, escaping, if (indented) 0 else -1) }.toString()

    private fun write(sb: StringBuilder, e: JsonElement, escaping: JsonEscaping, depth: Int) {
        when (e) {
            is JsonNull -> sb.append("null")
            is JsonPrimitive -> if (e.isString) appendString(sb, e.content, escaping) else sb.append(e.content)
            is JsonObject -> {
                if (e.isEmpty()) {
                    sb.append("{}")
                    return
                }
                sb.append('{')
                var first = true
                for ((k, v) in e) {
                    if (!first) sb.append(',')
                    first = false
                    if (depth >= 0) newline(sb, depth + 1)
                    appendString(sb, k, escaping)
                    sb.append(if (depth >= 0) ": " else ":")
                    write(sb, v, escaping, if (depth >= 0) depth + 1 else -1)
                }
                newline(sb, depth)
                sb.append('}')
            }
            is JsonArray -> {
                if (e.isEmpty()) {
                    sb.append("[]")
                    return
                }
                sb.append('[')
                var first = true
                for (v in e) {
                    if (!first) sb.append(',')
                    first = false
                    if (depth >= 0) newline(sb, depth + 1)
                    write(sb, v, escaping, if (depth >= 0) depth + 1 else -1)
                }
                newline(sb, depth)
                sb.append(']')
            }
        }
    }

    private fun newline(sb: StringBuilder, depth: Int) {
        if (depth < 0) return
        sb.append('\n')
        repeat(depth) { sb.append("  ") }
    }

    private const val HEX = "0123456789ABCDEF"

    private fun unicode(sb: StringBuilder, c: Char) {
        sb.append("\\u").append(HEX[(c.code shr 12) and 0xF]).append(HEX[(c.code shr 8) and 0xF]).append(HEX[(c.code shr 4) and 0xF]).append(HEX[c.code and 0xF])
    }

    fun appendString(sb: StringBuilder, s: String, escaping: JsonEscaping) {
        sb.append('"')
        var i = 0
        while (i < s.length) {
            val c = s[i]
            when {
                c == '"' -> if (escaping == JsonEscaping.RELAXED) sb.append("\\\"") else sb.append("\\u0022")
                c == '\\' -> sb.append("\\\\")
                c == '\b' -> sb.append("\\b")
                c == '\u000C' -> sb.append("\\f")
                c == '\n' -> sb.append("\\n")
                c == '\r' -> sb.append("\\r")
                c == '\t' -> sb.append("\\t")
                Character.isHighSurrogate(c) && i + 1 < s.length && Character.isLowSurrogate(s[i + 1]) -> {
                    // Characters beyond U+FFFF are always escaped, as their two halves.
                    unicode(sb, c)
                    unicode(sb, s[i + 1])
                    i++
                }
                Character.isSurrogate(c) -> sb.append("\\uFFFD")
                allowed(c, escaping) -> sb.append(c)
                else -> unicode(sb, c)
            }
            i++
        }
        sb.append('"')
    }

    private fun allowed(c: Char, escaping: JsonEscaping): Boolean = when (escaping) {
        JsonEscaping.RELAXED -> RelaxedAllowList.allows(c)
        JsonEscaping.DEFAULT -> c in ' '..'~' && c !in "\"&'+<>\\`"
    }

    /** JsonNode.DeepEquals: objects by property (any order), numbers by value. */
    fun deepEquals(a: JsonElement?, b: JsonElement?): Boolean {
        val x = a ?: JsonNull
        val y = b ?: JsonNull
        return when {
            x is JsonNull || y is JsonNull -> x is JsonNull && y is JsonNull
            x is JsonObject && y is JsonObject -> x.size == y.size && x.all { (k, v) -> y.containsKey(k) && deepEquals(v, y[k]) }
            x is JsonArray && y is JsonArray -> x.size == y.size && x.indices.all { deepEquals(x[it], y[it]) }
            x is JsonPrimitive && y is JsonPrimitive -> when {
                x.isString || y.isString -> x.isString && y.isString && x.content == y.content
                else -> x.content == y.content || numberEquals(x.content, y.content)
            }
            else -> false
        }
    }

    private fun numberEquals(a: String, b: String): Boolean = try {
        BigDecimal(a).compareTo(BigDecimal(b)) == 0
    } catch (e: NumberFormatException) {
        false
    }

    /** A JSON string's text, or null when it isn't a string (JsonValue.TryGetValue<string>). */
    fun str(e: JsonElement?): String? = (e as? JsonPrimitive)?.takeIf { it.isString }?.content

    /** A whole number that fits in a long, or null (JsonValue.TryGetValue<long>). */
    fun long(e: JsonElement?): Long? = (e as? JsonPrimitive)?.takeIf { !it.isString && it !is JsonNull }?.content?.takeIf { Regex("-?[0-9]+").matches(it) }?.toLongOrNull()

    fun bool(e: JsonElement?): Boolean? = (e as? JsonPrimitive)?.takeIf { !it.isString }?.content?.let { if (it == "true") true else if (it == "false") false else null }
}
