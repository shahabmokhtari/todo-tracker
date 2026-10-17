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
    fun `arranging Do now puts the skipped task second and keeps other tasks places`() {
        val board = TaskBoard()
        val a = board.addTask(NewTask("A", priority = Priority.HIGH), now = t0)
        val b = board.addTask(NewTask("B"), now = t0)
        val c = board.addTask(NewTask("C"), now = t0)
        assertEquals(listOf(a.id, b.id, c.id), Agenda.build(board, t0).now.map { it.item.id })

        board.arrangeNow(listOf(b.id, a.id, c.id), now = t0)
        assertEquals(listOf(b.id, a.id, c.id), Agenda.build(board, t0).now.map { it.item.id })
        assertEquals(t0, board.nowArrangedAt)

        // Only the arranged tasks' places are reused (another tab's order stays).
        board.arrangeNow(listOf(c.id, b.id), now = t0)
        assertEquals(listOf(c.id, a.id, b.id), board.nowOrder)
        assertThrows(BoardException::class.java) { board.arrangeNow(listOf(a.id, a.id), now = t0) }
    }

    @Test
    fun doneTodayListsWhatWasFinishedSinceLocalMidnightAndCheers() {
        val board = TaskBoard()
        val newYork = ZoneId.of("America/New_York")
        val morning = Instant.parse("2026-01-05T14:00:00Z") // 09:00 in New York
        val yesterday = board.addTask(NewTask("Yesterday's"), now = t0)
        val release = board.addTask(NewTask("Release"), now = t0)
        val ring0 = board.addTask(NewTask("Ring 0", parentId = release.id), now = t0)
        board.addTask(NewTask("Ring 1", parentId = release.id), now = t0)
        board.complete(yesterday.id, now = morning.minusSeconds(10 * 3600)) // 23:00 Sunday there
        board.complete(ring0.id, now = morning)
        board.complete(release.id, now = morning.plusSeconds(300))

        val done = Agenda.doneToday(board, morning.plusSeconds(3600), newYork)

        // Ring 1 was closed by finishing Release: shown once, as Release.
        assertEquals(listOf("Release", "Ring 0"), done.map { it.title })

        // An in-order task finishes itself with its last step: the step still counts (it was done by itself).
        val rollout = board.addTask(NewTask("Rollout", sequential = true), now = t0)
        val step1 = board.addTask(NewTask("Step 1", parentId = rollout.id), now = t0)
        val step2 = board.addTask(NewTask("Step 2", parentId = rollout.id), now = t0)
        board.complete(step1.id, now = morning.plusSeconds(600))
        board.complete(step2.id, now = morning.plusSeconds(1200))
        assertEquals(listOf("Rollout", "Step 2", "Step 1", "Release", "Ring 0"), Agenda.doneToday(board, morning.plusSeconds(3600), newYork).map { it.title })
        assertNull(Agenda.cheer(0))
        assertEquals("First one done. Nice start!", Agenda.cheer(1))
        assertEquals("3 done today. Keep it going!", Agenda.cheer(3))
        assertEquals("12 done today. What a day!", Agenda.cheer(12))
    }

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
        val e = assertThrows(BoardException::class.java) { BoardCodec.decode("""{"schemaVersion": 99}""") }
        assertTrue(e.isNewerSchema)
    }

    @Test
    fun theWrongShapesAreAnInvalidBoardNotACrash() {
        for (text in listOf("""{"items":[null]}""", """{"items":[{"id":"8b0f2c1e-1111-4a6b-9c1d-000000000001","title":"x","reminders":[1]}]}""", "[]")) {
            val e = assertThrows(text, BoardException::class.java) { BoardCodec.decode(text) }
            assertFalse(e.isNewerSchema)
        }
    }

    @Test
    fun whatOtherAppsKeepOnATaskIsWrittenBackAsItWas() {
        // Tags, labels, time from Windows or the server: this app doesn't use them, but never drops them.
        val text = """{"schemaVersion":1,"labels":[{"name":"Urgent","color":"#e11d48"}],"items":[{"id":"8b0f2c1e-1111-4a6b-9c1d-000000000001","title":"Launch","createdAt":"2026-01-05T09:00:00.000Z","tags":["deep work"],"stage":"doing"}]}"""

        val again = BoardCodec.encode(BoardCodec.decode(text))

        assertTrue(again.contains("\"deep work\""))
        assertTrue(again.contains("\"stage\": \"doing\""))
        assertTrue(again.contains("\"Urgent\""))
    }

    @Test
    fun aDamagedBoardWithoutAGoodCopyIsKeptAsideAndAFreshOneStarts() {
        val dir = Files.createTempDirectory("tt-android").toFile()
        try {
            File(dir, "board.json").writeText("{ not json")
            val store = BoardFileStore(File(dir, "board.json"))

            val board = store.load()

            assertTrue(board.items.isEmpty())
            assertNotNull(store.problem)
            assertTrue(dir.listFiles()!!.any { it.name.startsWith("board.json.corrupt-") })
        } finally {
            dir.deleteRecursively()
        }
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
