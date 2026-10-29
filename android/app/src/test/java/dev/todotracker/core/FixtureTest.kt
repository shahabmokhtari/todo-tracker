package dev.todotracker.core

import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.boolean
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Test

/** The shared fixtures for "not done after all" (tests/fixtures/undo.json) and search (search.json), as C# and Swift check them. */
class FixtureTest {
    private fun fixture(name: String): JsonObject {
        var dir: File? = File("").absoluteFile
        while (dir != null && !File(dir, "tests/fixtures/$name").isFile) dir = dir.parentFile
        return Json.parseToJsonElement(File(dir ?: error("tests/fixtures not found"), "tests/fixtures/$name").readText()).jsonObject
    }

    private fun JsonObject.text(key: String): String? = (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content
    private fun JsonObject.time(key: String): Instant? = text(key)?.let { BoardCodec.parseDate(it) }
    private fun JsonObject.strings(key: String): List<String> = (this[key] as? JsonArray)?.map { it.jsonPrimitive.content } ?: emptyList()
    private fun JsonObject.flag(key: String): Boolean = (this[key] as? JsonPrimitive)?.boolean ?: false

    @Test
    fun undoWorksLikeTheSharedFixture() {
        for (c in fixture("undo.json")["cases"]!!.jsonArray.map { it.jsonObject }) {
            val name = c.text("name")!!
            val board = TaskBoard().apply { zone = ZoneId.of(c.text("zone")) }
            val tasks = mutableMapOf<String, WorkItem>()
            val reminders = mutableMapOf<String, Reminder>()
            c["steps"]!!.jsonArray.map { it.jsonObject }.forEachIndexed { i, s ->
                val at = s.time("at")!!
                val where = "$name, step ${i + 1}"
                fun task() = tasks[s.text("task")]!!
                when (s.text("do")) {
                    "add" -> {
                        val item = board.addTask(
                            NewTask(s.text("title")!!, parentId = s.text("parent")?.let { tasks[it]!!.id }, deadline = s.time("due"), sequential = s.flag("inOrder")),
                            now = at,
                        )
                        s.text("repeat")?.let { board.update(item.id, TaskChanges(repeat = it), now = at) }
                        tasks[s.text("key")!!] = item
                    }
                    "complete" -> board.complete(task().id, now = at)
                    "reopen" -> board.reopen(task().id, now = at)
                    "archive" -> board.archive(task().id, now = at)
                    "remind" -> reminders[s.text("key")!!] = board.addReminder(task().id, s.time("due")!!, s.text("message"), now = at)
                    "check" -> check(board, s, at, tasks, reminders, where)
                    else -> error("$where: unknown step ${s["do"]}")
                }
            }
        }
    }

    private fun check(board: TaskBoard, s: JsonObject, at: Instant, tasks: Map<String, WorkItem>, reminders: Map<String, Reminder>, where: String) {
        (s["tasks"] as? JsonObject)?.forEach { (key, value) ->
            val item = tasks[key]!!
            val e = value.jsonObject
            val what = "$where: $key"
            if ("done" in e) assertEquals("$what done", e.flag("done"), item.isDone)
            if ("doneAt" in e) assertEquals("$what done at", e.time("doneAt"), item.completedAt)
            if ("due" in e) assertEquals("$what due", e.time("due"), item.deadline)
            if ("scheduled" in e) assertEquals("$what scheduled", e.time("scheduled"), item.nextActionAt)
            if ("rule" in e) assertEquals("$what rule", e.text("rule"), item.repeat?.toString())
            if ("archived" in e) assertEquals("$what archived", e.flag("archived"), item.archivedAt != null)
        }
        (s["reminders"] as? JsonObject)?.forEach { (key, value) ->
            assertEquals("$where: reminder $key pending", value.jsonObject.flag("pending"), reminders[key]!!.isPending)
        }
        if ("doNow" in s) {
            val keys = tasks.entries.associate { it.value.id to it.key }
            assertEquals("$where: Do now", s.strings("doNow"), Agenda.build(board, at).now.map { keys[it.item.id] ?: "?" })
        }
    }

    @Test
    fun searchFindsWhatTheSharedFixtureSays() {
        val f = fixture("search.json")
        val t0 = Instant.parse("2026-01-05T09:00:00Z")
        val board = TaskBoard()
        assertEquals(f.strings("groups"), board.groups.map { it.name })
        val tasks = mutableMapOf<String, WorkItem>()
        for (t in f["tasks"]!!.jsonArray.map { it.jsonObject }) {
            val group = t.text("group")?.let { g -> board.groups.single { it.name == g }.id }
            val item = board.addTask(NewTask(t.text("title")!!, parentId = t.text("parent")?.let { tasks[it]!!.id }, groupId = group, details = t.text("details")), now = t0)
            tasks[t.text("key")!!] = item
            // Tags, labels and the board column are kept as the other apps write them (this app only reads them).
            val extra = buildMap<String, JsonElement> {
                if (t.strings("tags").isNotEmpty()) put("tags", JsonArray(t.strings("tags").map(::JsonPrimitive)))
                if (t.strings("labels").isNotEmpty()) put("labels", JsonArray(t.strings("labels").map(::JsonPrimitive)))
                t.text("stage")?.let { put("stage", JsonPrimitive(it)) }
            }
            if (extra.isNotEmpty()) item.extra = JsonObject(extra)
            if (t.flag("done")) board.complete(item.id, now = t0)
            if (t.flag("archived")) board.archive(item.id, now = t0)
        }
        val keys: Map<UUID, String> = tasks.entries.associate { it.value.id to it.key }
        for (q in f["queries"]!!.jsonArray.map { it.jsonObject }) {
            val text = q.text("q")
            assertEquals("\"$text\"", q.strings("expect"), TaskSearch.parse(text).apply(board).map { keys[it.id] })
        }
    }

}
