package dev.todotracker.core

import java.io.File
import java.io.FileOutputStream

/**
 * The board on this device: one JSON file (the shared schema), written atomically and flushed to disk, with a `.bak`
 * of the last good version. A file from a newer app version is never replaced. An unreadable one is set aside
 * (`board.json.corrupt-<time>`) and the backup used; with no usable backup, a fresh board starts and [problem] says
 * where the old file was kept.
 */
class BoardFileStore(private val file: File) {
    private val backup = File(file.path + ".bak")

    /** Why the last load didn't open the saved board as it was (null: it did). */
    var problem: String? = null
        private set

    fun load(): TaskBoard {
        problem = null
        if (!file.exists()) {
            return if (backup.exists()) readOrNull(backup) ?: TaskBoard() else TaskBoard()
        }
        try {
            return BoardCodec.decode(file.readText())
        } catch (e: BoardException) {
            if (e.isNewerSchema) throw e
        } catch (e: java.io.IOException) {
            // Unreadable: handled below like a damaged file.
        }
        val aside = File(file.path + ".corrupt-${System.currentTimeMillis()}")
        file.renameTo(aside)
        val restored = if (backup.exists()) readOrNull(backup) else null
        problem = if (restored != null) {
            "Your saved board was damaged; the last good copy was opened (the damaged one is kept as ${aside.name})."
        } else {
            "Your saved board couldn't be read and had no good copy; it's kept as ${aside.name} and a fresh board started."
        }
        return restored ?: TaskBoard()
    }

    fun save(board: TaskBoard) = saveText(BoardCodec.encode(board))

    /** Writes already-encoded text (so the encoding can happen where the board is, and the writing elsewhere). */
    fun saveText(text: String) {
        file.parentFile?.mkdirs()
        // The version on disk now becomes the backup, but only if it's a good one.
        if (file.exists()) {
            val current = file.readBytes()
            if (runCatching { BoardCodec.decode(String(current, Charsets.UTF_8)) }.isSuccess) writeSynced(backup, current)
        }
        val temporary = File(file.path + ".tmp")
        writeSynced(temporary, text.toByteArray(Charsets.UTF_8))
        if (!temporary.renameTo(file)) {
            file.delete()
            if (!temporary.renameTo(file)) throw BoardException("Could not save the board.")
        }
    }

    private fun readOrNull(source: File): TaskBoard? = runCatching { BoardCodec.decode(source.readText()) }.getOrNull()

    /** Written and flushed to the disk before anything relies on it (a power cut can't leave it empty). */
    private fun writeSynced(target: File, bytes: ByteArray) {
        val temporary = if (target.path.endsWith(".tmp")) target else File(target.path + ".tmp")
        FileOutputStream(temporary).use { out ->
            out.write(bytes)
            out.flush()
            out.fd.sync()
        }
        if (temporary != target && !temporary.renameTo(target)) {
            target.delete()
            if (!temporary.renameTo(target)) throw BoardException("Could not save ${target.name}.")
        }
    }
}
