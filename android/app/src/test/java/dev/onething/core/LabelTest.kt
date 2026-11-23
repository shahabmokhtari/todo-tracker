package dev.onething.core

import java.io.File
import java.time.Instant
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/** Renaming, recoloring, removing and adding labels, the same in C#, Swift and Kotlin (tests/fixtures/labels.json). */
class LabelTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    private fun fixture() = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/labels.json").isFile) dir = dir.parentFile
        Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/labels.json").readText()).jsonObject
    }

    private fun snapshot(board: TaskBoard) =
        board.labels.joinToString(";") { "${it.name} ${it.color}" } + "|" + board.allItems.joinToString(";") { "${it.labels.joinToString(",")} ${it.icon ?: ""} ${it.color ?: ""}" }

    private fun JsonObject.text(key: String): String? = this[key]?.takeIf { it !is JsonNull }?.jsonPrimitive?.content

    @Test
    fun labelsChangeAsTheSharedFixtureSays() {
        val f = fixture()
        val board = BoardCodec.decode(f["board"]!!.toString())
        for (step in f["steps"]!!.jsonArray.map { it.jsonObject }) {
            val label = step.text("label") ?: ""
            val color = step.text("color")
            val change: () -> Unit = when (step.text("do")) {
                "update" -> { { board.updateLabel(label, step.text("name")!!, color!!, now = t0) } }
                "define" -> { { board.defineLabel(label, color, now = t0); Unit } }
                "remove" -> { { board.deleteLabel(label, now = t0) } }
                "set" -> { { board.setLabels(UUID.fromString(step.text("task")), step["names"]!!.jsonArray.map { it.jsonPrimitive.content }, now = t0) } }
                "icon" -> { { board.setIcon(UUID.fromString(step.text("task")), step.text("value"), now = t0) } }
                "color" -> { { board.setColor(UUID.fromString(step.text("task")), step.text("value"), now = t0) } }
                else -> error("$step")
            }
            val before = snapshot(board)
            if (step.containsKey("error")) {
                assertThrows(BoardException::class.java) { change() }
                assertEquals("a refused change changes nothing: $step", before, snapshot(board))
                continue
            }
            change()
            if (step.containsKey("icon")) {
                assertEquals("$step", step.text("icon"), board.get(UUID.fromString(step.text("task"))).icon)
                continue
            }
            if (step.containsKey("taskColor")) {
                assertEquals("$step", step.text("taskColor"), board.get(UUID.fromString(step.text("task"))).color)
                continue
            }
            val expected = step["labels"]!!.jsonArray.map { it.jsonObject }.map { "${it.text("name")} ${it.text("color")}" }
            assertEquals("$step", expected, board.labels.map { "${it.name} ${it.color}" })
            for ((id, names) in step["tasks"]!!.jsonObject) {
                assertEquals("$id after ${step.text("do")}", names.jsonArray.map { it.jsonPrimitive.content }, board.get(UUID.fromString(id)).labels.toList())
            }
        }
    }

    @Test
    fun addingALabelTheTaskHasLeavesItOn() {
        assertEquals(listOf("Urgent"), labelsAdding(listOf(), " Urgent "))
        assertEquals("typed again (any case): nothing to change", null, labelsAdding(listOf("Urgent"), "urgent"))
        assertEquals(listOf("Urgent", "Later"), labelsAdding(listOf("Urgent"), "Later"))
        assertEquals(null, labelsAdding(listOf("Urgent"), "  "))
    }

    @Test
    fun tagsAreCleanedAndEachKeptOnce() {
        val board = TaskBoard()
        val task = board.addTask(NewTask("Write"), now = t0)
        board.setTags(task.id, listOf("#deep work", "Deep Work", "errands"), now = t0)
        assertEquals(listOf("deep work", "errands"), task.tags.toList())
        assertThrows(BoardException::class.java) { board.setTags(task.id, listOf("123"), now = t0) }
        assertEquals(listOf("deep work", "errands"), task.tags.toList())
    }
}
