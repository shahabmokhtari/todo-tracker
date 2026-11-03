package dev.onething.core.sync

import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** What can't sync (too big, a link, a name this side can't use) is left alone on both sides: never taken as deleted. */
class SyncHeldTest {
    private val photo = "Work/_img/photo.png"
    private val bytes = "a photo".toByteArray()
    private val entry = SyncEntry(SyncKeys.file(photo), photo, SyncHash.of(bytes))
    private val now = Instant.parse("2026-01-05T14:00:00Z")

    private fun plan(peerEntries: Map<String, SyncEntry>, held: List<SyncHeld>): SyncPlan = SyncPlanner.plan(
        SyncSide("aaaa", "Phone", mapOf(entry.key to entry)) { bytes },
        SyncSide("bbbb", "Laptop", peerEntries) { bytes },
        mapOf(entry.key to entry),
        { bytes },
        SyncPlanOptions(held = held),
    )

    @Test
    fun aFileThePeerLeftOutIsDeletedOnlyWhenItWasntHeldBack() {
        // Gone on the peer, unchanged here: a delete...
        assertEquals(listOf(SyncOpKind.DELETE), plan(emptyMap(), emptyList()).ops.map { it.kind })
        // ...unless the peer only held it back (or this side can't take its name): then it stays.
        assertTrue(plan(emptyMap(), listOf(SyncHeld(entry.key, "", "held back on Laptop"))).ops.isEmpty())
    }

    @Test
    fun aHeldPathHereIsNeverWrittenOver() {
        val other = SyncEntry(SyncKeys.file("Work/_img/Photo.png"), "Work/_img/Photo.png", SyncHash.of("another".toByteArray()))
        val plan = SyncPlanner.plan(
            SyncSide("aaaa", "Phone", emptyMap()) { bytes },
            SyncSide("bbbb", "Laptop", mapOf(other.key to other)) { "another".toByteArray() },
            emptyMap(),
            { null },
            SyncPlanOptions(held = listOf(SyncHeld(entry.key, photo, "it's a link to somewhere else"))),
        )
        assertEquals(listOf("Work/_img/Photo 2.png"), plan.ops.map { it.path })
    }

    @Test
    fun aPeersEntryWithANameThisPhoneCantUseIsHeldNotDropped() {
        val good = SyncEntry(SyncKeys.file(photo), photo, SyncHash.of(bytes))
        val bad = SyncEntry(SyncKeys.file("Work/con.txt:secret"), "Work/con.txt:secret", SyncHash.of(bytes))
        val checked = SyncEngine.check(DeviceSnapshot("bbbbbbbb", "Laptop", now, listOf(good, bad)))!!
        assertEquals(setOf(good.key), checked.entries.keys)
        assertEquals(listOf(bad.key), checked.rejected.map { it.key })
    }

    @Test
    fun whatAPeerHoldsBackIsWithheldEvenIfItStillListsIt() {
        val checked = SyncEngine.check(DeviceSnapshot("bbbbbbbb", "Laptop", now, listOf(entry), held = listOf(entry.key)))!!
        assertTrue(checked.entries.isEmpty())
        assertEquals(listOf(SyncHeld(entry.key, photo, "it can't sync from there right now")), checked.rejected)
    }

    @Test
    fun heldKeysAreWrittenOnlyWhenThereAreSomeAndReadBack() {
        val plain = SyncJson.snapshot(DeviceSnapshot("bbbbbbbb", "Laptop", now, listOf(entry)))
        assertFalse(plain.containsKey("held"))
        assertNull(SyncJson.readSnapshot(SyncJson.write(plain)).held)
        val held = SyncJson.snapshot(DeviceSnapshot("bbbbbbbb", "Laptop", now, listOf(entry), held = listOf("f/Work/big.mov")))
        assertEquals(listOf("f/Work/big.mov"), (held["held"] as JsonArray).map { (it as JsonPrimitive).content })
        assertEquals(listOf("f/Work/big.mov"), SyncJson.readSnapshot(SyncJson.write(held)).held)
    }

    @Test
    fun twoTabsWithTheSameIdInAConfigMergeKeepTheFirstInsteadOfFailing() {
        fun json(text: String) = Json.parseToJsonElement(text)
        val base = json("""[{"id":"a","name":"Work"},{"id":"a","name":"Work again"}]""")
        val mine = json("""[{"id":"a","name":"Work"},{"id":"a","name":"Work again"},{"id":"b","name":"Home"}]""")
        val theirs = json("""[{"id":"a","name":"Job"},{"id":"a","name":"Job"}]""")
        val merged = SyncPlanner.mergeNode(base, mine, theirs)!!.jsonArray
        assertEquals(listOf("a", "b"), merged.map { ((it as JsonObject)["id"] as JsonPrimitive).content })
    }

    @Test
    fun settingsAPeerCantReadArentTakenHere() {
        val bad = "{ not json".toByteArray()
        val config = SyncEntry(SyncKeys.CONFIG, SyncKeys.CONFIG_PATH, SyncHash.of(bad))
        val plan = SyncPlanner.plan(
            SyncSide("aaaa", "Phone", emptyMap()) { bad },
            SyncSide("bbbb", "Laptop", mapOf(config.key to config)) { bad },
            emptyMap(),
            { null },
        )
        assertTrue(plan.ops.isEmpty())
    }

    @Test
    fun aHugeTextIsntDiffedButKeptWhole() {
        val base = (1..3000).joinToString("") { "line $it\n" }
        val first = base.replace("line 10\n", "line ten\n")
        val second = base.replace("line 2990\n", "line two thousand nine hundred ninety\n")
        val started = System.nanoTime()
        val result = TextMerge.merge(base, first, second)
        assertTrue(result.conflicted)
        assertTrue(result.tooLarge)
        assertEquals(first, result.text)
        assertTrue("took ${(System.nanoTime() - started) / 1_000_000} ms", System.nanoTime() - started < 5_000_000_000L)
        // One side's change alone is simply taken.
        assertEquals(TextMergeResult(first, false), TextMerge.merge(base, first, base))
    }
}
