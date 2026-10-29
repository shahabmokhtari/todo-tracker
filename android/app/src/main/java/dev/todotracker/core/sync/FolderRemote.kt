package dev.todotracker.core.sync

import dev.todotracker.core.vault.Net
import dev.todotracker.core.vault.VaultFiles
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException

/**
 * A shared folder as the meeting place (TodoTracker.Core.Sync.FolderRemote): `devices/<id>.json` per device and
 * `blobs/<ab>/<hash>` for contents, the layout of `Apps/TodoTrackerSync/<library>` on OneDrive or iCloud Drive. Files
 * are only ever added or replaced whole (a `.<name>.<8 hex>.tmp` then a rename), contents before the snapshot that
 * lists them. Over java.io.File: for tests, folders the phone can reach, and the model for a OneDrive (Graph) remote.
 */
class FolderRemote(val root: File, private val clock: () -> Long = System::currentTimeMillis) : SyncRemote {
    private var lastCollect = 0L

    override val identity: String get() = "folder:" + root.absolutePath

    private val devices: File get() = File(root, "devices")

    override fun forget(device: String) {
        require(device.length in 2..64 && device.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' }) { "Not a device id." }
        File(devices, "$device.json").delete()
    }

    override fun read(self: String, knownVersion: String?): RemoteSnapshot? {
        devices.mkdirs()
        val selfMissing = !File(devices, "$self.json").exists()
        val files = mutableListOf<Pair<String, ByteArray>>()
        for (file in (devices.listFiles { f -> f.isFile && f.name.endsWith(".json") } ?: emptyArray()).sortedWith(compareBy(Net.ordinal) { it.name })) {
            if (Net.equalsCi(Net.stem(file.name), self)) continue
            try {
                files.add(file.name to file.readBytes())
            } catch (e: IOException) {
                // Being replaced right now: seen next time.
            }
        }
        // The other devices' files by content (times and sizes can stay the same across quick writes; ours don't count).
        val version = SyncHash.of(files.joinToString("\n") { "${it.first}|${SyncHash.of(it.second)}" }.toByteArray(Charsets.UTF_8))
        if (version == knownVersion && !selfMissing) return null
        val found = mutableListOf<DeviceSnapshot>()
        var partial = false
        for ((name, content) in files) {
            try {
                val device = SyncJson.readSnapshot(String(content, Charsets.UTF_8))
                if (device.schema == 1 && SyncJson.hasEntries(device) && Net.equalsCi(device.device, Net.stem(name))) found.add(device)
            } catch (e: SyncFormatException) {
                // Half-synced or from a newer version: skipped, and looked at again next time.
                partial = true
            }
        }
        return RemoteSnapshot(found, version, partial, selfMissing)
    }

    override fun readContent(device: DeviceSnapshot, entry: SyncEntry): ByteArray {
        val file = blobPath(entry.hash)
        if (!file.exists()) throw FileNotFoundException("${entry.path} from ${device.name} hasn't arrived yet.")
        if (file.length() > SyncLimits.MAX_FILE_BYTES) throw IOException("${entry.path} from ${device.name} is too big to sync.")
        return file.readBytes()
    }

    override fun publish(snapshot: DeviceSnapshot, read: (SyncEntry) -> ByteArray, readVersion: String?): String? {
        val now = clock()
        for (entry in snapshot.entries) {
            val file = blobPath(entry.hash)
            if (!file.exists()) {
                VaultFiles.writeAtomic(file, read(entry))
            } else if (SyncHash.of(file.readBytes()) != entry.hash) {
                // Damaged (a sync client's half download, a bad disk): the others would never get it, so it's written again.
                VaultFiles.writeAtomic(file, read(entry))
            } else if (file.lastModified() < now - DAY) {
                // Still in use: never "old" to a device cleaning up while it sees an older list of ours.
                file.setLastModified(now)
            }
        }
        VaultFiles.writeAtomic(File(devices, snapshot.device + ".json"), SyncJson.write(SyncJson.snapshot(snapshot)).toByteArray(Charsets.UTF_8))
        if (now - lastCollect > 6 * HOUR) {
            lastCollect = now
            collect()
        }
        // Our own file doesn't change the version the other devices' files have.
        return readVersion
    }

    /** Removes contents no device lists any more (only old ones: a device may be between writing contents and its snapshot). */
    fun collect(minimumAgeMillis: Long = 7 * DAY) {
        val blobs = File(root, "blobs")
        if (!blobs.isDirectory || !devices.isDirectory) return
        val used = HashSet<String>()
        for (file in devices.listFiles { f -> f.isFile && f.name.endsWith(".json") } ?: emptyArray()) {
            try {
                used.addAll(SyncJson.readSnapshot(file.readText()).entries.map { it.hash })
            } catch (e: IOException) {
                return // a device file that can't be read now: keep everything
            }
        }
        val cutoff = clock() - minimumAgeMillis
        blobs.walkTopDown().filter { it.isFile && it.name !in used && it.lastModified() < cutoff }.forEach { it.delete() }
    }

    private fun blobPath(hash: String): File {
        if (hash.length != 64 || !hash.all { it in '0'..'9' || it in 'a'..'f' }) throw IOException("Not a content hash: $hash")
        return File(File(File(root, "blobs"), hash.substring(0, 2)), hash)
    }

    private companion object {
        const val HOUR = 3_600_000L
        const val DAY = 24 * HOUR
    }
}
