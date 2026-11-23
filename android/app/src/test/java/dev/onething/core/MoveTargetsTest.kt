package dev.onething.core

import java.time.Instant
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Move… in a task's details: the web's Move dialog rules (app.js moveDialog), as MoveTargets.swift has them. */
class MoveTargetsTest {
    private val t0: Instant = Instant.parse("2026-01-05T09:00:00Z")

    @Test
    fun aTaskGoesOnItsOwnOrUnderAnyOpenTaskButNeverItselfOrItsOwnSubtasks() {
        val board = TaskBoard()
        val work = board.groups[0].id
        val trip = board.addTask(NewTask("Trip", groupId = work), now = t0)
        val hotel = board.addTask(NewTask("Hotel", parentId = trip.id), now = t0)
        val room = board.addTask(NewTask("Room", parentId = hotel.id), now = t0)
        val booked = board.addTask(NewTask("Booked", parentId = trip.id), now = t0)
        board.complete(booked.id, now = t0)
        val old = board.addTask(NewTask("Old", groupId = work), now = t0)
        board.complete(old.id, now = t0)

        assertEquals(listOf("On its own", "Trip"), MoveTargets.options(board, hotel, work).map { it.title })
        assertEquals(listOf("On its own", "Trip", "Hotel"), MoveTargets.options(board, room, work).map { it.title })
        assertEquals(listOf(0, 0, 1), MoveTargets.options(board, room, work).map { it.depth })
        assertEquals(listOf("On its own", "Hotel"), MoveTargets.options(board, room, work, "hot").map { it.title })

        assertNull(MoveTargets.move(hotel, work, trip.id))
        assertEquals(BoardMove.Move(hotel.id, null, work), MoveTargets.move(hotel, work, null))
        assertNull(MoveTargets.move(trip, work, null))
        val home = board.groups[1].id
        assertEquals(BoardMove.Move(trip.id, null, home), MoveTargets.move(trip, home, null))
        assertEquals(BoardMove.Move(room.id, trip.id), MoveTargets.move(room, work, trip.id))
    }
}
