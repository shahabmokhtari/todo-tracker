package dev.todotracker.core.sync

import dev.todotracker.core.vault.Net
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.util.UUID

/** What a sync entry holds; decides how two versions are merged (TodoTracker.Core.Sync). */
enum class SyncKind { TASK, RICH, FILE, CONFIG, ORDER, ACTIVITY }

/** One syncable thing: [path] is vault-relative with forward slashes; [hash] is SHA-256 of its bytes (lowercase hex). */
data class SyncEntry(val key: String, val path: String, val hash: String, val since: Instant? = null)

object SyncKeys {
    const val CONFIG = "config"
    const val ORDER = "order"
    const val ACTIVITY = "activity"
    const val CONFIG_PATH = ".todo-tracker/config.json"
    const val ORDER_PATH = ".todo-tracker/order.json"
    const val ACTIVITY_PATH = ".todo-tracker/activity.jsonl"

    fun task(id: UUID): String = "t/" + Net.n(id)

    fun rich(id: UUID): String = "h/" + Net.n(id)

    fun file(path: String): String = "f/$path"

    fun kindOf(key: String): SyncKind = when {
        key == CONFIG -> SyncKind.CONFIG
        key == ORDER -> SyncKind.ORDER
        key == ACTIVITY -> SyncKind.ACTIVITY
        key.startsWith("t/") -> SyncKind.TASK
        key.startsWith("h/") -> SyncKind.RICH
        else -> SyncKind.FILE
    }
}

object SyncHash {
    fun of(content: ByteArray): String = Net.sha256Hex(content)
}

/** A device deleted this version of this entry at this time. */
data class SyncTombstone(val key: String, val hash: String, val at: Instant)

/** What one device published: everything in its vault, by key (contents are stored separately, by hash). */
data class DeviceSnapshot(
    val device: String,
    val name: String,
    val at: Instant,
    val entries: List<SyncEntry>,
    val schema: Int = 1,
    /** What this device deleted lately (so a device that comes back with old copies doesn't bring them back). */
    val deleted: List<SyncTombstone> = emptyList(),
)

object SyncLimits {
    /** Bigger files aren't synced. */
    const val MAX_FILE_BYTES = 50L * 1024 * 1024

    /** Deletes are remembered this long; devices not heard from this long are no longer merged. */
    val MEMORY: java.time.Duration = java.time.Duration.ofDays(180)

    /** A device that changed nothing still publishes this often, so others know it's alive. */
    val HEARTBEAT: java.time.Duration = java.time.Duration.ofDays(7)
}

/** One side of a merge: its entries and a way to read an entry's content. */
class SyncSide(val device: String, val name: String, val entries: Map<String, SyncEntry>, val read: (SyncEntry) -> ByteArray)

/** This vault's syncable entries; [read] throws [SyncStaleException] if a file changed since. */
class SyncSnapshot(val entries: Map<String, SyncEntry>, val read: (SyncEntry) -> ByteArray)

/** The vault changed while a sync was being worked out; the sync starts over. */
class SyncStaleException(message: String) : Exception(message)

enum class SyncOpKind(val wire: String) {
    /** Write [SyncOp.content] to [SyncOp.path]; [SyncOp.expected] null means the file must not exist yet. */
    WRITE("write"),

    /** Move the file at [SyncOp.path] to [SyncOp.to]. */
    MOVE("move"),

    /** Move the file to the vault's trash. */
    DELETE("delete"),

    /** Append these activity lines. */
    APPEND_ACTIVITY("appendActivity"),

    /** Replace the tabs/labels/order settings. */
    SET_CONFIG("setConfig"),

    /** Replace the manual Now order (a JSON array of task ids). */
    SET_ORDER("setOrder"),
}

/** One change to make to the vault; [expected] is the hash the file must still have (else the sync starts over). */
class SyncOp(val kind: SyncOpKind, val path: String, val expected: String?, val content: ByteArray? = null, val to: String? = null)

/** Both devices changed the same lines; both versions' lines were kept. [result] is the hash of what was written. */
data class SyncConflict(val key: String, val path: String, val peer: String, val peerName: String, val mine: String, val theirs: String, val base: String?, val result: String)

class SyncPlan(val ops: List<SyncOp>, val conflicts: List<SyncConflict>)

/** DateTimeOffset as System.Text.Json writes and reads it (100 ns ticks, fraction trimmed, `+00:00`). */
object NetTime {
    /** DateTimeOffset.MinValue: "oldest possible". */
    val MIN: Instant = LocalDateTime.of(1, 1, 1, 0, 0).toInstant(ZoneOffset.UTC)

    fun format(at: Instant): String {
        val t = LocalDateTime.ofInstant(at, ZoneOffset.UTC)
        val sb = StringBuilder()
        sb.append(String.format(java.util.Locale.ROOT, "%04d-%02d-%02dT%02d:%02d:%02d", t.year, t.monthValue, t.dayOfMonth, t.hour, t.minute, t.second))
        val ticks = t.nano / 100
        if (ticks != 0) sb.append('.').append(String.format(java.util.Locale.ROOT, "%07d", ticks).trimEnd('0'))
        return sb.append("+00:00").toString()
    }

    /** The time as a JSON string written the way System.Text.Json writes a DateTimeOffset (never escaped: `+` stays). */
    @OptIn(kotlinx.serialization.ExperimentalSerializationApi::class)
    fun json(at: Instant): kotlinx.serialization.json.JsonPrimitive = kotlinx.serialization.json.JsonUnquotedLiteral("\"" + format(at) + "\"")

    fun parse(text: String?): Instant? = text?.let { dev.todotracker.core.vault.VaultText.parseOffset(it, assumeUtc = false) ?: dev.todotracker.core.vault.VaultText.parseOffset(it, assumeUtc = true) }
}
