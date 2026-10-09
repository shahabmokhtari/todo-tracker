package dev.todotracker.core

import java.io.File
import java.time.Duration
import java.time.Instant
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

/** Timers on tasks, the focus timer, and breaks: the same rules as the other apps (TimeTrackingTests.cs, breaks.json). */
class TimeTrackingTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    private fun at(minutes: Long): Instant = t0.plus(Duration.ofMinutes(minutes))

    @Test
    fun oneTimerRunsAtATime() {
        val board = TaskBoard()
        val a = board.addTask(NewTask("A"), now = t0)
        val b = board.addTask(NewTask("B"), now = t0)

        board.startTimer(a.id, now = t0)
        board.startTimer(b.id, now = at(10))

        assertEquals(Duration.ofMinutes(10), a.timeSpent(at(30)))
        assertEquals(b.id, board.runningTimer(at(30))?.item?.id)
        board.stopTimer(at(30))
        assertNull(board.runningTimer())
        assertEquals(Duration.ofMinutes(20), b.timeSpent(at(60)))
    }

    @Test
    fun aFocusSessionIsTimedAndEndsExactlyWhenTheSessionDoes() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Write report"), now = t0)

        board.startFocus(item.id, now = t0)
        assertEquals(TimeSource.FOCUS, board.runningTimer(at(1))?.entry?.source)
        board.tickPomodoro(at(28))

        assertEquals(1, item.timeEntries.size)
        assertEquals(at(25), item.timeEntries[0].end)
        assertNull(board.runningTimer())
        assertEquals(PomodoroPhase.SHORT_BREAK, board.pomodoro.phase)
        assertEquals(1, board.pomodoro.completedFocusCount)
    }

    @Test
    fun pausingFocusPausesTheTimerAndResumingStartsItAgain() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Write report"), now = t0)
        board.startFocus(item.id, now = t0)

        board.pauseFocus(at(10))
        board.resumeFocus(at(20))
        board.resetFocus(at(30))

        assertEquals(listOf(Duration.ofMinutes(10), Duration.ofMinutes(10)), item.timeEntries.map { it.duration(at(60)) })
        assertNull(board.runningTimer())
    }

    @Test
    fun skippingFocusStopsItsTimerButNotOneStartedByHandOnAnotherTask() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Write report"), now = t0)
        val other = board.addTask(NewTask("Inbox zero"), now = t0)
        board.startFocus(item.id, now = t0)
        board.skipFocus(at(5))
        assertNull(board.runningTimer())

        board.startTimer(other.id, now = at(6))
        board.tickPomodoro(at(60))

        assertEquals(other.id, board.runningTimer()?.item?.id)
    }

    @Test
    fun startNextFocusEndsTheBreakAndTimesTheSameTaskAgainButNeverRestartsARunningSession() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("Write report"), now = t0)
        board.startFocus(item.id, now = t0)
        board.tickPomodoro(at(26))

        assertEquals(item.id, board.startNextFocus(null, now = at(27))?.id)

        assertEquals(PomodoroPhase.FOCUS, board.pomodoro.phase)
        assertEquals(1, board.pomodoro.completedFocusCount)
        assertEquals(2, item.timeEntries.size)
        assertThrows(BoardException::class.java) { board.startNextFocus(null, now = at(30)) }
    }

    @Test
    fun aBreakThatRunsOutBySaysWhenASkippedOrPausedOneDoesnt() {
        val board = TaskBoard()
        board.startFocus(null, now = t0)
        board.tickPomodoro(at(31))
        assertEquals(at(30), board.pomodoro.breakEndedAt)

        board.startNextFocus(null, now = at(32))
        assertNull(board.pomodoro.breakEndedAt)
        board.tickPomodoro(at(32 + 26))
        board.skipFocus(at(32 + 27))
        assertNull(board.pomodoro.breakEndedAt)
    }

    @Test
    fun completingATaskStopsItsTimerAndADoneTaskCantBeTimed() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("A"), now = t0)
        board.startTimer(item.id, now = t0)

        board.complete(item.id, now = at(15))

        assertNull(board.runningTimer())
        assertEquals(Duration.ofMinutes(15), item.timeSpent(at(60)))
        assertThrows(BoardException::class.java) { board.startTimer(item.id, now = at(20)) }
    }

    @Test
    fun aForgottenTimerCountsTwelveHoursAtMost() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("A"), now = t0)
        board.startTimer(item.id, now = t0)

        assertNull(board.runningTimer(at(13 * 60)))
        assertEquals(Duration.ofHours(12), item.timeSpent(at(30 * 60)))
    }

    @Test
    fun timeAndTheFocusTimerAreSavedAndReadBack() {
        val board = TaskBoard()
        val item = board.addTask(NewTask("A"), now = t0)
        board.startFocus(item.id, now = t0)
        board.pauseFocus(at(10))
        board.startTimer(item.id, now = at(11))

        val again = BoardCodec.decode(BoardCodec.encode(board))
        val entries = again.get(item.id).timeEntries

        assertEquals(listOf(TimeSource.FOCUS, TimeSource.MANUAL), entries.map { it.source })
        assertEquals(at(10), entries[0].end)
        assertNull(entries[1].end)
        assertEquals(PomodoroPhase.FOCUS, again.pomodoro.phase)
        assertEquals(Duration.ofMinutes(15), again.pomodoro.pausedRemaining)
        assertEquals(item.id, again.pomodoro.itemId)
    }

    // ---- breaks.json: the same break rules as the web app, Windows and the Apple apps ----

    private fun fixtures(): File {
        var dir: File? = File("").absoluteFile
        while (dir != null) {
            val candidate = File(dir, "tests/fixtures")
            if (candidate.isDirectory) return candidate
            dir = dir.parentFile
        }
        error("tests/fixtures not found")
    }

    private fun date(e: JsonElement?): Instant? = (e?.takeIf { it !is JsonNull })?.jsonPrimitive?.content?.let { BoardCodec.parseDate(it) }

    @Test
    fun breaksDecideLikeTheOtherApps() {
        val cases = Json.parseToJsonElement(File(fixtures(), "breaks.json").readText()).jsonArray
        assertTrue(cases.size >= 16)
        for (c in cases.map { it.jsonObject }) {
            val name = c["name"]!!.jsonPrimitive.content
            val p = c["pomodoro"]!!.jsonObject
            val settings = PomodoroSettings(25, p["shortBreakMinutes"]!!.jsonPrimitive.intOrNull!!, p["longBreakMinutes"]!!.jsonPrimitive.intOrNull!!, p["focusesBeforeLongBreak"]!!.jsonPrimitive.intOrNull!!)
            val phase = PomodoroPhase.of(p["phase"]!!.jsonPrimitive.content)
            val running = p["running"]!!.jsonPrimitive.booleanOrNull!!
            val now = date(c["now"])!!
            val due = Breaks.due(phase, running, date(p["endsAt"]), p["completedFocusCount"]!!.jsonPrimitive.intOrNull!!, settings, now)
            val shown = due != null && due.until != date(c["dismissed"])
            val expect = c["expect"]!!.jsonObject

            assertEquals("$name: show", expect["show"]!!.jsonPrimitive.booleanOrNull, shown)
            if (shown) {
                assertEquals("$name: until", date(expect["until"]), due!!.until)
                assertEquals("$name: long", expect["long"]!!.jsonPrimitive.booleanOrNull, due.isLong)
                assertEquals("$name: tip", expect["tip"]!!.jsonPrimitive.content, due.tip)
            }
            (expect["over"])?.let { over ->
                val actual = Breaks.over(phase, running, date(p["endsAt"]), date(p["breakEndedAt"]), now, date(c["seen"]), date(c["dismissedOver"]))
                assertEquals("$name: over", over.jsonPrimitive.booleanOrNull, actual)
            }
        }
    }

    @Test
    fun theBreakClockCountsDownLikeTheOtherApps() {
        assertEquals("4:59", Breaks.clock(at(5), at(0).plusSeconds(1)))
        assertEquals("0:00", Breaks.clock(at(5), at(6)))
    }

    @Test
    fun theServersExportReadsItsTimeAndFocusTimer() {
        val board = BoardCodec.decode(File(fixtures(), "export.json").readText())
        val launch = board.items.first { it.title == "Launch the site" }

        assertEquals(Duration.ofMinutes(25), launch.timeSpent(Instant.now()))
        assertEquals(PomodoroPhase.IDLE, board.pomodoro.phase)
        assertEquals(25, board.pomodoro.settings.focusMinutes)
        // Nothing lost when it's written again.
        val again = Json.parseToJsonElement(BoardCodec.encode(board)).jsonObject
        assertTrue((again["items"]!!.jsonArray.first { it.jsonObject["title"]!!.jsonPrimitive.content == "Launch the site" }.jsonObject["children"]!!.jsonArray[0].jsonObject["time"]) != null)
        assertTrue(again["pomodoro"] is JsonObject)
    }
}
