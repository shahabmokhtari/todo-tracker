package dev.onething.app

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The demo board shows only when a debug build is asked for it, and is left when the app opens without asking. */
class DemoModeTest {
    @Test
    fun `only a debug build asked for it shows the demo`() {
        assertTrue(DemoMode.wanted(debug = true, extra = true))
        assertFalse(DemoMode.wanted(debug = false, extra = true))
        assertFalse(DemoMode.wanted(debug = true, extra = null))
        assertFalse(DemoMode.wanted(debug = true, extra = false))
    }

    @Test
    fun `opening the app again from the launcher leaves the demo`() = assertEquals(false, DemoMode.next(on = true, debug = true, extra = null))

    @Test
    fun `asking for the demo again keeps it`() = assertNull(DemoMode.next(on = true, debug = true, extra = true))

    @Test
    fun `your own tasks stay unless the demo is asked for`() {
        assertNull(DemoMode.next(on = false, debug = true, extra = null))
        assertNull(DemoMode.next(on = false, debug = false, extra = true))
        assertEquals(true, DemoMode.next(on = false, debug = true, extra = true))
    }
}
