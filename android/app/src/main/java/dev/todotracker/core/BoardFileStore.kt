package dev.todotracker.core

import java.io.File

/**
 * The board on this device: one JSON file (the shared schema), written atomically, with a `.bak` of the version
 * before. A file from a newer app version is never replaced; an unreadable one is set aside and the backup used.
 */
class BoardFileStore(private val file: File) {
    private val backup = File(file.path + ".bak")

    fun load(): TaskBoard {
        if (!file.exists()) {
            return if (backup.exists()) BoardCodec.decode(backup.readText()) else TaskBoard()
        }
        return try {
            BoardCodec.decode(file.readText())
        } catch (e: BoardException) {
            if (e.message?.contains("newer than this app") == true || !backup.exists()) throw e
            file.renameTo(File(file.path + ".corrupt-${System.currentTimeMillis()}"))
            BoardCodec.decode(backup.readText())
        }
    }

    fun save(board: TaskBoard) {
        file.parentFile?.mkdirs()
        val text = BoardCodec.encode(board)
        val temporary = File(file.path + ".tmp")
        temporary.writeText(text)
        if (file.exists()) file.copyTo(backup, overwrite = true)
        if (!temporary.renameTo(file)) {
            file.delete()
            if (!temporary.renameTo(file)) throw BoardException("Could not save the board.")
        }
    }
}
