package dev.todotracker.core.vault

import dev.todotracker.core.BoardCodec
import java.io.File
import java.nio.file.Files
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

/**
 * tests/fixtures/vault/render.json: a board brought into a new tasks folder gives exactly the files a computer writes
 * (task files, config.json, state.json, activity.jsonl), the same sync entries, and reads back as the same board.
 */
class VaultRenderFixtureTest {
    private val cases = Fixtures.read("vault/render.json").arr("cases")

    private fun temp(): File = Files.createTempDirectory("tt-vault").toFile()

    private fun filesUnder(root: File): Map<String, String> =
        root.walkTopDown().filter { it.isFile }.map { it.relativeTo(root).invariantSeparatorsPath }
            .filter { !it.startsWith(".todo-tracker/trash/") }.sorted().associateWith { File(root, it).readText() }

    @Test
    fun aBoardBecomesExactlyTheFilesAComputerWrites() {
        for (c in cases) {
            val o = c.jsonObject
            val name = o.s("name")
            val dir = temp()
            try {
                val root = File(dir, "vault")
                val legacy = BoardCodec.decode(o["board"].toString())
                val store = VaultStore.open(root, Fixtures.zone, { Fixtures.now }, legacy)
                val expected = o.obj("files").mapValues { it.value.jsonPrimitive.content }
                val actual = filesUnder(root)
                assertEquals("$name: file names", expected.keys.sorted(), actual.keys.sorted())
                for ((path, text) in expected) assertEquals("$name: $path", text, actual[path])
                val folders = root.listFiles()!!.filter { it.isDirectory && it.name[0] != '.' && it.name[0] != '_' }.map { it.name }.sorted()
                assertEquals("$name: folders", o.arr("folders").map { it.jsonPrimitive.content }, folders)

                val snapshot = store.readSync()
                val sync = snapshot.entries.values.sortedBy { it.key }.map { e ->
                    val m = linkedMapOf<String, kotlinx.serialization.json.JsonElement>("key" to JsonPrimitive(e.key), "path" to JsonPrimitive(e.path), "hash" to JsonPrimitive(e.hash))
                    if (!File(root, e.path).exists()) m["content"] = JsonPrimitive(String(snapshot.read(e), Charsets.UTF_8))
                    JsonObject(m)
                }
                assertEquals("$name: sync entries", o["sync"], JsonArray(sync))
                assertEquals("$name: read back", o["parsed"], Json.parseToJsonElement(store.read { BoardCodec.encode(it) }))
            } finally {
                dir.deleteRecursively()
            }
        }
    }

    @Test
    fun theFilesReadBackAsTheBoardTheyCameFrom() {
        for (c in cases) {
            val o = c.jsonObject
            val name = o.s("name")
            val dir = temp()
            try {
                val root = File(dir, "vault")
                for (folder in o.arr("folders")) File(root, folder.jsonPrimitive.content).mkdirs()
                for ((path, text) in o.obj("files")) {
                    val f = File(root, path)
                    f.parentFile!!.mkdirs()
                    f.writeText(text.jsonPrimitive.content)
                }
                val before = filesUnder(root)
                val store = VaultStore.open(root, Fixtures.zone, { Fixtures.now })
                assertEquals("$name: read back", o["parsed"], Json.parseToJsonElement(store.read { BoardCodec.encode(it) }))
                assertEquals("$name: reading wrote nothing", before, filesUnder(root))

                for ((path, text) in o.obj("files")) {
                    if (!path.endsWith(".md")) continue
                    val context = Fixtures.context(path)
                    val parsed = TaskMarkdown.parse(text.jsonPrimitive.content, Net.stem(path), context)
                    assertFalse("$name: $path would be rewritten", parsed.needsWrite)
                    assertEquals("$name: $path", text.jsonPrimitive.content, TaskMarkdown.render(parsed.root, context, parsed))
                }
            } finally {
                dir.deleteRecursively()
            }
        }
    }
}
