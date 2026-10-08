package dev.todotracker.core

import java.io.File
import java.time.Instant
import java.time.ZoneId
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** Snooze choices and wording shared with C#, Swift and the web app (tests/fixtures/snooze.json), and "after another task". */
class SnoozeTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    private fun fixture() = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/snooze.json").isFile) dir = dir.parentFile
        Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/snooze.json").readText()).jsonObject
    }

    private fun instant(e: JsonElement?) = BoardCodec.parseDate(e!!.jsonPrimitive.content)!!

    @Test
    fun choicesMatchTheSharedFixture() {
        val cases = fixture()["choices"]!!.jsonArray
        assertTrue(cases.size >= 4)
        for (c in cases.map { it.jsonObject }) {
            val zone = ZoneId.of(c["zone"]!!.jsonPrimitive.content)
            val expected = c["expect"]!!.jsonArray.map { it.jsonObject }.map { "${it["id"]!!.jsonPrimitive.content} ${it["label"]!!.jsonPrimitive.content} ${instant(it["at"])}" }
            val actual = Snooze.choices(instant(c["now"]), zone).map { "${it.id} ${it.label} ${it.at}" }
            assertEquals(c["name"]!!.jsonPrimitive.content, expected, actual)
        }
    }

    @Test
    fun describeMatchesTheSharedFixture() {
        for (c in fixture()["describe"]!!.jsonArray.map { it.jsonObject }) {
            val zone = ZoneId.of(c["zone"]!!.jsonPrimitive.content)
            assertEquals(c["text"]!!.jsonPrimitive.content, Snooze.describe(instant(c["at"]), instant(c["now"]), zone))
        }
    }

    @Test
    fun waitingForAnotherTaskUntilItIsDone() {
        val board = TaskBoard()
        val keys = board.addTask(NewTask("Get the keys"), now = t0)
        val move = board.addTask(NewTask("Move in"), now = t0)
        board.scheduleNextAction(move.id, t0.plusSeconds(3600), notify = true, now = t0)

        board.waitFor(move.id, keys.id, now = t0)

        assertNull("waiting for a task replaces a snooze until a time", move.nextActionAt)
        assertEquals(ItemState.WAITING, Agenda.stateOf(move, t0))
        val dashboard = Agenda.build(board, t0)
        assertEquals(listOf(move.id), dashboard.waiting.map { it.item.id })
        assertEquals(keys.id, dashboard.waiting.first().waitingFor?.id)

        board.complete(keys.id, now = t0.plusSeconds(60))

        assertNull(move.afterId)
        val after = Agenda.build(board, t0.plusSeconds(60))
        assertEquals(move.id, after.focus?.item?.id)
        assertEquals("\"Get the keys\" is done: back to \"Move in\"", after.focus?.dueReminder?.message)
    }

    @Test
    fun waitForRefusesItselfItsOwnPartsAndCircles() {
        val board = TaskBoard()
        val a = board.addTask(NewTask("A"), now = t0)
        val child = board.addTask(NewTask("A child", parentId = a.id), now = t0)
        val b = board.addTask(NewTask("B"), now = t0)
        val done = board.addTask(NewTask("Done"), now = t0)
        board.complete(done.id, now = t0)
        board.waitFor(b.id, a.id, now = t0)

        assertThrows(BoardException::class.java) { board.waitFor(a.id, a.id, now = t0) }
        assertThrows(BoardException::class.java) { board.waitFor(a.id, child.id, now = t0) }
        assertThrows(BoardException::class.java) { board.waitFor(child.id, a.id, now = t0) }
        assertThrows(BoardException::class.java) { board.waitFor(a.id, b.id, now = t0) }
        assertThrows(BoardException::class.java) { board.waitFor(a.id, done.id, now = t0) }
        assertThrows(BoardException::class.java) { board.waitFor(done.id, a.id, now = t0) }
        assertFalse(board.canWaitFor(a.id, b.id))
    }

    @Test
    fun aCircleThroughAParentAndItsSubtaskIsRefused() {
        // P can't be done while its subtask C waits; so X waiting for P and C waiting for X would wait forever.
        val board = TaskBoard()
        val p = board.addTask(NewTask("P"), now = t0)
        val c = board.addTask(NewTask("C", parentId = p.id), now = t0)
        val x = board.addTask(NewTask("X"), now = t0)
        board.waitFor(x.id, p.id, now = t0)

        assertFalse(board.canWaitFor(c.id, x.id))
        assertThrows(BoardException::class.java) { board.waitFor(c.id, x.id, now = t0) }
    }

    @Test
    fun bringingBackEndsTheWaitAndDeletingTheOtherTaskReleasesIt() {
        val board = TaskBoard()
        val a = board.addTask(NewTask("A"), now = t0)
        val b = board.addTask(NewTask("B"), now = t0)
        val c = board.addTask(NewTask("C"), now = t0)
        board.waitFor(b.id, a.id, now = t0)
        board.waitFor(c.id, a.id, now = t0)

        board.clearNextAction(b.id, now = t0)
        assertNull(b.afterId)
        board.delete(a.id, now = t0)
        assertNull(c.afterId)
        assertEquals(ItemState.ACTIONABLE, Agenda.stateOf(c, t0))
        assertEquals("\"A\" was deleted: back to \"C\"", c.reminders.last().message)
    }

    @Test
    fun theWaitIsSavedAndReadBack() {
        val board = TaskBoard()
        val a = board.addTask(NewTask("A"), now = t0)
        val b = board.addTask(NewTask("B"), now = t0)
        board.waitFor(b.id, a.id, now = t0)

        val again = BoardCodec.decode(BoardCodec.encode(board))

        assertEquals(a.id, again.get(b.id).afterId)
        assertEquals(a.id, again.get(b.id).waitingFor?.id)
    }
}
