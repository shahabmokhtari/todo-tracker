package dev.onething.core

import java.io.File
import java.time.Instant
import java.time.ZoneId
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Test

/** Quick capture's tags and snooze words, as QuickCaptureParser in C# reads them. */
class QuickCaptureTest {
    private val zone = ZoneId.of("Europe/Amsterdam")

    // A Wednesday, 15:00 in Amsterdam.
    private val now = Instant.parse("2026-01-07T14:00:00Z")

    private fun parse(text: String) = QuickCaptureParser.parse(text, now, zone)

    @Test
    fun aHashWordIsATagAndLeavesTheTitle() {
        val capture = parse("Call mum #family #Errands")
        assertEquals("Call mum", capture.title)
        assertEquals(listOf("family", "Errands"), capture.tags)
    }

    @Test
    fun aTagInQuotesCanHaveSeveralWords() {
        assertEquals(listOf("deep work"), parse("Write the plan #\"deep work\"").tags)
        assertEquals("Write the plan", parse("Write the plan #\"deep  work\"").title)
        assertEquals(listOf("deep work"), parse("Write the plan #\u201Cdeep work\u201D").tags)
    }

    @Test
    fun wordsThatCantBeTagsStayInTheTitle() {
        val capture = parse("Fix #123 and ##heading #\"42\"")
        assertEquals("Fix #123 and ##heading #\"42\"", capture.title)
        assertEquals(emptyList<String>(), capture.tags)
    }

    @Test
    fun theSameTagTwiceIsOneTag() {
        assertEquals(listOf("work"), parse("Report #work #Work").tags)
    }

    @Test
    fun aWeekdaySnoozesUntilThatMorning() {
        assertEquals(Instant.parse("2026-01-09T08:00:00Z"), parse("Plan trip @fri").nextActionAt)
        // Today's weekday is next week's.
        assertEquals(Instant.parse("2026-01-14T08:00:00Z"), parse("Plan trip @wednesday").nextActionAt)
    }

    @Test
    fun theWeekendIsSaturdayMorning() {
        assertEquals(Instant.parse("2026-01-10T08:00:00Z"), parse("Clean the garage @weekend").nextActionAt)
    }

    @Test
    fun weeksCountFromNow() {
        assertEquals(Instant.parse("2026-01-21T14:00:00Z"), parse("Check in @2w").nextActionAt)
    }

    @Test
    fun wordsWithSpacesAreWrittenWithHyphens() {
        assertEquals(Instant.parse("2026-01-12T08:00:00Z"), parse("Review @next-week").nextActionAt)
        assertEquals(Instant.parse("2026-01-08T13:00:00Z"), parse("Call @tomorrow-14:00").nextActionAt)
        assertEquals("Review", parse("Review @next-week").title)
    }

    @Test
    fun minutesHoursAndDaysStillCountFromNow() {
        assertEquals(now.plusSeconds(2 * 3600), parse("x @2h").nextActionAt)
        assertEquals(now.plusSeconds(3 * 86_400), parse("x @3d").nextActionAt)
    }

    @Test
    fun aTimeAlreadyPastOrNotUnderstoodStaysInTheTitle() {
        val capture = parse("Meet @someday")
        assertEquals("Meet @someday", capture.title)
        assertNull(capture.nextActionAt)
        assertNull(parse("Meet @2025-01-01").nextActionAt)
    }

    @Test
    fun aJustCapturedTaskKeepsItsTags() {
        val board = TaskBoard()
        val capture = parse("Write #\"deep work\" #writing")
        val item = board.addTask(NewTask(capture.title, tags = capture.tags), now = now)
        assertEquals(listOf("deep work", "writing"), item.tags)
    }

    /**
     * tests/fixtures/quick-capture.json, shared by C#, Swift and Kotlin (written from the C# core). Skipped until it's
     * there.
     */
    @Test
    fun quickCaptureReadsLikeTheSharedFixture() {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures").isDirectory) dir = dir.parentFile
        val file = File(dir ?: error("tests/fixtures not found"), "tests/fixtures/quick-capture.json")
        assumeTrue("tests/fixtures/quick-capture.json isn't there yet", file.isFile)
        val f = Json.parseToJsonElement(file.readText()).jsonObject
        fun JsonObject.text(key: String) = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content
        val at = Instant.parse(f.text("now"))
        val zone = ZoneId.of(f.text("timeZone"))
        for (c in f["cases"]!!.jsonArray.map { it.jsonObject }) {
            val text = c.text("text")!!
            if (c.containsKey("error")) {
                try {
                    QuickCaptureParser.parse(text, at, zone)
                    fail("$text should have been refused")
                } catch (expected: BoardException) {
                    continue
                }
            }
            val capture = QuickCaptureParser.parse(text, at, zone)
            c.text("title")?.let { assertEquals(text, it, capture.title) }
            (c["tags"] as? JsonArray)?.let { tags -> assertEquals(text, tags.map { it.jsonPrimitive.content }, capture.tags) }
            if (c.containsKey("snoozeUntil")) assertEquals(text, c.text("snoozeUntil")?.let(Instant::parse), capture.nextActionAt)
            if (c.containsKey("deadline")) assertEquals(text, c.text("deadline")?.let(Instant::parse), capture.deadline)
            c.text("priority")?.let { assertEquals(text, Priority.of(it), capture.priority) }
        }
    }
}
