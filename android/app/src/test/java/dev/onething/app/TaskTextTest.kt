package dev.onething.app

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Test

/** A task's typed title and details: only a real change is saved (opening and closing a task never writes it). */
class TaskTextTest {
    @Test
    fun lineEndsAndSpacesAtTheEdgesAreNoChange() {
        assertEquals(sameText("Line one\nLine two"), sameText("  Line one\r\nLine two\n"))
        assertNotEquals(sameText("Line one\nLine two"), sameText("Line one\nLine 2"))
    }
}
