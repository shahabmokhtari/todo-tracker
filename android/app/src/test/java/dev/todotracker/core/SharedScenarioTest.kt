package dev.todotracker.core

import java.io.File
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The agenda scenarios every app checks (tests/fixtures/scenarios: C#, Swift and this one), so Android can't drift
 * from what Windows, the web and the iPhone show.
 */
class SharedScenarioTest {
    private fun fixtures(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null) {
            val candidate = File(dir, "tests/fixtures")
            if (candidate.isDirectory) return candidate
            dir = dir.parentFile
        }
        error("tests/fixtures not found")
    }

    private fun ids(array: JsonArray) = array.map { UUID.fromString(it.jsonPrimitive.content) }

    @Test
    fun everyScenarioGivesTheSameAgendaAsTheOtherApps() {
        val files = File(fixtures(), "scenarios").listFiles { f -> f.name.endsWith(".json") }!!.sortedBy { it.name }
        assertTrue("expected scenarios", files.size >= 5)
        for (file in files) {
            val root = Json.parseToJsonElement(file.readText()).jsonObject
            val now = BoardCodec.parseDate(root["now"]!!.jsonPrimitive.content)!!
            val board = BoardCodec.decode(root["board"].toString())
            val group = (root["group"] as? kotlinx.serialization.json.JsonPrimitive)?.content?.let(UUID::fromString)
            val expect = root["expect"]!!.jsonObject

            val dashboard = Agenda.build(board, now, group)

            val name = file.name
            assertEquals(name, ids(expect["now"]!!.jsonArray), dashboard.now.map { it.item.id })
            assertEquals(name, ids(expect["waiting"]!!.jsonArray), dashboard.waiting.map { it.item.id })
            assertEquals(name, ids(expect["attention"]!!.jsonArray), dashboard.now.filter { it.needsAttention }.map { it.item.id })
            val focus = expect["focus"]
            assertEquals(name, if (focus == null || focus is JsonNull) null else UUID.fromString(focus.jsonPrimitive.content), dashboard.focus?.item?.id)
            for ((id, state) in (expect["states"] as JsonObject)) {
                assertEquals("$name $id", state.jsonPrimitive.content, Agenda.stateOf(board.get(UUID.fromString(id)), now).wire)
            }
        }
    }

    @Test
    fun theServersExportReads() {
        // The board as Todo Tracker's server sends it (written by the C# tests), with everything newer apps add.
        val board = BoardCodec.decode(File(fixtures(), "export.json").readText())

        val launch = board.items.first { it.title == "Launch the site" }
        assertEquals(Priority.HIGH, launch.priority)
        assertEquals(listOf("Write the copy", "Pick images"), launch.children.map { it.title })
        assertEquals(setOf(ActorKind.VAULT, ActorKind.CONNECTOR), launch.notes.map { it.author.kind }.toSet())
    }
}
