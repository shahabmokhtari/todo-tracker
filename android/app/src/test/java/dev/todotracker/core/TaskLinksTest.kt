package dev.todotracker.core

import java.util.UUID
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Deep links: the same rules as the C# TaskLinkTests. */
class TaskLinksTest {
    private val id = UUID.fromString("4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f")

    @Test
    fun aTaskLinkRoundTrips() {
        val link = TaskLinks.forTask(id)
        assertEquals("todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", link)
        assertEquals(TaskLinks.Target.Task(id), TaskLinks.target(link))
    }

    @Test
    fun otherShapesStillOpenTheTask() {
        listOf(
            "TodoTracker://Task/4F9D1C2E-3B7A-4E10-9C55-0A1B2C3D4E5F",
            "todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/",
            "todotracker://task/4f9d1c2e3b7a4e109c550a1b2c3d4e5f?from=teams",
            "todotracker:task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f",
            "  todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f  ",
        ).forEach { assertEquals(it, TaskLinks.Target.Task(id), TaskLinks.target(it)) }
    }

    @Test
    fun theBareSchemeJustOpensTheApp() {
        listOf("todotracker://", "todotracker:", "todotracker://open").forEach { assertEquals(it, TaskLinks.Target.App, TaskLinks.target(it)) }
    }

    @Test
    fun anythingElseIsIgnored() {
        listOf(
            null, "", "https://example.com/#/task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", "todotracker://task/not-a-guid",
            "todotracker://task/", "todotracker://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f/../../settings",
            "todotracker://settings/delete-everything", "todotracker://task/{4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f}", "--data",
        ).forEach { assertNull(it, TaskLinks.target(it)) }
    }
}
