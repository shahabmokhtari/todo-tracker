package dev.onething.core

import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/** tests/fixtures/reports.json (written by the C# core): the reports for some ranges have the same numbers here as in TimeReport.cs. */
class ReportFixtureTest {
    private fun fixture(): JsonObject {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/reports.json").isFile) dir = dir.parentFile
        return Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/reports.json").readText()).jsonObject
    }

    private fun JsonObject.text(key: String): String? = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content

    private fun s(text: String?): JsonElement = text?.let(::JsonPrimitive) ?: JsonNull
    private fun n(value: Number) = JsonPrimitive(value)
    private fun at(time: Instant?) = s(time?.toString())

    /** The report as the fixture writes it. */
    private fun json(r: TimeReport): JsonElement = JsonObject(
        linkedMapOf(
            "trackedSeconds" to n(r.trackedSeconds), "focusSessions" to n(r.focusSessions), "completed" to n(r.completed), "created" to n(r.created),
            "activeDays" to n(r.activeDays), "longestStreak" to n(r.longestStreak),
            "days" to JsonArray(r.days.map { JsonObject(linkedMapOf("date" to s(it.date.toString()), "trackedSeconds" to n(it.trackedSeconds), "focusSeconds" to n(it.focusSeconds), "completed" to n(it.completed), "created" to n(it.created))) }),
            "groups" to JsonArray(r.groups.map { JsonObject(linkedMapOf("groupId" to s(it.groupId.toString()), "name" to s(it.name), "color" to s(it.color), "trackedSeconds" to n(it.trackedSeconds))) }),
            "tasks" to JsonArray(r.tasks.map { JsonObject(linkedMapOf("id" to s(it.id.toString()), "title" to s(it.title), "groupId" to s(it.groupId.toString()), "done" to JsonPrimitive(it.done), "trackedSeconds" to n(it.trackedSeconds))) }),
            "hourGrid" to JsonArray(r.hourGrid.map { row -> JsonArray(row.map(::n)) }),
            "timeline" to JsonArray(
                r.timeline.map { row ->
                    JsonObject(
                        linkedMapOf(
                            "id" to s(row.id.toString()), "title" to s(row.title), "groupId" to s(row.groupId.toString()), "createdAt" to at(row.createdAt),
                            "completedAt" to at(row.completedAt), "deadline" to at(row.deadline),
                            "work" to JsonArray(row.work.map { JsonObject(linkedMapOf("start" to at(it.start), "end" to at(it.end), "source" to s(it.source.wire))) }),
                        ),
                    )
                },
            ),
        ),
    )

    /** Times as instants, ids in lower case, numbers as numbers: written the same however the C# core wrote them. */
    private fun normalized(e: JsonElement): JsonElement = when (e) {
        is JsonObject -> JsonObject(e.mapValues { normalized(it.value) }.toSortedMap())
        is JsonArray -> JsonArray(e.map(::normalized))
        is JsonNull -> e
        is JsonPrimitive -> when {
            e.isString && runCatching { Instant.parse(e.content) }.isSuccess -> JsonPrimitive(Instant.parse(e.content).toString())
            e.isString && runCatching { UUID.fromString(e.content) }.isSuccess && e.content.length == 36 -> JsonPrimitive(e.content.lowercase())
            e.isString -> e
            e.doubleOrNull != null -> JsonPrimitive(e.doubleOrNull)
            else -> e
        }
    }

    @Test
    fun theReportsHaveTheNumbersOfTheSharedFixture() {
        val f = fixture()
        val zone = ZoneId.of(f.text("zone"))
        val board = BoardCodec.decode(f["board"].toString()).apply { this.zone = zone }
        val now = Instant.parse(f.text("now"))
        for (c in f["cases"]!!.jsonArray.map { it.jsonObject }) {
            val group = c.text("group")?.let(UUID::fromString)
            val report = TimeReport.build(board, LocalDate.parse(c.text("from")), LocalDate.parse(c.text("to")), zone, now, group)
            val label = "${c.text("from")}–${c.text("to")} ${c.text("group") ?: "all"}"
            assertEquals(label, normalized(c["expected"]!!).toString(), normalized(json(report)).toString())
        }
    }

    @Test
    fun aRangeIsAtMostTwoYearsAndEndsAfterItStarts() {
        val board = TaskBoard()
        val zone = ZoneId.of("Europe/Amsterdam")
        assertThrows(BoardException::class.java) { TimeReport.build(board, LocalDate.parse("2026-01-02"), LocalDate.parse("2026-01-01"), zone, Instant.now()) }
        assertThrows(BoardException::class.java) { TimeReport.build(board, LocalDate.parse("2024-01-01"), LocalDate.parse("2026-01-03"), zone, Instant.now()) }
        TimeReport.build(board, LocalDate.parse("2025-01-01"), LocalDate.parse("2026-12-31"), zone, Instant.now())
    }
}
