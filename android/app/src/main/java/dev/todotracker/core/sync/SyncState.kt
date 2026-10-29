package dev.todotracker.core.sync

import dev.todotracker.core.vault.JsonEscaping
import dev.todotracker.core.vault.NetJson
import dev.todotracker.core.vault.VaultFiles
import java.io.File
import java.io.IOException
import java.time.Instant
import java.util.UUID
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** A sync that couldn't be merged by itself; both versions are kept. [result] is the hash of what the merge wrote. */
data class SyncConflictRecord(val key: String, val path: String, val peer: String, val peerName: String, val mine: String, val theirs: String, val base: String?, val at: Instant, val result: String? = null)

/** Another device syncing through the same place, and when it last published. */
data class SyncDevice(val device: String, val name: String, val at: Instant)

/** How the last sync went, for the status line and the Sync screen. */
data class SyncStatus(val lastSync: Instant?, val lastError: String?, val peers: Int, val conflicts: List<SyncConflictRecord>, val devices: List<SyncDevice> = emptyList()) {
    companion object {
        val EMPTY = SyncStatus(null, null, 0, emptyList())
    }
}

/**
 * What this vault remembers about syncing with one place (TodoTracker.Core.Sync.SyncState), kept in the app's private
 * storage, never in the vault or the cloud: this device's id, what it last agreed on with each other device (the merge
 * base), the contents those bases need, what it last published, and the open conflicts.
 */
class SyncState(private val dir: File, deviceDirectory: File, deviceName: String? = null) {
    val deviceId: String
    val deviceName: String

    init {
        dir.mkdirs()
        deviceDirectory.mkdirs()
        // One id per vault on this device, shared by every place it syncs with.
        val deviceFile = File(deviceDirectory, "device.json")
        var device = read(deviceFile) as? JsonObject
        val id = NetJson.str(device?.get("id"))
        if (device == null || id == null || !isSafeId(id)) {
            device = JsonObject(linkedMapOf("id" to JsonPrimitive(UUID.randomUUID().toString().replace("-", "")), "name" to JsonPrimitive(deviceName ?: "Android")))
            write(deviceFile, device)
        }
        deviceId = NetJson.str(device["id"])!!
        this.deviceName = deviceName ?: NetJson.str(device["name"]) ?: "Android"
    }

    private val marksFile get() = File(dir, "marks.json")

    private fun mark(name: String): String? = NetJson.str((read(marksFile) as? JsonObject)?.get(name))

    private fun setMark(name: String, value: String?) {
        val marks = LinkedHashMap((read(marksFile) as? JsonObject) ?: JsonObject(emptyMap()))
        if (value == null) marks.remove(name) else marks[name] = JsonPrimitive(value)
        write(marksFile, JsonObject(marks))
    }

    /** The place's version (a gist's ETag, a folder's hash) when everything it had was last merged. */
    var remoteVersion: String?
        get() = mark("remoteVersion")
        set(value) = setMark("remoteVersion", value)

    /** The place's version when last seen while only writing to it. */
    var seenVersion: String?
        get() = mark("seenVersion")
        set(value) = setMark("seenVersion", value)

    /** A hash of what was last published (an unchanged vault isn't published again). */
    var published: String?
        get() = mark("published")
        set(value) = setMark("published", value)

    var publishedAt: Instant?
        get() = NetTime.parse(mark("publishedAt"))
        set(value) = setMark("publishedAt", value?.let(NetTime::format))

    /** Which place was last synced with (a new one gets everything published again). */
    var remoteIdentity: String?
        get() = mark("remoteIdentity")
        set(value) = setMark("remoteIdentity", value)

    /** The entries last published (what disappeared since was deleted here). */
    var publishedEntries: List<SyncEntry>
        get() = readEntries(File(dir, "published.json"))
        set(value) = write(File(dir, "published.json"), JsonArray(value.map(SyncJson::entry)))

    /** What this device deleted lately. */
    var tombstones: List<SyncTombstone>
        get() = ((read(File(dir, "deleted.json")) as? JsonArray) ?: JsonArray(emptyList())).mapNotNull { t ->
            val o = t as? JsonObject ?: return@mapNotNull null
            SyncTombstone(NetJson.str(o["key"]) ?: return@mapNotNull null, NetJson.str(o["hash"]) ?: return@mapNotNull null, NetTime.parse(NetJson.str(o["at"])) ?: return@mapNotNull null)
        }
        set(value) = write(File(dir, "deleted.json"), JsonArray(value.map { JsonObject(linkedMapOf("key" to JsonPrimitive(it.key), "hash" to JsonPrimitive(it.hash), "at" to NetTime.json(it.at))) }))

    fun forgetPeer(peer: String) {
        basePath(peer).delete()
    }

    fun loadBase(peer: String): Map<String, SyncEntry> = readEntries(basePath(peer)).associateByTo(LinkedHashMap()) { it.key }

    fun saveBase(peer: String, entries: Map<String, SyncEntry>) = write(basePath(peer), JsonArray(entries.values.sortedBy { it.key }.map(SyncJson::entry)))

    fun peers(): List<String> = File(dir, "bases").listFiles { f -> f.name.endsWith(".json") }?.map { it.name.removeSuffix(".json") }?.sorted() ?: emptyList()

    fun readBlob(hash: String): ByteArray? = blobPath(hash).takeIf { it.exists() }?.readBytes()

    fun hasBlob(hash: String): Boolean = blobPath(hash).exists()

    fun writeBlob(hash: String, content: ByteArray) {
        val file = blobPath(hash)
        if (!file.exists()) VaultFiles.writeAtomic(file, content)
    }

    /** Drops cached contents that no merge base and no open conflict needs. */
    fun collectBlobs(keep: Collection<String>) {
        val needed = keep.toHashSet()
        File(dir, "blobs").walkBottomUp().filter { it.isFile && it.name !in needed }.forEach { it.delete() }
    }

    var status: SyncStatus
        get() {
            val o = read(File(dir, "status.json")) as? JsonObject ?: return SyncStatus.EMPTY
            val conflicts = ((o["conflicts"] as? JsonArray) ?: JsonArray(emptyList())).mapNotNull { c ->
                val x = c as? JsonObject ?: return@mapNotNull null
                fun s(k: String) = NetJson.str(x[k])
                SyncConflictRecord(s("key") ?: return@mapNotNull null, s("path") ?: "", s("peer") ?: "", s("peerName") ?: "", s("mine") ?: "", s("theirs") ?: "", s("base"), NetTime.parse(s("at")) ?: NetTime.MIN, s("result"))
            }
            val devices = ((o["devices"] as? JsonArray) ?: JsonArray(emptyList())).mapNotNull { d ->
                val x = d as? JsonObject ?: return@mapNotNull null
                SyncDevice(NetJson.str(x["device"]) ?: return@mapNotNull null, NetJson.str(x["name"]) ?: "", NetTime.parse(NetJson.str(x["at"])) ?: NetTime.MIN)
            }
            return SyncStatus(NetTime.parse(NetJson.str(o["lastSync"])), NetJson.str(o["lastError"]), NetJson.long(o["peers"])?.toInt() ?: 0, conflicts, devices)
        }
        set(value) {
            fun str(v: String?): JsonElement = v?.let(::JsonPrimitive) ?: JsonNull
            write(
                File(dir, "status.json"),
                JsonObject(
                    linkedMapOf(
                        "lastSync" to (value.lastSync?.let(NetTime::json) ?: JsonNull),
                        "lastError" to str(value.lastError),
                        "peers" to JsonPrimitive(value.peers),
                        "conflicts" to JsonArray(
                            value.conflicts.map { c ->
                                JsonObject(
                                    linkedMapOf(
                                        "key" to JsonPrimitive(c.key), "path" to JsonPrimitive(c.path), "peer" to JsonPrimitive(c.peer), "peerName" to JsonPrimitive(c.peerName),
                                        "mine" to JsonPrimitive(c.mine), "theirs" to JsonPrimitive(c.theirs), "base" to str(c.base), "at" to NetTime.json(c.at), "result" to str(c.result),
                                    ),
                                )
                            },
                        ),
                        "devices" to JsonArray(value.devices.map { JsonObject(linkedMapOf("device" to JsonPrimitive(it.device), "name" to JsonPrimitive(it.name), "at" to NetTime.json(it.at))) }),
                    ),
                ),
            )
        }

    private fun readEntries(file: File): List<SyncEntry> = ((read(file) as? JsonArray) ?: JsonArray(emptyList())).mapNotNull { e ->
        val o = e as? JsonObject ?: return@mapNotNull null
        SyncEntry(NetJson.str(o["key"]) ?: return@mapNotNull null, NetJson.str(o["path"]) ?: return@mapNotNull null, NetJson.str(o["hash"]) ?: return@mapNotNull null, NetTime.parse(NetJson.str(o["since"])))
    }

    private fun basePath(peer: String) = File(File(dir, "bases"), safeId(peer) + ".json")

    private fun blobPath(hash: String) = File(File(File(dir, "blobs"), safeId(hash).substring(0, 2)), safeId(hash))

    private fun isSafeId(id: String) = id.length in 2..128 && id.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' }

    /** Ids come from other devices: only letters and digits reach a file name. */
    private fun safeId(id: String): String = if (isSafeId(id)) id else throw IOException("Not a valid sync id: $id")

    private fun read(file: File): JsonElement? = if (file.exists()) NetJson.parseOrNull(file.readText()) else null

    private fun write(file: File, value: JsonElement) = VaultFiles.writeAtomic(file, NetJson.write(value, JsonEscaping.DEFAULT, indented = true))
}
