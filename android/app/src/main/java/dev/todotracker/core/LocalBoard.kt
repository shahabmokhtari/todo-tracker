package dev.todotracker.core

import dev.todotracker.core.vault.VaultFiles
import dev.todotracker.core.vault.VaultStore
import java.io.File
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

    fun open(filesDir: File, zone: ZoneId = ZoneId.systemDefault(), clock: () -> Instant = Instant::now): Opened {
        val root = vaultRoot(filesDir)
        val legacy = legacyFile(filesDir)
        if (!legacy.exists() && !File(legacy.path + ".bak").exists()) return Opened(VaultStore.open(root, zone, clock), null, null)
        val loader = BoardFileStore(legacy)
        val board = try {
            loader.load()
        } catch (e: BoardException) {
            // Only a board from a newer version of the app: it stays where it is, untouched, and nothing is changed.
            return Opened(null, "${e.message} Changes are off so your board isn't overwritten.", null)
        }
        board.zone = zone
        val store: VaultStore
        val count: Int
        if (isNew(root)) {
            store = VaultStore.open(root, zone, clock, board)
            count = board.items.size
        } else {
            // Moved before, but the file wasn't set aside (the app stopped in between): what isn't there yet comes in.
            store = VaultStore.open(root, zone, clock)
            count = store.importBeside(board)
        }
        VaultFiles.setAside(legacy)
        return Opened(store, loader.problem, count)
    }

    /** Nothing in the folder yet: no settings and no task files. */
    private fun isNew(root: File): Boolean =
        !File(File(root, VaultFiles.META_FOLDER), "config.json").exists() &&
            (root.listFiles() ?: emptyArray()).filter { it.isDirectory && !it.name.startsWith('.') && !it.name.startsWith('_') }.none { dir -> dir.walkTopDown().any { it.isFile && it.name.endsWith(".md", ignoreCase = true) } }
}
