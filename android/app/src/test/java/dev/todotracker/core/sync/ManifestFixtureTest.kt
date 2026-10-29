package dev.todotracker.core.sync

import dev.todotracker.core.vault.Fixtures
import dev.todotracker.core.vault.arr
import dev.todotracker.core.vault.obj
import dev.todotracker.core.vault.s
import java.io.File
import java.nio.file.Files
import java.time.Instant
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

/** tests/fixtures/sync/manifest.json: what a device publishes, byte for byte, in a shared folder and in a gist. */
class ManifestFixtureTest {
    private val fixture = Fixtures.read("sync/manifest.json")
    private val contents = fixture.obj("contents").mapValues { it.value.jsonPrimitive.content.toByteArray(Charsets.UTF_8) }

    /** The snapshot the C# core published, built here from scratch. */
    private fun snapshot(): DeviceSnapshot {
        fun entry(key: String, path: String, text: String, since: String): SyncEntry {
            val bytes = text.toByteArray(Charsets.UTF_8)
            return SyncEntry(key, path, SyncHash.of(bytes), Instant.parse(since))
        }
        return DeviceSnapshot(
            "0a1b2c3d4e5f60718293a4b5c6d7e8f9",
            "Shahab's iPhone",
            Instant.parse("2026-01-05T14:00:00Z"),
            listOf(
                entry(SyncKeys.ACTIVITY, SyncKeys.ACTIVITY_PATH, "{\"at\":\"2026-01-05T09:00:00+00:00\",\"itemId\":\"7b0c2f9e-0000-4000-8000-000000000001\",\"kind\":\"created\",\"summary\":\"Created \\u0022Plan\\u0022\",\"actorKind\":\"user\"}\n", "2026-01-05T09:00:00Z"),
                entry(SyncKeys.ORDER, SyncKeys.ORDER_PATH, "[\"7b0c2f9e-0000-4000-8000-000000000001\"]", "2026-01-05T09:00:00.123Z"),
                entry(SyncKeys.file("Work/_img/photo.png"), "Work/_img/photo.png", "not really a png", "2026-01-05T09:00:00Z"),
                entry("t/7b0c2f9e000040008000000000000001", "Work/Plan.md", "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# Plan\n", "2026-01-05T09:00:00Z"),
            ),
            deleted = listOf(SyncTombstone("t/7b0c2f9e000040008000000000000009", SyncHash.of("# Old\n".toByteArray()), Instant.parse("2026-01-04T18:30:00Z"))),
        )
    }

    @Test
    fun aFolderGetsTheSameDeviceFileAndBlobs() {
        val dir = Files.createTempDirectory("tt-folder").toFile()
        try {
            val snapshot = snapshot()
            for (e in snapshot.entries) assertEquals(e.path, true, contents.containsKey(e.hash))
            FolderRemote(dir).publish(snapshot, { contents[it.hash]!! }, null)
            val folder = fixture.obj("folder")
            assertEquals(folder.s("device"), File(dir, folder.s("path")).readText())
            val blobs = File(dir, "blobs").walkTopDown().filter { it.isFile }.map { it.relativeTo(dir).invariantSeparatorsPath }.sorted().toList()
            assertEquals(folder.arr("blobs").map { it.jsonPrimitive.content }, blobs)

            // What another device reads back is the snapshot published.
            val back = FolderRemote(dir).read("someoneelse", null)!!
            assertEquals(listOf(snapshot), back.devices)
            assertEquals(folder.s("device"), SyncJson.write(SyncJson.snapshot(back.devices[0])))
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun aGistGetsTheSameBundleAndMarker() {
        val github = FakeGitHub()
        var gistId: String? = null
        val gist = GistRemote(github, { "token" }, { gistId }, { gistId = it }, "Todo Tracker")
        gist.publish(snapshot(), { contents[it.hash]!! }, null)
        val expected = fixture.obj("gist")
        val files = github.files(gistId!!)
        assertEquals(expected.arr("files").map { it.jsonPrimitive.content }, files.keys.sorted())
        assertEquals(expected.s("marker"), files[GistRemote.MARKER])
        assertEquals(expected.s("bundle"), files["device-0a1b2c3d4e5f60718293a4b5c6d7e8f9.json"])
        assertEquals(expected.s("description"), GistRemote.DESCRIPTION_PREFIX + "Todo Tracker")

        // Another device reads it back, contents and all.
        val other = GistRemote(github, { "token" }, { gistId }, {}, "Todo Tracker")
        val back = other.read("someoneelse", null)!!
        assertEquals(listOf(snapshot()), back.devices)
        for (e in back.devices[0].entries) assertEquals(String(contents[e.hash]!!), String(other.readContent(back.devices[0], e)))
    }
}
