package dev.todotracker.app

import org.junit.Assert.assertEquals
import org.junit.Test

/** Shake to send feedback: a firm shake opens it once; a bump, or the phone lying still, never does. */
class ShakeDetectorTest {
    private val g = 9.80665f

    @Test
    fun twoJoltsWithinASecondAreAShakeAndTheNextTwoSecondsAreQuiet() {
        var shakes = 0
        val d = ShakeDetector { shakes++ }
        d.jolt(0f, 0f, g, 0)              // lying on the table
        d.jolt(3 * g, 0f, 0f, 1_000)      // one bump
        assertEquals(0, shakes)
        d.jolt(-3 * g, 0f, 0f, 1_300)     // and back: a shake
        assertEquals(1, shakes)
        d.jolt(3 * g, 0f, 0f, 1_600)
        d.jolt(-3 * g, 0f, 0f, 1_900)     // still the same shake
        assertEquals(1, shakes)
    }

    @Test
    fun joltsFarApartAreNotAShake() {
        var shakes = 0
        val d = ShakeDetector { shakes++ }
        d.jolt(3 * g, 0f, 0f, 0)
        d.jolt(3 * g, 0f, 0f, 5_000)
        d.jolt(0f, 2f * g, 0f, 5_400)     // under the threshold
        assertEquals(0, shakes)
    }
}
