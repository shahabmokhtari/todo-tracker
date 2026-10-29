package dev.todotracker.core.sync

import dev.todotracker.core.vault.Fixtures
import dev.todotracker.core.vault.TaskMarkdown
import dev.todotracker.core.vault.arr
import dev.todotracker.core.vault.obj
import dev.todotracker.core.vault.s
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** tests/fixtures/sync/textmerge.json and planner.json: two devices merge exactly as the C# core merges them. */
class SyncFixtureTest {
    @Test
    fun textMergesTheSameWay() {
        val fixture = Fixtures.read("sync/textmerge.json")
        val cases = fixture.arr("cases")
        assertTrue(cases.size >= 25)
        for (c in cases) {
            val o = c.jsonObject
            val policy = ConflictPolicy.valueOf(o.s("policy").uppercase())
            val keyed = o["lineKeys"].s() == "task"
            val result = TextMerge.merge(o.s("base"), o.s("first"), o.s("second"), if (keyed) SyncPlanner::taskLineKeys else null, policy)
            assertEquals(o.s("name"), o.s("text"), result.text)
            assertEquals(o.s("name"), o["conflicted"]!!.jsonPrimitive.boolean, result.conflicted)
        }
        for (k in fixture.arr("taskLineKeys")) {
            val o = k.jsonObject
            val lines = o.arr("lines").map { it.jsonPrimitive.content }
            assertEquals(o.arr("keys").map { it.s() }, SyncPlanner.taskLineKeys(lines))
        }
    }

    private fun entries(list: JsonArray, blobs: MutableMap<String, ByteArray>): Map<String, SyncEntry> {
        val map = LinkedHashMap<String, SyncEntry>()
        for (e in list) {
            val o = e.jsonObject
            val bytes = o.s("text").toByteArray(Charsets.UTF_8)
            val entry = SyncEntry(o.s("key"), o.s("path"), SyncHash.of(bytes))
            assertEquals(o.s("hash"), entry.hash)
            blobs[entry.hash] = bytes
            map[entry.key] = entry
        }
        return map
    }

    @Test
    fun everyPlanIsTheSameAsOnAComputer() {
        val fixture = Fixtures.read("sync/planner.json")
        val cases = fixture.arr("cases")
        assertTrue(cases.size >= 44)
        for (c in cases) {
            val o = c.jsonObject
            val name = o.s("name")
            val blobs = HashMap<String, ByteArray>()
            fun side(s: JsonObject) = SyncSide(s.s("device"), s.s("name"), entries(s.arr("entries"), blobs)) { blobs[it.hash]!! }
            val local = side(o.obj("local"))
            val peer = side(o.obj("peer"))
            val base = entries(o.arr("base"), blobs)
            val deleted = o.arr("deleted").map { it.jsonObject.s("key") to it.jsonObject.s("hash") }.toSet()
            val validate = o["validateTasks"]!!.jsonPrimitive.boolean
            val options = SyncPlanOptions(
                deleted = { (it.key to it.hash) in deleted },
                isValidTask = if (validate) { path, text -> TaskMarkdown.isValidTask(path, text, Fixtures.zone, Fixtures.now) } else { _, _ -> true },
            )
            val plan = SyncPlanner.plan(local, peer, base, { blobs[it] }, options)
            fun str(v: String?): JsonElement = v?.let(::JsonPrimitive) ?: JsonNull
            val ops = JsonArray(
                plan.ops.map { op ->
                    JsonObject(linkedMapOf("kind" to JsonPrimitive(op.kind.wire), "path" to JsonPrimitive(op.path), "expected" to str(op.expected), "to" to str(op.to), "text" to str(op.content?.let { String(it, Charsets.UTF_8) })))
                },
            )
            val conflicts = JsonArray(
                plan.conflicts.map { x ->
                    JsonObject(linkedMapOf("key" to JsonPrimitive(x.key), "path" to JsonPrimitive(x.path), "peer" to JsonPrimitive(x.peer), "peerName" to JsonPrimitive(x.peerName), "mine" to JsonPrimitive(x.mine), "theirs" to JsonPrimitive(x.theirs), "base" to str(x.base), "result" to JsonPrimitive(x.result)))
                },
            )
            val expected = o.obj("expected")
            assertEquals("$name: ops", expected["ops"], ops)
            assertEquals("$name: conflicts", expected["conflicts"], conflicts)
        }
    }

    @Test
    fun theNextBaseIsWhatBothAgreeOn() {
        for (c in Fixtures.read("sync/planner.json").arr("nextBase")) {
            val o = c.jsonObject
            val blobs = HashMap<String, ByteArray>()
            val next = SyncPlanner.nextBase(entries(o.arr("local"), blobs), entries(o.arr("peer"), blobs), entries(o.arr("base"), blobs))
            val actual = JsonArray(next.values.sortedBy { it.key }.map { JsonObject(linkedMapOf("key" to JsonPrimitive(it.key), "path" to JsonPrimitive(it.path), "hash" to JsonPrimitive(it.hash))) })
            assertEquals(o.s("name"), o["expected"], actual)
        }
    }
}
