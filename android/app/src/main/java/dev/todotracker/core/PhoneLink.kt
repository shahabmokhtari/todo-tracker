package dev.todotracker.core

import java.net.URLDecoder
import java.util.Base64

/**
 * A pairing link from a computer (Settings › Phones › Connect a phone):
 * `todotracker://pair?v=1&hosts=192.168.1.20:5318,100.101.102.103:5318&code=…&fp=…&name=…`. It says where to reach
 * the computer, the one-time code, and the fingerprint of its certificate (so the phone knows it's that computer).
 */
data class PairingLink(val hosts: List<String>, val code: String, val fingerprint: String, val computer: String)

object PhoneLink {
    /** Whether the text is a pairing link at all (even a broken one): it's handled here, not as a task link. */
    fun isPairing(text: String?): Boolean {
        val t = text?.trim() ?: return false
        return t.startsWith("${TaskLinks.SCHEME}://pair", ignoreCase = true) &&
            (t.length == "${TaskLinks.SCHEME}://pair".length || t["${TaskLinks.SCHEME}://pair".length] in "?/#")
    }

    /** The link's parts, or null when it isn't a whole, valid pairing link (then nothing is tried). */
    fun parse(text: String?): PairingLink? {
        val t = text?.trim() ?: return null
        if (!isPairing(t)) return null
        val query = t.substringAfter('?', "").substringBefore('#')
        val params = HashMap<String, String>()
        for (pair in query.split('&')) {
            if (pair.isEmpty()) continue
            val name = pair.substringBefore('=')
            val value = runCatching { URLDecoder.decode(pair.substringAfter('=', "").replace("+", "%2B"), "UTF-8") }.getOrNull() ?: return null
            // A name said twice could mean two different things: such a link isn't trusted.
            if (params.put(name, value) != null) return null
        }
        if (params["v"] != "1") return null
        val hosts = params["hosts"]?.split(',')?.map { it.trim() }?.filter { it.isNotEmpty() } ?: return null
        if (hosts.isEmpty() || hosts.size > 16 || hosts.any { !isHost(it) }) return null
        val code = params["code"]?.trim()?.takeIf { it.isNotEmpty() && it.length <= 200 } ?: return null
        val fp = params["fp"]?.trim() ?: return null
        if (!isFingerprint(fp)) return null
        val computer = params["name"]?.trim()?.take(100)?.ifEmpty { null } ?: "your computer"
        return PairingLink(hosts.distinct(), code, fp, computer)
    }

    /** host:port, with an IPv4 address or a host name (no user, path or IPv6 brackets), and a port 1–65535. */
    fun isHost(text: String): Boolean {
        val i = text.lastIndexOf(':')
        if (i <= 0) return false
        val port = text.substring(i + 1).toIntOrNull() ?: return false
        if (port !in 1..65535) return false
        return Regex("^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$").matches(text.substring(0, i))
    }

    /** A SHA-256 in base64url without padding (43 characters). */
    fun isFingerprint(text: String): Boolean =
        text.length == 43 && Regex("^[A-Za-z0-9_-]+$").matches(text) &&
            runCatching { Base64.getUrlDecoder().decode(text).size == 32 }.getOrDefault(false)
}
