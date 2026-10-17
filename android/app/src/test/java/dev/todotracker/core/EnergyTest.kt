package dev.todotracker.core

import java.io.File
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.double
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

/** A day's energy, the same in C# (Energy.cs), Swift and Kotlin (tests/fixtures/energy.json). */
class EnergyTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    private fun fixture() = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/energy.json").isFile) dir = dir.parentFile
        Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/energy.json").readText()).jsonObject
    }

    @Test
    fun aDayTakesWhatTheSharedFixtureSays() {
        val f = fixture()
        val board = BoardCodec.decode(f["board"]!!.toString())
        val zone = ZoneId.of(f["zone"]!!.jsonPrimitive.content)
        assertEquals(f["perDay"]!!.jsonPrimitive.int, board.dailyEnergy)
        for (day in f["days"]!!.jsonArray.map { it.jsonObject }) {
            val date = LocalDate.parse(day["day"]!!.jsonPrimitive.content)
            assertEquals(date.toString(), day["planned"]!!.jsonPrimitive.double, Energy.plannedOn(board, date, zone), 0.0)
        }
    }

    @Test
    fun aTaskTakesOneToThreeAnHourAndTheDayHasTenUnlessSet() {
        val board = TaskBoard()
        val task = board.addTask(NewTask("Deep work"), now = t0)
        assertEquals(1, Energy.of(task))
        board.setEnergy(task.id, 3, now = t0)
        assertEquals(3, Energy.of(task))
        assertThrows(BoardException::class.java) { board.setEnergy(task.id, 4, now = t0) }
        assertEquals(10, board.dailyEnergy)
        board.setEnergyPerDay(6)
        val again = BoardCodec.decode(BoardCodec.encode(board))
        assertEquals(6, again.dailyEnergy)
        assertEquals(3, again.find(task.id)?.energy)
        assertThrows(BoardException::class.java) { board.setEnergyPerDay(0) }
    }

    @Test
    fun todaysLineReadsLikeTheOtherAppsAndWaitsForSomethingToSay() {
        val board = TaskBoard()
        val utc = ZoneId.of("UTC")
        val task = board.addTask(NewTask("Design doc"), now = t0)
        assertNull(Energy.today(board, t0, utc))
        board.setEnergy(task.id, 2, now = t0)
        task.plannedAt = t0
        task.plannedMinutes = 90
        assertEquals("⚡ 3 of 10 planned today.", Energy.today(board, t0, utc))
        assertEquals("⚡ 6 of 4 planned today: 2 too much.", Energy.line(6.0, 4))
        assertEquals("⚡ 4.5 of 10 planned today.", Energy.line(4.5, 10))
    }
}
