package dev.todotracker.core.sync

import dev.todotracker.core.Actor
import dev.todotracker.core.NewTask
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.vault.VaultStore
import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phones as sync devices of their own: two tasks folders meet through a folder or a gist and end with the same files. */
class SyncEngineTest {
    private val dir: File = Files.createTempDirectory("tt-sync").toFile()
    private var now = Instant.parse("2026-01-05T14:00:00Z")

    @After
    fun cleanUp() {
        dir.deleteRecursively()
    }

    private inner class Device(val name: String, remote: (Device) -> SyncRemote, legacy: TaskBoard? = null) {
        val root = File(dir, "$name/vault")
        val store = VaultStore.open(root, ZoneOffset.UTC, { now }, legacy)
        val state = SyncState(File(dir, "$name/sync"), File(dir, "$name/device"), name)
        val engine = SyncEngine(store, remote(this), state) { now }

        fun sync(): SyncResult {
            now = now.plus(Duration.ofSeconds(1))
            return engine.sync()
        }

        fun tasks(): Map<String, String> = root.walkTopDown().filter { it.isFile && it.extension == "md" && it.parentFile != root }
            .associate { it.relativeTo(root).invariantSeparatorsPath to it.readText() }

        fun titles(): List<String> = store.read { b -> b.items.map { it.title }.sorted() }
    }

    private val shared = File(dir, "shared")

    private fun folderDevice(name: String, legacy: TaskBoard? = null) = Device(name, { FolderRemote(shared) }, legacy)

    @Test
    fun twoPhonesEndWithTheSameFiles() {
        val a = folderDevice("Laptop")
        val b = folderDevice("Phone")
        val trip = a.store.update { board ->
            board.addTask(NewTask("Plan the trip", groupId = board.groups[0].id), Actor.USER, now).also {
                board.addSteps(it.id, listOf("Book flights", "Book hotel", "Pack"), null, Actor.USER, now)
            }
        }
        a.sync()
        b.sync()
        assertEquals(a.tasks(), b.tasks())
        assertEquals(listOf("Plan the trip"), b.titles())
        a.sync()

        // One checks a step, the other renames another: both changes land on both.
        val steps = trip.children.map { it.id }
        a.store.update { it.complete(steps[0], Actor.USER, now) }
        b.store.update { it.update(steps[2], title = "Pack light", actor = Actor.USER, now = now) }
        a.sync()
        b.sync()
        a.sync()
        assertEquals(a.tasks(), b.tasks())
        val (done, packed) = b.store.read { board -> board.get(steps[0]).isDone to board.get(steps[2]).title }
        assertTrue(done)
        assertEquals("Pack light", packed)
        assertEquals(a.engine.state.status.conflicts, emptyList<SyncConflictRecord>())
    }

    @Test
    fun aTaskDeletedOnOneGoesToTheOthersTrash() {
        val a = folderDevice("Laptop")
        val b = folderDevice("Phone")
        val task = a.store.update { it.addTask(NewTask("Old idea", groupId = it.groups[1].id), Actor.USER, now) }
        a.sync()
        b.sync()
        assertEquals(listOf("Old idea"), b.titles())
        // The laptop hears back from the phone first: a delete counts against what both agreed on.
        a.sync()
        a.store.update { it.delete(task.id, Actor.USER, now) }
        a.sync()
        b.sync()
        assertEquals(emptyList<String>(), b.titles())
        assertTrue(File(b.root, ".todo-tracker/trash").list()!!.any { it.endsWith("Old idea.md") })
        // Nothing changed since: a sync does nothing at all.
        assertTrue(b.sync().changes == 0)
    }

    @Test
    fun aGistIsCheckedWithItsETag() {
        val github = FakeGitHub()
        var gistId: String? = null
        val remote = { _: Device -> GistRemote(github, { "token" }, { gistId }, { gistId = it }) }
        val a = Device("Laptop", remote)
        val b = Device("Phone", remote)
        a.store.update { it.addTask(NewTask("From the laptop", groupId = it.groups[0].id), Actor.USER, now) }
        a.sync()
        b.sync()
        assertEquals(listOf("From the laptop"), b.titles())
        a.sync()
        assertEquals(a.tasks(), b.tasks())
        val quiet = github.notModified
        val result = b.sync()
        assertTrue(result.skipped)
        assertTrue(github.notModified > quiet)
        assertEquals(1, github.ids.size)
    }

    @Test
    fun theFirstJoinBringsThePhonesOwnTasksInBesideWhatCame() {
        val a = folderDevice("Laptop")
        val shared = a.store.update { it.addTask(NewTask("Shared already", groupId = it.groups[0].id), Actor.USER, now) }
        a.sync()

        // The phone used remote mode before: its board.json has a copy of the laptop's task, and one of its own.
        val legacy = TaskBoard()
        legacy.items.add(dev.todotracker.core.WorkItem(shared.id, "Shared already", dev.todotracker.core.Priority.NORMAL, now, legacy.groups[0].id))
        legacy.addTask(NewTask("Only on the phone", groupId = legacy.groups[1].id), Actor.USER, now)
        val b = folderDevice("Phone")
        b.sync()
        assertEquals(1, b.store.importBeside(legacy))
        assertEquals(listOf("Only on the phone", "Shared already"), b.titles())
        assertFalse(b.tasks().keys.any { it.contains("from") })
        b.sync()
        a.sync()
        assertEquals(listOf("Only on the phone", "Shared already"), a.titles())
        assertEquals(a.tasks(), b.tasks())
    }
}
