package dev.todotracker.core.sync

import java.io.IOException

/** What a place holds: every other device's last snapshot, and a version token for all of it. */
data class RemoteSnapshot(
    val devices: List<DeviceSnapshot>,
    val version: String,
    /** Some device's snapshot couldn't be read this time (try again; don't count this version as merged). */
    val partial: Boolean = false,
    /** This device's own snapshot isn't there (deleted, or a new place): publish again. */
    val selfMissing: Boolean = false,
)

/**
 * Where devices meet (TodoTracker.Core.Sync.ISyncRemote): a shared folder, a gist… Each device only ever writes its
 * own snapshot (and contents), so two devices never write the same thing. The calls block: run them off the main
 * thread (a worker). A new kind of place implements this and is offered through a [SyncBackend].
 */
interface SyncRemote {
    /** Which place this is: when it changes, everything is published again. */
    val identity: String

    /** The other devices' snapshots, or null when nothing changed since [knownVersion]. */
    fun read(self: String, knownVersion: String?): RemoteSnapshot?

    /** The content of one of a device's entries; an [IOException] when it isn't there (yet). */
    fun readContent(device: DeviceSnapshot, entry: SyncEntry): ByteArray

    /** Publishes this device's snapshot (contents first, the snapshot last); the new version when nothing else changed since [readVersion]. */
    fun publish(snapshot: DeviceSnapshot, read: (SyncEntry) -> ByteArray, readVersion: String?): String?

    /** Removes a device's snapshot, so it's no longer merged. */
    fun forget(device: String)
}
