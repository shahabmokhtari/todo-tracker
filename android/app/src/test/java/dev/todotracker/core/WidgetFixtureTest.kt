package dev.todotracker.core

import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * tests/fixtures/widget.json (written by the C# core): what the home-screen widget shows over the next day, and at some
 * moments. The same rules as WidgetSnapshot.cs and the Swift port.
 */
class WidgetFixtureTest {
    private fun fixture(): JsonObject {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/widget.json").isFile) dir = dir.parentFile
        return Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/widget.json").readText()).jsonObject
    }

    private fun ids(value: Any?): List<UUID> = (value as? JsonArray)?.map { UUID.fromString(it.jsonPrimitive.content) } ?: emptyList()
    private fun time(o: JsonObject, key: String): Instant = BoardCodec.parseDate(o[key]!!.jsonPrimitive.content)!!

    private fun load(): Triple<JsonObject, TaskBoard, ZoneId> {
        val f = fixture()
        val zone = ZoneId.of(f["zone"]!!.jsonPrimitive.content)
        val board = BoardCodec.decode(f["board"].toString()).apply { this.zone = zone }
        return Triple(f, board, zone)
    }

    @Test
    fun theSnapshotIsWhatTheSharedFixtureSays() {
        val (f, board, zone) = load()
        val expected = WidgetSnapshot.fromJson(f["snapshot"]!!.jsonObject)!!
        val built = WidgetSnapshot.build(board, time(f, "now"), zone)
        assertEquals(expected.items, built.items)
        expected.frames.zip(built.frames).forEach { (a, b) -> assertEquals("frame at ${a.at}", a, b) }
        assertEquals(expected, built)
    }

    @Test
    fun theSnapshotWritesTheSharedFormat() {
        val (f, board, zone) = load()
        assertEquals(f["snapshot"], WidgetSnapshot.build(board, time(f, "now"), zone).toJson())
    }

    @Test
    fun theWidgetShowsWhatTheSharedFixtureSays() {
        val (f, _, _) = load()
        val snapshot = WidgetSnapshot.fromJson(f["snapshot"]!!.jsonObject)!!
        for (v in f["views"]!!.jsonArray.map { it.jsonObject }) {
            val view = snapshot.viewAt(time(v, "at"), ids(v["hidden"]).toSet())
            val label = "${v["at"]} hiding ${v["hidden"]}"
            assertEquals(label, (v["focus"] as? JsonPrimitive)?.takeIf { it.isString }?.content?.let(UUID::fromString), view.focus?.id)
            assertEquals(label, ids(v["next"]), view.next.map { it.id })
            assertEquals(label, v["more"]!!.jsonPrimitive.int, view.more)
            assertEquals(label, ids(v["today"]), view.today.map { it.id })
            val later = v["later"]!!.jsonArray.map { it.jsonObject }
            assertEquals(label, later.map { UUID.fromString(it["id"]!!.jsonPrimitive.content) to time(it, "at") }, view.later.map { it.first.id to it.second })
            assertEquals(label, v["nowCount"]!!.jsonPrimitive.int, view.nowCount)
            assertEquals(label, v["laterCount"]!!.jsonPrimitive.int, view.laterCount)
        }
    }

    @Test
    fun nothingButTitlesGroupsIdsAndTimesGoesIntoTheSnapshot() {
        val (f, board, zone) = load()
        assertFalse(WidgetSnapshot.build(board, time(f, "now"), zone).toJson().toString().contains("secret"))
    }

    @Test
    fun theWidgetKnowsWhenItNextChanges() {
        val (f, board, zone) = load()
        val now = time(f, "now")
        val snapshot = WidgetSnapshot.build(board, now, zone)
        assertEquals(snapshot.frames[1].at, snapshot.nextChange(now))
        assertNull(snapshot.nextChange(snapshot.frames.last().at))
    }

    @Test
    fun anEmptyBoardShowsNothingToDo() {
        val view = WidgetSnapshot.build(TaskBoard(), Instant.now(), ZoneId.of("UTC")).viewAt(Instant.now())
        assertNull(view.focus)
        assertEquals(0, view.more)
    }
}

/** Done on the widget: only a task that's to do now is marked done (so a second tap, or an old widget, does nothing). */
class WidgetDoneTest {
    private val start: Instant = Instant.parse("2026-11-02T08:00:00Z")

    @Test
    fun aTaskToDoNowCanBeMarkedDoneFromTheWidget() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Answer mail"), now = start)
        assertEquals(true, WidgetSnapshot.canMarkDone(board, item.id, start))
    }

    @Test
    fun aSecondTapOnARepeatingTaskDoesNotSkipItsNextTime() {
        val board = TaskBoard().apply { zone = ZoneId.of("Europe/Amsterdam") }
        val item = board.addTask(NewTask("Stretch"), now = start)
        board.update(item.id, TaskChanges(repeat = "every day"), now = start)
        board.complete(item.id, now = start.plusSeconds(60))
        assertFalse("it moved on to tomorrow", WidgetSnapshot.canMarkDone(board, item.id, start.plusSeconds(61)))
    }

    @Test
    fun aDoneOrMissingTaskOrOneWithOpenSubtasksIsNotMarkedDone() {
        val board = TaskBoard()
        val done = board.addTask(NewTask("Old"), now = start)
        board.complete(done.id, now = start)
        val parent = board.addTask(NewTask("Move house"), now = start)
        board.addTask(NewTask("Pack books", parentId = parent.id), now = start)
        assertFalse(WidgetSnapshot.canMarkDone(board, done.id, start))
        assertFalse(WidgetSnapshot.canMarkDone(board, UUID.randomUUID(), start))
        assertFalse(WidgetSnapshot.canMarkDone(board, parent.id, start))
    }
}

class WidgetRemoteDoneTest {
    @Test
    fun a_repeating_task_done_from_the_widget_on_a_connected_phone_is_not_done_again_before_the_computer_answers() {
        val now = Instant.parse("2026-11-02T09:00:00Z")
        val board = TaskBoard()
        val task = board.addTask(NewTask("Water the plants"), now = now.minusSeconds(3600))
        board.update(task.id, TaskChanges(repeat = "every day"), now = now.minusSeconds(3600))
        val cached = BoardCodec.encode(board)
        assertEquals(true, WidgetSnapshot.canMarkDone(BoardCodec.decode(cached), task.id, now))

        // Sent to the computer: the board as last read moves on too, so a second tap finds nothing to do.
        val after = WidgetSnapshot.markedDone(cached, task.id, now)!!
        assertFalse(WidgetSnapshot.canMarkDone(BoardCodec.decode(after), task.id, now))
    }
}
