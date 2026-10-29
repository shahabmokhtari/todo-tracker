package dev.todotracker.core.sync

import java.time.Instant
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SyncScheduleTest {
    private val t0 = Instant.parse("2026-01-05T14:00:00Z")

    private fun at(seconds: Long) = t0.plusSeconds(seconds)

    @Test
    fun itSyncsAtOnceOnLaunchThenEveryMinute() {
        val s = SyncSchedule()
        assertTrue(s.due(at(0)))
        s.started(at(0))
        assertFalse(s.due(at(59)))
        assertTrue(s.due(at(60)))
    }

    @Test
    fun changesInARowWaitForTheLastOneToSettle() {
        val s = SyncSchedule()
        s.started(at(0))
        s.changed(at(10))
        assertFalse(s.due(at(13)))
        s.changed(at(13))
        assertFalse(s.due(at(17)))
        assertTrue(s.due(at(18)))
        s.started(at(18))
        assertFalse(s.due(at(30)))
    }

    @Test
    fun aChangeDuringASyncGetsASyncOfItsOwn() {
        val s = SyncSchedule()
        s.started(at(0))
        s.changed(at(1))
        // The sync that started before it finishes; the change still syncs 5 s after it was made.
        assertTrue(s.due(at(6)))
    }

    @Test
    fun comingBackOrSyncNowSyncsAtOnce() {
        val s = SyncSchedule()
        s.started(at(0))
        assertFalse(s.due(at(5)))
        s.soon()
        assertTrue(s.due(at(5)))
    }
}
