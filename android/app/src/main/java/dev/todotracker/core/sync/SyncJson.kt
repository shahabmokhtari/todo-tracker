package dev.todotracker.core.sync

import dev.todotracker.core.vault.JsonEscaping
import dev.todotracker.core.vault.Net
import dev.todotracker.core.vault.NetJson
import java.io.IOException
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** A device file that can't be read (half written, damaged, or from a newer version). */
class SyncFormatException(message: String) : IOException(message)

/**
 * Device snapshots as System.Text.Json writes them with JsonSerializerDefaults.Web (camelCase, nulls written, the
 * default encoder) and reads them (property names in any case), byte for byte (sync/manifest.json).
 */
object SyncJson {
    fun entry(e: SyncEntry): JsonObject = JsonObject(
        linkedMapOf("key" to JsonPrimitive(e.key), "path" to JsonPrimitive(e.path), "hash" to JsonPrimitive(e.hash), "since" to (e.since?.let(NetTime::json) ?: JsonNull)),
    )

    fun snapshot(s: DeviceSnapshot): JsonObject = JsonObject(
        linkedMapOf(
            "device" to JsonPrimitive(s.device),
            "name" to JsonPrimitive(s.name),
            "at" to NetTime.json(s.at),
            "entries" to JsonArray(s.entries.map(::entry)),
            "schema" to JsonPrimitive(s.schema),
            "deleted" to JsonArray(s.deleted.map { JsonObject(linkedMapOf("key" to JsonPrimitive(it.key), "hash" to JsonPrimitive(it.hash), "at" to NetTime.json(it.at))) }),
        ),
    )

    fun write(e: JsonElement): String = NetJson.write(e, JsonEscaping.DEFAULT)

    private fun prop(o: JsonObject, name: String): JsonElement? = o.entries.firstOrNull { Net.equalsCi(it.key, name) }?.value?.takeIf { it !is JsonNull }

    private fun string(o: JsonObject, name: String): String? = prop(o, name)?.let { NetJson.str(it) ?: throw SyncFormatException("\"$name\" isn't text.") }

    private fun time(o: JsonObject, name: String): java.time.Instant? = string(o, name)?.let { NetTime.parse(it) ?: throw SyncFormatException("\"$name\" isn't a time.") }

    /** A device snapshot, or a [SyncFormatException]. Missing fields read as nothing, like System.Text.Json. */
    fun readSnapshot(text: String): DeviceSnapshot = readSnapshot(NetJson.parseOrNull(text) ?: throw SyncFormatException("Not JSON."))

    fun readSnapshot(e: JsonElement): DeviceSnapshot {
        val o = e as? JsonObject ?: throw SyncFormatException("Not a device snapshot.")
        val entries = (prop(o, "entries") as? JsonArray)?.map { x ->
            val eo = x as? JsonObject ?: throw SyncFormatException("Not an entry.")
            SyncEntry(string(eo, "key") ?: "", string(eo, "path") ?: "", string(eo, "hash") ?: "", time(eo, "since"))
        }
        val deleted = (prop(o, "deleted") as? JsonArray)?.mapNotNull { x ->
            val t = x as? JsonObject ?: return@mapNotNull null
            val key = string(t, "key") ?: return@mapNotNull null
            val hash = string(t, "hash") ?: return@mapNotNull null
            SyncTombstone(key, hash, time(t, "at") ?: NetTime.MIN)
        } ?: emptyList()
        val schema = prop(o, "schema")?.let { NetJson.long(it)?.toInt() ?: throw SyncFormatException("\"schema\" isn't a number.") } ?: 1
        return DeviceSnapshot(string(o, "device") ?: "", string(o, "name") ?: "", time(o, "at") ?: NetTime.MIN, entries ?: NO_ENTRIES, schema, deleted)
    }

    /** Stands for "entries": null (a file that isn't a device snapshot). */
    val NO_ENTRIES: List<SyncEntry> = java.util.Collections.unmodifiableList(ArrayList())

    fun hasEntries(s: DeviceSnapshot): Boolean = s.entries !== NO_ENTRIES
}
