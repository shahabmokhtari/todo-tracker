package dev.todotracker.core.vault

import java.io.File
import java.io.FileOutputStream
import java.text.Normalizer
import java.util.UUID

/** File names and safe writes in the tasks folder (TodoTracker.Core.Vault.VaultFiles). */
object VaultFiles {
    const val META_FOLDER = ".todo-tracker"
    const val ATTACHMENTS_FOLDER = "_attachments"

    private val reserved = setOf(
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    )

    private val unsafe = Net.rx("[<>:\"/\\\\|?*#^\\[\\]\\x00-\\x1F]")
    private val spaces = Net.rx("[${Net.WS}]{2,}")

    /** A file or folder name that works on Windows, macOS, Linux and in Obsidian links (at most [maxLength] UTF-16 units). */
    fun safeName(title: String, maxLength: Int = 80): String {
        var cleaned = unsafe.matcher(Normalizer.normalize(title, Normalizer.Form.NFC)).replaceAll(" ")
        cleaned = Net.trim(spaces.matcher(cleaned).replaceAll(" ")).trimEnd('.', ' ').trimStart('.', ' ')
        if (cleaned.length > maxLength) {
            // Cut by UTF-16 units, but never between the two halves of a character beyond U+FFFF.
            cleaned = cleaned.substring(0, if (Character.isHighSurrogate(cleaned[maxLength - 1])) maxLength - 1 else maxLength).trimEnd('.', ' ')
        }
        if (cleaned.isEmpty()) cleaned = "Untitled"
        return if (Net.ci(cleaned) in reserved) cleaned + "_" else cleaned
    }

    /** A group folder name: like [safeName], without a leading _ or . (those folders are skipped). */
    fun safeFolderName(name: String): String {
        val cleaned = safeName(name, 60).trimStart('_', '.', ' ')
        return cleaned.ifEmpty { "Group" }
    }

    /** [stem], or "stem 2", "stem 3"… so that it isn't [taken]. */
    fun uniqueStem(stem: String, taken: (String) -> Boolean): String {
        if (!taken(stem)) return stem
        var n = 2
        while (true) {
            val candidate = "$stem $n"
            if (!taken(candidate)) return candidate
            n++
        }
    }

    /**
     * Written atomically (a temp file `.<name>.<8 hex>.tmp` next to it, flushed, then renamed over it), so readers and
     * sync never see half a file.
     */
    fun writeAtomic(target: File, content: ByteArray) {
        target.parentFile?.mkdirs()
        val temp = File(target.parentFile, "." + target.name + "." + UUID.randomUUID().toString().replace("-", "").substring(0, 8) + ".tmp")
        try {
            FileOutputStream(temp).use { out ->
                out.write(content)
                out.flush()
                out.fd.sync()
            }
            if (!temp.renameTo(target)) {
                java.nio.file.Files.move(temp.toPath(), target.toPath(), java.nio.file.StandardCopyOption.REPLACE_EXISTING)
            }
        } finally {
            if (temp.exists()) temp.delete()
        }
    }

    fun writeAtomic(target: File, text: String) = writeAtomic(target, text.toByteArray(Charsets.UTF_8))

    /** The text of a file as .NET's File.ReadAllText reads it: UTF-8, a byte order mark left out. */
    fun readText(file: File): String = decode(file.readBytes())

    fun decode(bytes: ByteArray): String {
        val bom = bytes.size >= 3 && bytes[0] == 0xEF.toByte() && bytes[1] == 0xBB.toByte() && bytes[2] == 0xBF.toByte()
        return if (bom) String(bytes, 3, bytes.size - 3, Charsets.UTF_8) else String(bytes, Charsets.UTF_8)
    }

    private val reservedName = Net.rx("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\\..*)?$", ignoreCase = true)

    /** A path from another device is only ever a file inside the vault's own folders, with a name every system keeps as is. */
    fun isSyncablePath(rel: String): Boolean =
        rel.isNotEmpty() && rel.length < 400 && rel.contains('/') && !rel.contains('\\') && !rel.contains(':') && !Net.endsWithCi(rel, ".tmp") &&
            rel.split('/').all { s ->
                s.isNotEmpty() && s != ".." && s[0] != '.' && Net.trim(s).length == s.length && !s.endsWith('.') &&
                    s.none { it.code < 32 || it in "<>:\"|?*/" } && !reservedName.matcher(s).find()
            }
}
