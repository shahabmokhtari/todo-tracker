package dev.todotracker.app

import android.content.Context
import android.os.Build
import android.provider.Settings
import dev.todotracker.core.Claimed
import dev.todotracker.core.PairingLink
import dev.todotracker.core.PhoneClient
import dev.todotracker.core.PinnedHttpsTransport
import java.io.File
import java.time.Instant

/**
 * The computer this phone is connected to (Settings › Connect to a computer): where it is, its certificate's
 * fingerprint, its name, and this phone's key to it (encrypted with a key that never leaves the phone's keystore), and
 * the last tasks read from it (kept to show when it can't be reached). The phone's own board is a separate file, left
 * as it was while connected.
 */
class PhoneConnection(context: Context) {
    private val app = context.applicationContext
    private val prefs = app.getSharedPreferences("phone", Context.MODE_PRIVATE)
    private val cache = File(app.filesDir, "remote-board.json")

    data class Pairing(val hosts: List<String>, val fingerprint: String, val computer: String, val token: String)

    /** The connection, or null when this phone uses its own board (or its key can't be read, e.g. after a restore). */
    fun load(): Pairing? {
        val hosts = prefs.getString("hosts", null)?.split(',')?.filter { it.isNotEmpty() } ?: return null
        val fp = prefs.getString("fp", null) ?: return null
        val token = prefs.getString("token", null)?.let(::decrypt) ?: return null
        return Pairing(hosts, fp, prefs.getString("computer", null) ?: "your computer", token)
    }

    fun save(link: PairingLink, claimed: Claimed, now: Instant) {
        cache.delete()
        prefs.edit()
            .putString("hosts", link.hosts.joinToString(","))
            .putString("fp", link.fingerprint)
            .putString("computer", claimed.computer)
            .putString("token", encrypt(claimed.token))
            .remove("preferred").remove("etag").remove("readAt").remove("revokedFrom")
            .putLong("remindersSince", now.toEpochMilli())
            .apply()
    }

    /** Back to the phone's own board. [revoked]: the computer disconnected it (said once, with Reconnect). */
    fun forget(revoked: Boolean) {
        val computer = prefs.getString("computer", null)
        cache.delete()
        val editor = prefs.edit().clear()
        if (revoked && computer != null) editor.putString("revokedFrom", computer)
        editor.apply()
    }

    /** The computer that disconnected this phone, to say so (null: nothing to say). */
    var revokedFrom: String?
        get() = prefs.getString("revokedFrom", null)
        set(value) = prefs.edit().putString("revokedFrom", value).apply()

    fun client(pairing: Pairing, connectMillis: Int = 5_000, readMillis: Int = 15_000): PhoneClient =
        PhoneClient(pairing.hosts, PinnedHttpsTransport(pairing.fingerprint, connectMillis, readMillis), pairing.token, prefs.getString("preferred", null))

    /** Remembers the address that answered (tried first next time). */
    fun answered(client: PhoneClient) {
        val host = client.current ?: return
        if (prefs.getString("preferred", null) != host) prefs.edit().putString("preferred", host).apply()
    }

    /** The last tasks read: the export's text, its tag, and when. */
    data class Cached(val text: String, val etag: String?, val at: Instant)

    fun cached(): Cached? {
        val text = runCatching { cache.readText() }.getOrNull() ?: return null
        return Cached(text, prefs.getString("etag", null), Instant.ofEpochMilli(prefs.getLong("readAt", 0)))
    }

    fun cache(text: String, etag: String?, at: Instant) {
        val temporary = File(cache.path + ".tmp")
        temporary.writeText(text)
        if (!temporary.renameTo(cache)) {
            cache.delete()
            temporary.renameTo(cache)
        }
        prefs.edit().putString("etag", etag).putLong("readAt", at.toEpochMilli()).apply()
    }

    /** The read was unchanged: the tasks shown are as of now. */
    fun stillCurrent(at: Instant) = prefs.edit().putLong("readAt", at.toEpochMilli()).apply()

    /** Reminders due after this were not told yet (each is told once). */
    var remindersSince: Instant
        get() = Instant.ofEpochMilli(prefs.getLong("remindersSince", System.currentTimeMillis()))
        set(value) = prefs.edit().putLong("remindersSince", value.toEpochMilli()).apply()

    private val box = KeystoreBox(KEY)

    private fun encrypt(text: String): String = box.seal(text)

    private fun decrypt(text: String): String? = box.open(text)

    companion object {
        private const val KEY = "phone-token"

        /** The name the computer shows for this phone ("Pixel 8"): the one set in the phone's settings, else its model. */
        fun deviceName(context: Context): String =
            runCatching { Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME) }.getOrNull()?.trim()?.ifEmpty { null }
                ?: listOf(Build.MANUFACTURER, Build.MODEL).filterNotNull().joinToString(" ").trim().ifEmpty { "Android phone" }

        /** The name typed for this phone (kept apart, so it stays through Disconnect), else the phone's own name. */
        fun phoneName(context: Context): String =
            context.applicationContext.getSharedPreferences("phone-name", Context.MODE_PRIVATE).getString("name", null)
                ?.takeIf { it.isNotBlank() } ?: deviceName(context)

        fun rememberPhoneName(context: Context, name: String) {
            context.applicationContext.getSharedPreferences("phone-name", Context.MODE_PRIVATE).edit().putString("name", name).apply()
        }

        /** Connects with a pairing link: the code is traded for this phone's key (off the screen's thread). */
        fun claim(link: PairingLink, name: String): Claimed =
            PhoneClient(link.hosts, PinnedHttpsTransport(link.fingerprint)).claim(link.code, name)
    }
}
