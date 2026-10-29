package dev.todotracker.core

import dev.todotracker.core.vault.VaultFiles
import dev.todotracker.core.vault.VaultStore
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.time.Instant
import java.time.ZoneId

/**
 * The tasks kept on this phone: a tasks folder (`<filesDir>/vault`, the same format as on a computer) that is the
 * truth for everything the app does. An older version of the app kept them in one file, `board.json`: the first time,
 * its tasks move into the folder and the file is set aside as `board.json.migrated` (never deleted).
 */
object LocalBoard {
    /** The phone's tasks folder, or why it isn't used ([store] null: changes are off, nothing is overwritten). */
    class Opened(val store: VaultStore?, val problem: String?, val migrated: Int?)

    fun vaultRoot(filesDir: File) = File(filesDir, "vault")

    fun legacyFile(filesDir: File) = File(filesDir, "board.json")

    /** Where a move from board.json builds the folder before it's renamed into place (so a half-built one is never taken for it). */
    private fun movingRoot(filesDir: File) = File(filesDir, "vault.moving")

    /** Whether opening will move an old board.json in first (it takes a moment: say so). */
    fun needsMove(filesDir: File): Boolean = legacyFile(filesDir).exists() || File(legacyFile(filesDir).path + ".bak").exists()

    /** Opens the phone's tasks folder (moving board.json in the first time). Never throws: a failure turns changes off, with why. */
    fun open(filesDir: File, zone: ZoneId = ZoneId.systemDefault(), clock: () -> Instant = Instant::now): Opened =
        try {
            openOrThrow(filesDir, zone, clock)
        } catch (e: Exception) {
            // Nothing is changed or lost: board.json (if any) stays where it was, and the next start tries again.
            Opened(null, "Couldn't open your tasks (${e.message ?: e.javaClass.simpleName}). Changes are off so nothing is overwritten; restart the app to try again.", null)
        }

    private fun openOrThrow(filesDir: File, zone: ZoneId, clock: () -> Instant): Opened {
        val root = vaultRoot(filesDir)
        val legacy = legacyFile(filesDir)
        if (!needsMove(filesDir)) return Opened(VaultStore.open(root, zone, clock), null, null)
        val loader = BoardFileStore(legacy)
        val board = try {
            loader.load()
        } catch (e: BoardException) {
            // Only a board from a newer version of the app: it stays where it is, untouched, and nothing is changed.
            return Opened(null, "${e.message} Changes are off so your board isn't overwritten.", null)
        }
        board.zone = zone
        val count: Int
        if (isNew(root)) {
            // Built aside, then renamed into place in one step: a move that stops half way leaves no half-built folder.
            val moving = movingRoot(filesDir)
            if (moving.exists() && !moving.deleteRecursively()) throw java.io.IOException("Couldn't clear ${moving.name}.")
            VaultStore.open(moving, zone, clock, board)
            if (root.exists()) {
                // Only empty folders from an earlier try may be here; anything else stays, and the move waits.
                if (root.walkTopDown().any { it.isFile }) throw java.io.IOException("The tasks folder has files but no tasks; nothing was moved.")
                root.deleteRecursively()
            }
            Files.move(moving.toPath(), root.toPath(), StandardCopyOption.ATOMIC_MOVE)
            count = board.items.size
        } else {
            // Moved before, but the file wasn't set aside (the app stopped in between): what isn't there yet comes in.
            count = VaultStore.open(root, zone, clock).importBeside(board)
        }
        VaultFiles.setAside(legacy)
        return Opened(VaultStore.open(root, zone, clock), loader.problem, count)
    }

    /** Nothing in the folder yet: no settings and no task files (an empty folder left by an earlier try counts as new). */
    private fun isNew(root: File): Boolean {
        if (root.exists() && !root.isDirectory) throw java.io.IOException("\"${root.name}\" is a file, not a folder.")
        return !File(File(root, VaultFiles.META_FOLDER), "config.json").exists() &&
            (root.listFiles() ?: emptyArray()).filter { it.isDirectory && !it.name.startsWith('.') && !it.name.startsWith('_') }.none { dir -> dir.walkTopDown().any { it.isFile && it.name.endsWith(".md", ignoreCase = true) } }
    }
}
