package dev.onething.core

import java.io.File
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Deep links: the same rules as C# and Swift (tests/fixtures/links.json). */
class TaskLinksTest {
    private val fixture: JsonObject = run {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/links.json").isFile) dir = dir.parentFile
        Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/links.json").readText()).jsonObject
    }

    private val id = UUID.fromString(fixture["id"]!!.jsonPrimitive.content)

    private fun lines(name: String): List<String?> = fixture[name]!!.jsonArray.map { it.jsonPrimitive.contentOrNull }

    @Test
    fun aTaskLinkRoundTrips() {
        val link = TaskLinks.forTask(id)
        assertEquals(fixture["link"]!!.jsonPrimitive.content, link)
        assertEquals(TaskLinks.Target.Task(id), TaskLinks.target(link))
    }

    @Test
    fun otherShapesStillOpenTheTask() {
        assertTrue(lines("opensTheTask").isNotEmpty())
        lines("opensTheTask").forEach { assertEquals(it, TaskLinks.Target.Task(id), TaskLinks.target(it)) }
    }

    @Test
    fun theBareSchemeJustOpensTheApp() {
        assertTrue(lines("opensTheApp").isNotEmpty())
        lines("opensTheApp").forEach { assertEquals(it, TaskLinks.Target.App, TaskLinks.target(it)) }
    }

    @Test
    fun anythingElseIsIgnored() {
        assertTrue(lines("ignored").contains(null))
        lines("ignored").forEach { assertNull(it, TaskLinks.target(it)) }
    }
}
