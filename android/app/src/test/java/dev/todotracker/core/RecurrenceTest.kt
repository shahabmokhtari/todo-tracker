package dev.todotracker.core

import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** Repeat rules shared with C# and Swift (tests/fixtures/repeat.json): reading, next days, and moving a task on. */
class RecurrenceTest {
    private fun fixture() = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/repeat.json").isFile) dir = dir.parentFile
        Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/repeat.json").readText()).jsonObject
    }

    private fun text(o: JsonObject, key: String) = o[key]!!.jsonPrimitive.content

    private fun instant(e: JsonElement?): Instant? = e?.let { BoardCodec.parseDate(it.jsonPrimitive.content) }

    @Test
    fun rulesReadAndWriteLikeTheSharedFixture() {
        for (c in fixture()["parse"]!!.jsonArray.map { it.jsonObject }) {
            val input = text(c, "text")
            if (c["error"] != null) {
                val e = assertThrows(input, Recurrence.RuleException::class.java) { Recurrence.parse(input) }
                assertTrue(e.message!!.contains("every day"))
                assertNull(Recurrence.parseOrNull(input))
                continue
            }
            val canonical = text(c, "canonical")
            assertEquals(input, canonical, Recurrence.parse(input).toString())
            assertEquals(canonical, Recurrence.parse(canonical).toString())
        }
    }

    @Test
    fun nextDaysMatchTheSharedFixture() {
        for (c in fixture()["next"]!!.jsonArray.map { it.jsonObject }) {
            val rule = Recurrence.parse(text(c, "rule"))
            val expected = if (c["next"] is JsonNull) null else LocalDate.parse(text(c, "next"))
            assertEquals("$rule after ${text(c, "after")}", expected, rule.next(LocalDate.parse(text(c, "anchor")), LocalDate.parse(text(c, "after"))))
        }
    }

    @Test
    fun repeatingTasksMoveOnLikeTheSharedFixture() {
        for (c in fixture()["roll"]!!.jsonArray.map { it.jsonObject }) {
            val name = text(c, "name")
            val board = TaskBoard().apply { zone = ZoneId.of(text(c, "zone")) }
            val now = instant(c["now"])!!
            val item = board.addTask(NewTask("Water the plants", deadline = instant(c["due"])), now = now.minusSeconds(30L * 86400))
            item.repeat = Recurrence.parse(text(c, "rule"))
            instant(c["scheduled"])?.let { item.nextActionAt = it }

            board.complete(item.id, now = now)

            val expect = c["expect"]!!.jsonObject
            assertEquals(name, expect["done"]!!.jsonPrimitive.boolean, item.isDone)
            if (item.isDone) continue
            assertEquals(name, instant(expect["due"]), item.deadline)
            assertEquals(name, instant(expect["scheduled"]), item.nextActionAt)
            assertEquals(name, text(expect, "rule"), item.repeat.toString())
        }
    }

    @Test
    fun doneTodayCountsATaskThatMovedOnAndReopenPutsItBack() {
        val now = Instant.parse("2026-01-05T10:00:00Z")
        val board = TaskBoard().apply { zone = ZoneId.of("UTC") }
        val item = board.addTask(NewTask("Stand-up notes", deadline = Instant.parse("2026-01-05T17:00:00Z")), now = now)
        item.repeat = Recurrence.parse("every day")

        board.complete(item.id, now = now)
        assertFalse(item.isDone)
        assertEquals(listOf(item), Agenda.doneToday(board, now.plusSeconds(60), ZoneId.of("UTC")))

        board.reopen(item.id, now = now.plusSeconds(120))
        assertEquals(Instant.parse("2026-01-05T17:00:00Z"), item.deadline)
        assertNull(item.nextActionAt)
        assertTrue(Agenda.doneToday(board, now.plusSeconds(180), ZoneId.of("UTC")).isEmpty())
    }

    @Test
    fun aRepeatingChecklistMovesOnWhenItsLastStepIsDoneAndUndoPutsBackWhatItHad() {
        val now = Instant.parse("2026-01-05T10:00:00Z")
        val board = TaskBoard().apply { zone = ZoneId.of("UTC") }
        val review = board.addTask(NewTask("Weekly review", deadline = Instant.parse("2026-01-02T17:00:00Z")), now = now)
        review.repeat = Recurrence.parse("every week")
        review.nextActionAt = Instant.parse("2026-01-02T08:30:00Z")
        val steps = board.addSteps(review.id, listOf("Inbox zero", "Plan the week"), null, now = now)

        board.complete(steps[0].id, now = now)
        board.complete(steps[1].id, now = now)
        assertFalse(review.isDone)
        assertEquals(Instant.parse("2026-01-09T17:00:00Z"), review.deadline)
        assertEquals("every week on Friday", review.repeat.toString())
        assertTrue(steps.none { it.isDone })

        board.reopen(review.id, now = now.plusSeconds(60))
        assertEquals(Instant.parse("2026-01-02T17:00:00Z"), review.deadline)
        assertEquals(Instant.parse("2026-01-02T08:30:00Z"), review.nextActionAt)
    }

    @Test
    fun theRuleSurvivesTheBoardFile() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Rent"), now = Instant.parse("2026-01-05T10:00:00Z"))
        item.repeat = Recurrence.parse("every month on the last day")

        assertEquals("every month on the last day", BoardCodec.decode(BoardCodec.encode(board)).get(item.id).repeat.toString())

        item.deadline = Instant.parse("2026-01-31T17:00:00Z")
        board.complete(item.id, now = Instant.parse("2026-01-31T10:00:00Z"))
        assertEquals(RepeatUndo(Instant.parse("2026-01-31T17:00:00Z"), null), BoardCodec.decode(BoardCodec.encode(board)).get(item.id).repeatUndo)
    }
}
