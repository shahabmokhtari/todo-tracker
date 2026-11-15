package dev.onething.app

import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Test

/** Opening a task while another is open asks that one to save what's typed first (the tablet's side pane). */
class TaskOpeningTest {
    private val a = UUID.randomUUID()
    private val b = UUID.randomUUID()

    @Test
    fun `with nothing open, the task opens`() = assertEquals(TaskOpening.OPEN, TaskOpening.route(null, a))

    @Test
    fun `the open task again stays open`() = assertEquals(TaskOpening.OPEN, TaskOpening.route(a, a))

    @Test
    fun `another task waits for the open one to save first`() = assertEquals(TaskOpening.REQUEST, TaskOpening.route(a, b))
}
