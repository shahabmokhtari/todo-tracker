package dev.onething.app

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** A swipe on a row does its action once, and only when the row really moved far enough. */
class SwipeGuardTest {
    private val width = 1000f

    @Test
    fun `a quick flick that barely moved the row does nothing`() {
        val guard = SwipeGuard()
        assertNull(guard.decide(SwipeGuard.Action.DONE, offset = 120f, width = width))
        assertNull(guard.decide(SwipeGuard.Action.LATER, offset = -200f, width = width))
    }

    @Test
    fun `a swipe past a third of the row does its action`() {
        assertEquals(SwipeGuard.Action.DONE, SwipeGuard().decide(SwipeGuard.Action.DONE, offset = 400f, width = width))
        assertEquals(SwipeGuard.Action.LATER, SwipeGuard().decide(SwipeGuard.Action.LATER, offset = -400f, width = width))
    }

    @Test
    fun `asked twice in one swipe, it acts once`() {
        val guard = SwipeGuard()
        assertEquals(SwipeGuard.Action.DONE, guard.decide(SwipeGuard.Action.DONE, offset = 500f, width = width))
        assertNull(guard.decide(SwipeGuard.Action.DONE, offset = 1000f, width = width))
    }

    @Test
    fun `once the row is back in place, the next swipe acts again`() {
        val guard = SwipeGuard()
        guard.decide(SwipeGuard.Action.DONE, offset = 500f, width = width)
        guard.moved(offset = 0f)
        assertEquals(SwipeGuard.Action.DONE, guard.decide(SwipeGuard.Action.DONE, offset = 500f, width = width))
    }

    @Test
    fun `a row moved the other way, or not measured yet, does nothing`() {
        assertNull(SwipeGuard().decide(SwipeGuard.Action.DONE, offset = -500f, width = width))
        assertNull(SwipeGuard().decide(SwipeGuard.Action.DONE, offset = 500f, width = 0f))
    }
}
