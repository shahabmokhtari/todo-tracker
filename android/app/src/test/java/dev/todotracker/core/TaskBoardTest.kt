package dev.todotracker.core

import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TaskBoardTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    @Test
    fun stepsUnlockInOrderAfterTheirDelay() {
        val board = TaskBoard()
        val rollout = board.addTask(NewTask("Roll out"), now = t0)
        val steps = board.addSteps(rollout.id, listOf("Ring 0", "Ring 1"), Duration.ofHours(24), now = t0)

        assertThrows(BoardException::class.java) { board.complete(steps[1].id, now = t0) }
        board.complete(steps[0].id, now = t0)

        assertEquals(t0.plus(Duration.ofHours(24)), steps[1].nextActionAt)
        assertEquals(ItemState.WAITING, Agenda.stateOf(steps[1], t0))
        board.complete(steps[1].id, now = t0.plus(Duration.ofDays(1)))
        assertTrue("the parent finishes with its last step", rollout.isDone)
    }

    @Test
    fun snoozingDefersAndAReminderComesBack() {
        val board = TaskBoard()
        val task = board.addTask(NewTask("Call the bank"), now = t0)
        board.scheduleNextAction(task.id, t0.plusSeconds(3600), notify = true, now = t0)

        assertTrue(Agenda.build(board, t0).now.isEmpty())
        val later = Agenda.build(board, t0.plusSeconds(3601))
        assertEquals(task.id, later.focus?.item?.id)
        assertTrue(later.focus!!.needsAttention)

        board.dismissReminder(task.id, task.reminders.single().id, now = t0.plusSeconds(3602))
        assertFalse(Agenda.build(board, t0.plusSeconds(3603)).focus!!.needsAttention)
    }

    @Test
    fun theBoardRoundTripsThroughItsFileWithABackup() {
        val dir = Files.createTempDirectory("tt-android").toFile()
        try {
            val store = BoardFileStore(File(dir, "board.json"))
            val board = TaskBoard()
            val task = board.addTask(NewTask("Plan the trip", priority = Priority.HIGH, deadline = t0.plusSeconds(86_400)), now = t0)
            board.addNote(task.id, "Ask about Sunday", now = t0)
            store.save(board)
            store.save(board)

            val again = store.load()
            val read = again.get(task.id)
            assertEquals("Plan the trip", read.title)
            assertEquals(Priority.HIGH, read.priority)
            assertEquals("Ask about Sunday", read.notes.single().text)
            assertTrue(File(dir, "board.json.bak").exists())

            // An unreadable file: the backup is used, the file is set aside.
            File(dir, "board.json").writeText("{ not json")
            assertEquals("Plan the trip", store.load().get(task.id).title)
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun aBoardFromANewerVersionIsNeverReplaced() {
        assertThrows(BoardException::class.java) { BoardCodec.decode("""{"schemaVersion": 99}""") }
    }

    @Test
    fun quickCaptureReadsPriorityDeferAndDeadline() {
        val zone = ZoneId.of("UTC")
        val capture = QuickCaptureParser.parse("Call mum !! @2h due:tomorrow", t0, zone)

        assertEquals("Call mum", capture.title)
        assertEquals(Priority.CRITICAL, capture.priority)
        assertEquals(t0.plusSeconds(7200), capture.nextActionAt)
        assertEquals(Instant.parse("2026-01-06T17:00:00Z"), capture.deadline)
        assertEquals(Instant.parse("2026-01-06T09:00:00Z"), QuickCaptureParser.parse("x @tomorrow", t0, zone).nextActionAt)
        assertNull(QuickCaptureParser.parse("due:2026-02-30 x", t0, zone).deadline)
        assertThrows(BoardException::class.java) { QuickCaptureParser.parse("  !! ", t0, zone) }
        assertNotNull(QuickCaptureParser.parse("x due:3d", t0, zone).deadline)
    }
}
