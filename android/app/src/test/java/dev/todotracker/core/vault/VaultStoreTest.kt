package dev.todotracker.core.vault

import dev.todotracker.core.Actor
import dev.todotracker.core.NewTask
import java.io.File
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The phone's tasks folder: the files are the truth, a change writes only what it touched, nothing is lost. */
class VaultStoreTest {
    private val dir: File = Files.createTempDirectory("tt-store").toFile()
    private val root = File(dir, "vault")
    private var now = Instant.parse("2026-01-05T14:00:00Z")
    private fun open() = VaultStore.open(root, ZoneOffset.UTC, { now })

    @After
    fun cleanUp() {
        dir.deleteRecursively()
    }

    private fun snapshot(): Map<String, String> = root.walkTopDown().filter { it.isFile }.associate { it.relativeTo(root).invariantSeparatorsPath to it.readText() }

    @Test
    fun aNewFolderHasTheDefaultTabsWithIdsFromTheirNames() {
        val store = open()
        val groups = store.read { b -> b.groups.map { it.id to it.name } }
        assertEquals(listOf(VaultStore.groupId("Work") to "Work", VaultStore.groupId("Personal") to "Personal"), groups)
        assertTrue(File(root, "Work").isDirectory && File(root, "Personal").isDirectory)
        assertEquals("b908b103-8878-83f3-b6cf-3469c0d45423", VaultStore.groupId("Work").toString())
    }

    @Test
    fun readingNeverWritesAndAChangeWritesOnlyTheTaskItTouched() {
        val store = open()
        val personal = store.read { it.groups[1].id }
        val milk = store.update { it.addTask(NewTask("Buy milk", groupId = personal), Actor.USER, now) }
        store.update { it.addTask(NewTask("Call mom", groupId = personal), Actor.USER, now) }
        val mom = File(root, "Personal/Call mom.md")
        val before = mom.readText()
        val stamp = mom.lastModified()
        now = now.plus(Duration.ofMinutes(5))

        val files = snapshot()
        repeat(3) { open().read { it.items.size } }
        assertEquals("reading wrote nothing", files, snapshot())

        // A title change keeps the file name.
        store.update { it.update(milk.id, title = "Buy oat milk", actor = Actor.USER, now = now) }
        assertTrue(File(root, "Personal/Buy milk.md").readText().contains("# Buy oat milk\n"))
        assertEquals(before, mom.readText())
        assertEquals(stamp, mom.lastModified())

        // Moving to another tab moves the file; deleting it puts it in the trash.
        val work = store.read { it.groups[0].id }
        store.update { it.get(milk.id).ownGroupId = work }
        assertTrue(File(root, "Work/Buy milk.md").exists())
        assertFalse(File(root, "Personal/Buy milk.md").exists())
        store.update { it.delete(milk.id, Actor.USER, now) }
        assertFalse(File(root, "Work/Buy milk.md").exists())
        assertEquals(listOf("20260105-140500 Buy milk.md"), File(root, ".todo-tracker/trash").list()!!.toList())
        assertTrue(File(root, ".todo-tracker/activity.jsonl").readText().lines().any { it.contains("\"kind\":\"deleted\"") })
    }

    @Test
    fun editsMadeInTheFilesAreReadAndABrokenFileIsKeptAsItIs() {
        val store = open()
        File(root, "Work/Typed.md").writeText("# Typed by hand\n\n- [ ] One\n- [x] Two\n")
        val read = store.read { b -> b.items.single().let { it.title to it.children.map { c -> c.title to c.isDone } } }
        assertEquals("Typed by hand" to listOf("One" to false, "Two" to true), read)
        assertEquals("# Typed by hand\n\n- [ ] One\n- [x] Two\n", File(root, "Work/Typed.md").readText())

        File(root, "Work/Broken.md").writeText("---\nenergy: 2\nenergy: 3\n---\n# Broken\n")
        assertEquals(listOf("Work/Broken.md"), store.problems.map { it.path })
        assertEquals("---\nenergy: 2\nenergy: 3\n---\n# Broken\n", File(root, "Work/Broken.md").readText())
    }

    @Test
    fun aFileChangedUnderneathIsReadAgainBeforeTheChange() {
        val store = open()
        val task = store.update { it.addTask(NewTask("Plan", groupId = it.groups[0].id), Actor.USER, now) }
        val file = File(root, "Work/Plan.md")
        file.writeText(file.readText().replace("# Plan\n", "# Plan\n\nNotes typed elsewhere.\n"))
        store.update { it.addNote(task.id, "from the phone", Actor.USER, now) }
        val text = file.readText()
        assertTrue(text.contains("Notes typed elsewhere."))
        assertTrue(text.contains("> from the phone"))
        assertNull(store.problems.firstOrNull())
    }
}
