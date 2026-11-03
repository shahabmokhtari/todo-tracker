package dev.onething.core

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.put

/**
 * Send feedback: a note from the person (a bug, an idea or something else) filed as an issue through the owner's
 * issues relay. Never a GitHub token; never anything from the tasks. The same rules in C# (Feedback.cs), Swift and
 * Kotlin, with shared cases in tests/fixtures/feedback.json.
 */
object Feedback {
    const val RELAY_URL = "https://issues.awesomehq.app/v1/issues"

    /** The relay's public key for this app: it can only open a new issue in this app's repository. Not a secret. */
    const val PUBLIC_KEY = "pk_gGPPMjRSsuHaXRdgdXvUevvL"

    const val MAX_MESSAGE = 2000

    /** The counter shows when this few characters are left. */
    const val COUNTER_FROM = 200

    const val SENT_TEXT = "Thanks — we read every note."
    const val RETRY_TEXT = "Couldn’t send — check your connection and try again."
    const val FAILED_TEXT = "Couldn’t send your note: something went wrong on our side. Please try again later."
    const val FINE_PRINT = "We’ll include the app version, your OS and its version, device model, language and screen size."
    const val CONTACT_HINT = "Only if you’d like a reply. It’s shown with your note on GitHub."

    /** What the note is about: its id (in the label) and what the sheet and the title say. */
    enum class Kind(val id: String, val label: String) {
        BUG("bug", "Bug"),
        IDEA("idea", "Idea"),
        OTHER("other", "Other"),
        ;

        companion object {
            fun of(id: String): Kind? = entries.firstOrNull { it.id == id }
        }
    }

    /** Where the sheet was opened from: a button or menu, a shake, a web page, a QR code. */
    val SOURCES = listOf("button", "shake", "page", "qr")

    enum class Outcome { SENT, RETRY, FAILED }

    /** What the person wrote, as the sheet has it (clientID: made once per sheet and kept for retries). */
    data class Draft(val kind: Kind, val message: String, val contact: String?, val source: String, val clientId: String)

    /** Coarse facts about the device: no ids, no location, nothing from the tasks. */
    data class Device(
        val platform: String,
        val appVersion: String?,
        val build: String?,
        val os: String?,
        val device: String?,
        val language: String?,
        val screen: String?,
    )

    /**
     * The message as sent: control characters removed (except new lines and tabs), trimmed, cut to [max] code points
     * (an emoji is one), trimmed again.
     */
    fun cleanMessage(text: String, max: Int = MAX_MESSAGE): String {
        val kept = StringBuilder()
        text.codePoints().forEach { cp ->
            if (cp == '\n'.code || cp == '\t'.code || Character.getType(cp) != Character.CONTROL.toInt()) kept.appendCodePoint(cp)
        }
        val trimmed = kept.toString().trim()
        if (trimmed.codePointCount(0, trimmed.length) <= max) return trimmed
        return trimmed.substring(0, trimmed.offsetByCodePoints(0, max)).trim()
    }

    /** How long the address for a reply can be. */
    const val MAX_CONTACT = 200

    /** The address for a reply as sent: every control character removed, trimmed, cut to [MAX_CONTACT] code points, trimmed again. */
    fun cleanContact(text: String?): String {
        val kept = StringBuilder()
        text.orEmpty().codePoints().forEach { cp -> if (Character.getType(cp) != Character.CONTROL.toInt()) kept.appendCodePoint(cp) }
        val trimmed = kept.toString().trim()
        if (trimmed.codePointCount(0, trimmed.length) <= MAX_CONTACT) return trimmed
        return trimmed.substring(0, trimmed.offsetByCodePoints(0, MAX_CONTACT)).trim()
    }

    /** How long the message is, as the relay counts it (code points). */
    fun length(text: String): Int = cleanMessage(text, Int.MAX_VALUE).let { it.codePointCount(0, it.length) }

    /** How long an issue's title can be (code points). */
    const val MAX_TITLE = 256

    /**
     * "Bug · Android: <the note's first line, trimmed>"; longer than [MAX_TITLE] code points: cut to one less, trimmed,
     * and ended with "…" (the whole note is in the body).
     */
    fun title(kind: Kind, platform: String, message: String): String {
        val title = "${kind.label} · $platform: " + dev.onething.core.vault.Net.trim(message.split('\n')[0])
        if (title.codePointCount(0, title.length) <= MAX_TITLE) return title
        return dev.onething.core.vault.Net.trimEnd(title.substring(0, title.offsetByCodePoints(0, MAX_TITLE - 1))) + "…"
    }

    /** The request body (keys in the relay's order), or null when there's nothing to send. */
    fun body(draft: Draft, device: Device, maxMessage: Int = MAX_MESSAGE): JsonObject? {
        val message = cleanMessage(draft.message, maxMessage)
        if (message.isEmpty()) return null
        val platform = device.platform.lowercase()
        val contact = cleanContact(draft.contact)
        val version = device.appVersion?.takeIf { it.isNotBlank() }?.let { v -> device.build?.takeIf { it.isNotBlank() }?.let { "$v ($it)" } ?: v }
        return buildJsonObject {
            put("message", message)
            put("title", title(draft.kind, device.platform, message))
            if (contact.isNotEmpty()) put("contact", contact)
            put("clientID", draft.clientId)
            put("labels", buildJsonArray {
                add(JsonPrimitive("kind:${draft.kind.id}"))
                add(JsonPrimitive("platform:$platform"))
                add(JsonPrimitive("source:${draft.source}"))
            })
            put("metadata", buildJsonObject {
                listOf(
                    "Platform" to platform, "App version" to version, "OS" to device.os, "Device" to device.device,
                    "Language" to device.language, "Screen" to device.screen, "Source" to draft.source,
                ).forEach { (key, value) -> if (!value.isNullOrBlank()) put(key, value) }
            })
        }
    }

    /** What an answer means: 2xx sent; no answer (null), 408, 429 or 5xx: try again (same clientID); else failed. */
    fun classify(status: Int?): Outcome = when {
        status == null || status == 408 || status == 429 || status in 500..599 -> Outcome.RETRY
        status in 200..299 -> Outcome.SENT
        else -> Outcome.FAILED
    }

    fun text(outcome: Outcome): String = when (outcome) {
        Outcome.SENT -> SENT_TEXT
        Outcome.RETRY -> RETRY_TEXT
        Outcome.FAILED -> FAILED_TEXT
    }

    /** Debug builds only log the note, unless asked to send for real; release builds always send. */
    fun sendsForReal(debugBuild: Boolean, sendFlag: Boolean): Boolean = !debugBuild || sendFlag
}

/** Posts one body to the relay; returns the HTTP status, or throws IOException when there is no answer (offline). */
fun interface FeedbackTransport {
    fun post(url: String, headers: Map<String, String>, body: String): Int
}

/** Plain HTTPS to the relay. */
object HttpsFeedbackTransport : FeedbackTransport {
    override fun post(url: String, headers: Map<String, String>, body: String): Int {
        val connection = URL(url).openConnection() as HttpURLConnection
        try {
            connection.connectTimeout = 10_000
            connection.readTimeout = 15_000
            connection.useCaches = false
            connection.instanceFollowRedirects = false
            connection.requestMethod = "POST"
            connection.doOutput = true
            for ((name, value) in headers) connection.setRequestProperty(name, value)
            connection.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }
            val status = connection.responseCode
            runCatching { (if (status >= 400) connection.errorStream else connection.inputStream)?.use { it.readBytes() } }
            return status
        } finally {
            connection.disconnect()
        }
    }
}

/**
 * Sends a note (blocking: call it off the main thread). [sendForReal] false (a debug build): the body is only logged.
 */
class FeedbackClient(
    private val transport: FeedbackTransport = HttpsFeedbackTransport,
    private val sendForReal: Boolean = true,
    private val log: (String) -> Unit = {},
    private val url: String = Feedback.RELAY_URL,
) {
    fun send(draft: Feedback.Draft, device: Feedback.Device): Feedback.Outcome {
        val body = Feedback.body(draft, device) ?: return Feedback.Outcome.FAILED
        if (!sendForReal) {
            log("Feedback (debug build, not sent): POST $url $body")
            return Feedback.Outcome.SENT
        }
        val headers = linkedMapOf("Authorization" to "Bearer ${Feedback.PUBLIC_KEY}", "Content-Type" to "application/json")
        val status = try {
            transport.post(url, headers, body.toString())
        } catch (_: IOException) {
            null
        }
        return Feedback.classify(status)
    }
}
