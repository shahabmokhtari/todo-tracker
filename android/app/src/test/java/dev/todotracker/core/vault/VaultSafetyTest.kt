package dev.todotracker.core.vault

import dev.todotracker.core.Actor
import dev.todotracker.core.BoardCodec
import dev.todotracker.core.NewTask
import dev.todotracker.core.TaskBoard
import dev.todotracker.core.sync.SyncHash
import dev.todotracker.core.sync.SyncOp
import dev.todotracker.core.sync.SyncOpKind
import java.io.File
import java.io.IOException
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files
import java.time.Duration
import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** Nothing is ever lost: the trash keeps 30 days from when a task was trashed, and changes are all or nothing. */
class VaultSafetyTest {
    private val dir: File = Files.createTempDirectory("tt-safety").toFile()
    private val root = File(dir, "vault")
    private var now = Instant.parse("2026-03-05T14:00:00Z")
    private fun open() = VaultStore.open(root, ZoneOffset.UTC, { now })
    private val trash get() = File(root, ".todo-tracker/trash")
    private val days = 24L * 3600 * 1000

    @After
    fun cleanUp() {
        root.walkTopDown().forEach { it.setWritable(true) }
        dir.deleteRecursively()
    }

    @Test
    fun aTaskLastEditedLongAgoStaysInTheTrashFor30DaysAfterItWasDeleted() {
        val store = open()
        val task = store.update { it.addTask(NewTask("Old plan", groupId = it.groups[0].id), Actor.USER, now) }
        // Edited two months ago: the move to the trash keeps that time, which isn't when it was trashed.
        File(root, "Work/Old plan.md").setLastModified(now.toEpochMilli() - 60 * days)
        store.update { it.delete(task.id, Actor.USER, now) }
        val trashed = trash.listFiles()!!.single()
        trashed.setLastModified(now.toEpochMilli() - 60 * days)
        open()
        assertTrue(trashed.exists())
    }

    @Test
    fun trashIsEmptiedByTheTimeInItsNameAndTrashWithoutOneIsKept() {
        open()
        trash.mkdirs()
        val stamp = DateTimeFormatter.ofPattern("uuuuMMdd-HHmmss").withZone(ZoneOffset.UTC)
        val old = File(trash, stamp.format(now.minus(Duration.ofDays(31))) + " Gone.md")
        val recent = File(trash, stamp.format(now.minus(Duration.ofDays(29))) + " Kept.md")
        val undated = File(trash, "Someone's note.md")
        for (f in listOf(old, recent, undated)) {
            f.writeText("# x\n")
            f.setLastModified(now.toEpochMilli() - 90 * days)
        }
        open()
        assertFalse(old.exists())
        assertTrue(recent.exists())
        assertTrue(undated.exists())
    }

    @Test
    fun importingAnOldBoardIsAllOrNothing() {
        val store = open()
        val legacy = TaskBoard()
        legacy.addTask(NewTask("Personal one", groupId = legacy.groups[1].id), Actor.USER, now)
        legacy.addTask(NewTask("Work one", groupId = legacy.groups[0].id), Actor.USER, now)
        val before = BoardCodec.encode(legacy)
        File(root, "Work").setWritable(false)
        try {
            store.importBeside(legacy)
            fail("the Work folder can't be written")
        } catch (e: IOException) {
            // Refused as a whole.
        }
        File(root, "Work").setWritable(true)
        assertEquals(emptyList<String>(), File(root, "Personal").list()!!.toList())
        assertEquals(0, store.read { it.items.size })
        assertEquals(before, BoardCodec.encode(legacy))
        // Once it can be written, it all comes in.
        assertEquals(2, store.importBeside(legacy))
    }

    @Test
    fun aSyncThatFailsPartWayIsUndoneWhateverWentWrong() {
        val store = open()
        val config = File(root, ".todo-tracker/config.json")
        val ops = listOf(
            SyncOp(SyncOpKind.WRITE, "Work/From the phone.md", null, "# From the phone\n".toByteArray()),
            // Not an I/O error (no content): the write before it is undone all the same.
            SyncOp(SyncOpKind.SET_CONFIG, ".todo-tracker/config.json", SyncHash.of(config.readBytes()), null),
        )
        try {
            store.applySync(ops)
            fail("the plan can't be applied")
        } catch (e: NullPointerException) {
            // Rethrown after undoing.
        }
        assertFalse(File(root, "Work/From the phone.md").exists())
    }

    @Test
    fun aNewFileNeverReplacesOneThatAppearedMeanwhile() {
        val file = File(root, "Work/New.md")
        file.parentFile!!.mkdirs()
        file.writeText("someone else's")
        try {
            VaultFiles.writeAtomic(file, "mine", replace = false)
            fail("it was there")
        } catch (e: FileAlreadyExistsException) {
            // Kept.
        }
        assertEquals("someone else's", file.readText())
        assertEquals(listOf("New.md"), file.parentFile!!.list()!!.toList())
    }

    @Test
    fun aTaskFileThatCantBeReadStopsSyncInsteadOfLookingDeleted() {
        val store = open()
        store.update { it.addTask(NewTask("Fine", groupId = it.groups[0].id), Actor.USER, now) }
        File(root, "Work/Broken.md").writeText("---\nenergy: 2\nenergy: 3\n---\n# Broken\n")
        try {
            store.readSync()
            fail("a file that can't be read is never left out of what syncs")
        } catch (e: IOException) {
            assertTrue(e.message!!.contains("Work/Broken.md"))
        }
    }

    @Test
    fun anEarlierMigratedBoardIsNeverReplaced() {
        val board = File(dir, "board.json")
        File(dir, "board.json.migrated").writeText("first")
        board.writeText("second")
        VaultFiles.setAside(board)
        assertFalse(board.exists())
        assertEquals("first", File(dir, "board.json.migrated").readText())
        assertEquals("second", File(dir, "board.json.migrated 2").readText())
    }

    @Test
    fun aNameLongerThan255BytesNeverSyncs() {
        assertTrue(VaultFiles.isSyncablePath("Work/" + "é".repeat(126) + ".md"))
        assertFalse(VaultFiles.isSyncablePath("Work/" + "é".repeat(127) + ".md"))
    }
}
