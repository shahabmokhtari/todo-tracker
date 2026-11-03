package dev.onething.core

import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.fail
import org.junit.Test

/** tests/fixtures/plan.json (written by the C# core): what the Plan view shows for some days, and what planning does. */
class PlanFixtureTest {
    private class Loaded(val fixture: JsonObject, val board: TaskBoard, val zone: ZoneId, val now: Instant)

    private fun load(): Loaded {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/plan.json").isFile) dir = dir.parentFile
        val f = Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/plan.json").readText()).jsonObject
        val zone = ZoneId.of(f.text("zone"))
        val board = BoardCodec.decode(f["board"].toString()).apply { this.zone = zone }
        return Loaded(f, board, zone, Instant.parse(f.text("now")))
    }

    private fun JsonObject.text(key: String): String? = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content
    private fun id(text: String?) = text?.lowercase()
    private fun instant(at: Instant?) = at?.toString()
    private fun utc(text: String?) = text?.let { Instant.parse(it).toString() }

    @Test
    fun whileItsBlockIsOnItIsTheThingToDoNow() {
        val l = load()
        assertEquals(id(l.fixture.text("focus")), Agenda.build(l.board, l.now).focus?.item?.id?.toString())
    }

    @Test
    fun theDaysShowWhatTheSharedFixtureSays() {
        val l = load()
        for (c in l.fixture["cases"]!!.jsonArray.map { it.jsonObject }) {
            val from = LocalDate.parse(c.text("from"))
            val group = c.text("group")?.let(UUID::fromString)
            val plan = PlanDays.build(l.board, from, c["days"]!!.jsonPrimitive.int, l.zone, l.now, group)
            val label = "${c.text("from")} ${c["days"]} ${c.text("group") ?: "all"}"

            val blocks = c["blocks"]!!.jsonArray.map { it.jsonObject }
            assertEquals(label, blocks.map { id(it.text("id")) }, plan.blocks.map { it.id.toString() })
            for ((item, expected) in plan.blocks.zip(blocks)) {
                assertEquals(label, utc(expected.text("start")), instant(item.plannedAt))
                assertEquals(label, expected["minutes"]!!.jsonPrimitive.int, item.plannedLength)
                assertEquals(label, expected["done"]!!.jsonPrimitive.boolean, item.isDone)
            }
            assertEquals(label, c["deadlines"]!!.jsonArray.map { id(it.jsonPrimitive.content) }, plan.deadlines.map { it.id.toString() })
            assertEquals(label, c["unplanned"]!!.jsonArray.map { id(it.jsonPrimitive.content) }, plan.unplanned.map { it.item.id.toString() })
            val later = c["later"]!!.jsonArray.map { it.jsonObject }
            assertEquals(label, later.map { id(it.text("id")) }, plan.later.map { it.item.id.toString() })
            for ((entry, expected) in plan.later.zip(later)) {
                assertEquals(label, utc(expected.text("wakeAt")), instant(entry.wakeAt))
                assertEquals(label, id(expected.text("waitingFor")), entry.waitingFor?.id?.toString())
            }
            val energy = c["energy"]!!.jsonObject.mapValues { it.value.jsonPrimitive.double }
            assertEquals(label, energy, plan.energy.mapKeys { it.key.toString() })
        }
    }

    @Test
    fun planningATaskDoesWhatTheSharedFixtureSays() {
        val l = load()
        for (step in l.fixture["plan"]!!.jsonArray.map { it.jsonObject }) {
            val item = UUID.fromString(step.text("id"))
            try {
                val start = step.text("start")
                if (start != null) {
                    l.board.plan(item, Instant.parse(start), (step["minutes"] as? JsonPrimitive)?.intOrNull, now = l.now)
                } else {
                    l.board.unplan(item, now = l.now)
                }
                assertNull("$step", step.text("error"))
                val planned = l.board.get(item)
                assertEquals("$step", utc(step.text("plannedAt")), instant(planned.plannedAt))
                assertEquals("$step", (step["plannedMinutes"] as? JsonPrimitive)?.intOrNull, planned.plannedMinutes)
            } catch (e: BoardException) {
                val expected = step.text("error")
                val done = l.board.get(item).isDone
                if (!(expected == "length" && !done) && !(expected == "done" && done)) fail("$step: ${e.message}")
            }
        }
    }

    @Test
    fun planningSaysWhoAndWhenInTheTimeline() {
        val board = TaskBoard().apply { zone = ZoneId.of("Europe/Amsterdam") }
        val now = Instant.parse("2026-10-28T10:15:00Z")
        val task = board.addTask(NewTask("Deep work"), now = now)
        board.plan(task.id, Instant.parse("2026-10-29T08:00:00Z"), 90, Actor(ActorKind.AGENT, "claude"), now)
        assertEquals("Planned \"Deep work\" Thu 29 Oct 09:00, 90 min", board.activity.last().summary)
        assertEquals("scheduled", board.activity.last().kind)
        assertEquals(Actor(ActorKind.AGENT, "claude"), board.activity.last().actor)
        board.unplan(task.id, now = now)
        assertEquals("Took \"Deep work\" off the plan", board.activity.last().summary)
        assertNull(BoardCodec.decode(BoardCodec.encode(board)).find(task.id)?.plannedAt)
    }
}
