package dev.onething.app

import dev.onething.core.Agenda
import dev.onething.core.BoardColumn
import dev.onething.core.BoardScope
import java.time.Instant
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The made-up board for screenshots fills every part of the app. */
class DemoBoardTest {
    private val zone = ZoneId.of("UTC")
    private val now = Instant.parse("2026-11-14T10:20:00Z")
    private val board = DemoBoard.board(now, zone)

    @Test
    fun `today has one thing to do now, more to do, things coming up and things done`() {
        val dashboard = Agenda.build(board, now, null)
        assertEquals("Reply to the security review", dashboard.focus?.item?.title)
        assertTrue(dashboard.now.size > 3)
        assertTrue(dashboard.waiting.size >= 2)
        assertTrue(Agenda.doneToday(board, now, zone, null).isNotEmpty())
        assertTrue(Agenda.workstreams(dashboard).isNotEmpty())
    }

    @Test
    fun `every board column has cards, and a task is being timed`() {
        val columns = BoardScope(board, null).columns(now)
        for (column in BoardColumn.entries) assertTrue("$column is empty", columns[column].orEmpty().isNotEmpty())
        assertNotNull(board.runningTimer(now))
        assertTrue(BoardScope(board, null).archivedRoots.isNotEmpty())
    }
}
