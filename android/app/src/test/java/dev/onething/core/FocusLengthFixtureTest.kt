package dev.onething.core

import java.io.File
import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * tests/fixtures/focus-lengths.json (written by the C# core): the focus timer's limits. A refused change changes
 * nothing; an allowed one adds the same line to the timeline, by whoever made it.
 */
class FocusLengthFixtureTest {
    private val fields = listOf("focusMinutes", "shortBreakMinutes", "longBreakMinutes", "focusesBeforeLongBreak")
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    private fun values(s: PomodoroSettings) = listOf(s.focusMinutes, s.shortBreakMinutes, s.longBreakMinutes, s.focusesBeforeLongBreak)
    private fun JsonObject.ints() = fields.map { this[it]!!.jsonPrimitive.int }

    @Test
    fun theFocusTimersLengthsAreLimitedAsTheSharedFixtureSays() {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/focus-lengths.json").isFile) dir = dir.parentFile
        val f = Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/focus-lengths.json").readText()).jsonObject
        val board = TaskBoard()
        assertEquals(f["start"]!!.jsonObject.ints(), values(board.pomodoro.settings))
        val limits = f["limits"]!!.jsonObject
        assertEquals(
            listOf(TaskBoard.MAX_FOCUS_MINUTES, TaskBoard.MAX_SHORT_BREAK_MINUTES, TaskBoard.MAX_LONG_BREAK_MINUTES, TaskBoard.MAX_FOCUSES_BEFORE_LONG_BREAK),
            fields.map { limits[it]!!.jsonArray[1].jsonPrimitive.int },
        )
        val agent = Actor(ActorKind.AGENT, "claude")
        for (step in f["steps"]!!.jsonArray.map { it.jsonObject }) {
            val (focus, short, long, sessions) = step["set"]!!.jsonObject.ints()
            val before = board.activity.size
            val change = { board.setFocusLengths(focus, short, long, sessions, agent, t0) }
            if (step["ok"]!!.jsonPrimitive.boolean) {
                change()
                assertEquals(before + 1, board.activity.size)
                assertEquals(step["summary"]!!.jsonPrimitive.content, board.activity.last().summary)
                assertEquals(agent, board.activity.last().actor)
            } else {
                assertThrows(BoardException::class.java) { change() }
                assertEquals("a refused change changes nothing", before, board.activity.size)
            }
            assertEquals("$step", step["settings"]!!.jsonObject.ints(), values(board.pomodoro.settings))
        }
    }
}
