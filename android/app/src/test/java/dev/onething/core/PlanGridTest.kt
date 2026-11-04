package dev.onething.core

import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

/** The Plan view's grid: the same days, hours and snapping as the web app (plan.js) and the iPhone (PlanGridTests.swift). */
class PlanGridTest {
    private val zone = ZoneId.of("Europe/Amsterdam")
    private fun day(text: String) = LocalDate.parse(text)
    private fun at(text: String) = Instant.parse(text)

    @Test
    fun `a week starts on Monday or Sunday and a day is itself`() {
        val wednesday = day("2026-10-28")
        assertEquals(day("2026-10-26"), PlanGrid.firstDay(wednesday, week = true))
        assertEquals(day("2026-10-25"), PlanGrid.firstDay(wednesday, week = true, weekStart = 7))
        assertEquals(day("2026-10-25"), PlanGrid.firstDay(day("2026-10-25"), week = true, weekStart = 7))
        assertEquals("Sunday is the end of a Monday week", day("2026-10-26"), PlanGrid.firstDay(day("2026-11-01"), week = true))
        assertEquals(wednesday, PlanGrid.firstDay(wednesday, week = false))
    }

    @Test
    fun `a week of work hours shows the work days and the full day shows all`() {
        val settings = PlanSettings()
        assertEquals(listOf("2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30"), PlanGrid.shownDays(day("2026-10-26"), settings).map { it.toString() })
        assertEquals(7, PlanGrid.shownDays(day("2026-10-26"), settings.copy(hours = PlanSettings.FULL)).size)
        assertEquals("a day is shown even when it's not a work day", listOf(day("2026-10-31")), PlanGrid.shownDays(day("2026-10-31"), settings.copy(view = PlanSettings.DAY)))
    }

    @Test
    fun `dropping and resizing snap to a quarter of an hour`() {
        assertEquals(9 * 60, PlanGrid.snapped(9 * 60 + 7.0))
        assertEquals(9 * 60 + 15, PlanGrid.snapped(9 * 60 + 7.5))
        assertEquals(0, PlanGrid.snapped(-20.0))
        assertEquals(24 * 60 - 15, PlanGrid.snapped(24 * 60 + 30.0))
        assertEquals(45, PlanGrid.resized(30, 20.0))
        assertEquals("at least a quarter of an hour", 15, PlanGrid.resized(30, -40.0))
        assertEquals("at most 12 hours", 720, PlanGrid.resized(600, 600.0))
    }

    @Test
    fun `a block moves by the drag on its new day snapped`() {
        assertEquals(at("2026-10-29T09:30:00Z"), PlanGrid.moved(at("2026-10-28T09:00:00Z"), 37.0, 1, zone))
        // Across the change to winter time: the same wall-clock time on the new day.
        assertEquals(at("2026-10-26T09:00:00Z"), PlanGrid.moved(at("2026-10-23T08:00:00Z"), 0.0, 3, zone))
    }

    @Test
    fun `a block across midnight is a part on each day`() {
        val parts = PlanGrid.segments(at("2026-10-30T22:30:00Z"), 90, zone) // Fri 23:30 to Sat 01:00
        assertEquals(
            listOf(
                PlanSegment(day("2026-10-30"), 23 * 60 + 30, 24 * 60, continues = false, isLast = false),
                PlanSegment(day("2026-10-31"), 0, 60, continues = true, isLast = true),
            ),
            parts,
        )
    }

    @Test
    fun `work hours stretch to show a block outside them`() {
        val settings = PlanSettings()
        val early = PlanGrid.segments(at("2026-10-28T06:30:00Z"), 30, zone) // 07:30
        val late = PlanGrid.segments(at("2026-10-29T17:00:00Z"), 45, zone) // 18:00–18:45
        val shown = listOf(day("2026-10-28"), day("2026-10-29"))
        assertEquals(7 * 60 to 19 * 60, PlanGrid.hours(settings, early + late, shown))
        assertEquals("only blocks on the days shown", 9 * 60 to 17 * 60, PlanGrid.hours(settings, early, listOf(day("2026-10-27"))))
        assertEquals(0 to 24 * 60, PlanGrid.hours(settings.copy(hours = PlanSettings.FULL), emptyList(), shown))
    }

    @Test
    fun `work hours are checked like the server does`() {
        assertNull(PlanSettings().problem)
        assertNotNull(PlanSettings(workEnd = 9 * 60 + 30).problem)
        assertNotNull(PlanSettings(workDays = emptyList()).problem)
        assertNotNull(PlanSettings(weekStart = 3).problem)
    }

    @Test
    fun `plan settings are kept and read back, and bad ones fall back to the defaults`() {
        val mine = PlanSettings(view = PlanSettings.DAY, hours = PlanSettings.FULL, workStart = 8 * 60, workEnd = 16 * 60, workDays = listOf(1, 2, 3, 4), weekStart = 7)
        assertEquals(mine, PlanSettings.decode(mine.encode()))
        assertEquals(PlanSettings(), PlanSettings.decode("""{"workStart":600,"workEnd":610}"""))
        assertEquals(PlanSettings(), PlanSettings.decode("not json"))
        assertEquals(PlanSettings(), PlanSettings.decode(null))
    }

    @Test
    fun `overlapping blocks go side by side`() {
        val d = day("2026-10-28")
        val (lanes, count) = PlanGrid.lanes(listOf(PlanSegment(d, 540, 600, false, true), PlanSegment(d, 570, 630, false, true), PlanSegment(d, 600, 660, false, true)))
        assertEquals(listOf(0, 1, 0), lanes)
        assertEquals(2, count)
    }

    @Test
    fun `plan a task starts at the next quarter of an hour`() {
        assertEquals(at("2026-10-28T09:15:00Z"), PlanGrid.nextQuarter(at("2026-10-28T09:07:00Z"), ZoneId.of("UTC")))
        assertEquals(at("2026-10-29T00:00:00Z"), PlanGrid.nextQuarter(at("2026-10-28T23:50:00Z"), ZoneId.of("UTC")))
        assertEquals("1 h 30 min", PlanGrid.length(90))
        assertEquals("09:05", PlanGrid.hhmm(545))
    }
}
