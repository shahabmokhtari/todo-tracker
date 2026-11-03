package dev.onething.core

import java.time.Duration
import java.time.Instant
import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Every change says who made it: arranging, energy, timers and focus land on the timeline with their actor (as C#'s AttributionTests). */
class AttributionTest {
    private val t0 = Instant.parse("2026-01-05T09:00:00Z")
    private val bot = Actor(ActorKind.AGENT, "helper")
    private val board = TaskBoard()

    private fun task(title: String, parent: UUID? = null) = board.addTask(NewTask(title, parentId = parent), Actor.USER, t0)

    private fun min(n: Long) = t0.plus(Duration.ofMinutes(n))

    private val last: ActivityEntry get() = board.activity.last()

    @Test
    fun arrangingDoNowSaysWhoArrangedIt() {
        val a = task("A")
        val b = task("B")
        board.arrangeNow(listOf(b.id, a.id), bot, min(1))
        assertEquals(bot, last.actor)
        assertEquals("moved", last.kind)
        assertTrue(last.summary.contains("\"B\""))
    }

    @Test
    fun reorderingSiblingsSaysWhoMovedTheTask() {
        val trip = task("Trip")
        val flights = task("Flights", trip.id)
        val visa = task("Visa", trip.id)
        board.reorder(visa.id, flights.id, bot, min(1))
        assertEquals(Triple(bot, visa.id, "moved"), Triple(last.actor, last.itemId, last.kind))
        board.reorderAfter(visa.id, flights.id, Actor.USER, min(2))
        assertEquals(Actor.USER to visa.id, last.actor to last.itemId)
    }

    @Test
    fun changingADaysEnergySaysWhoChangedIt() {
        board.setEnergyPerDay(6, bot, t0)
        assertEquals(bot, last.actor)
        assertEquals(UUID(0, 0), last.itemId)
        assertTrue(last.summary.contains("6"))
    }

    @Test
    fun stoppingTheTimerSaysWhoStoppedItAndNoTimerChangesNothing() {
        val item = task("Write report")
        val before = board.activity.size
        assertNull(board.stopTimer(bot, t0))
        assertEquals(before, board.activity.size)
        board.startTimer(item.id, Actor.USER, t0)
        board.stopTimer(bot, min(25))
        assertEquals(Triple(bot, item.id, "timeLogged"), Triple(last.actor, last.itemId, last.kind))
        assertEquals("Stopped the timer on \"Write report\" (25 min)", last.summary)
    }

    @Test
    fun pausingResumingAndEndingFocusSayWhoDidIt() {
        val item = task("Deep work")
        board.startFocus(item.id, Actor.USER, t0)
        board.pauseFocus(bot, min(5))
        assertEquals(bot to item.id, last.actor to last.itemId)
        board.resumeFocus(Actor.USER, min(6))
        assertEquals(Actor.USER to item.id, last.actor to last.itemId)
        board.skipFocus(bot, min(10))
        assertEquals(bot to item.id, last.actor to last.itemId)
        board.startFocus(item.id, Actor.USER, min(20))
        board.resetFocus(bot, min(22))
        assertEquals(bot to item.id, last.actor to last.itemId)
    }

    @Test
    fun aTimerTheAppEndsByItselfIsPutDownToTheApp() {
        val item = task("Forgot me")
        board.startTimer(item.id, Actor.USER, t0)
        assertEquals(1, board.closeForgottenTimers(t0.plus(Duration.ofDays(2))))
        assertEquals(Triple(Actor.SYSTEM, item.id, "timeLogged"), Triple(last.actor, last.itemId, last.kind))
        assertEquals("Stopped a timer left running on \"Forgot me\" (counted 12 h 00 min)", last.summary)
    }
}
