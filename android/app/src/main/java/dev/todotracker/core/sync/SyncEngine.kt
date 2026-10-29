package dev.todotracker.core.sync

import dev.todotracker.core.BoardCodec
import dev.todotracker.core.vault.Net
import dev.todotracker.core.vault.VaultFiles
import dev.todotracker.core.vault.VaultStore
import java.io.File
import java.io.IOException
import java.time.Duration
import java.time.Instant

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
 * own snapshot (and contents). The calls block: run them off the main thread (a worker).
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

/** What one sync did. */
data class SyncResult(
    val changes: Int,
    val peers: Int,
    val conflicts: List<SyncConflictRecord>,
    val published: Boolean,
    val skipped: Boolean,
    val problem: String? = null,
    val devices: List<SyncDevice>? = null,
    /** Every other device there was merged (none skipped, half read or waiting for contents). */
    val complete: Boolean = true,
)

/** What joining sync did: the pull, how many of this phone's own tasks came in beside what came, and the publish. */
data class JoinResult(val pulled: SyncResult, val imported: Int, val published: SyncResult?) {
    /** False: the first pull didn't get everything, so nothing was imported or published yet (join again later). */
    val joined: Boolean get() = published != null
}

/** How a place is used: read (others' changes come in), write (this device's tasks go out), or both. */
enum class SyncMode(val reads: Boolean, val writes: Boolean) { READ(true, false), WRITE(false, true), BOTH(true, true) }

/**
 * Syncs the phone's tasks folder through a place, exactly as TodoTracker.Core.Sync.SyncEngine: merges each other
 * device's latest snapshot three-way against what the two last agreed on, then publishes this vault's snapshot.
 * Nothing is merged or published when neither side changed. One sync at a time (calls are serialized).
 */
class SyncEngine(
    private val store: VaultStore,
    private val remote: SyncRemote,
    val state: SyncState,
    private val clock: () -> Instant = Instant::now,
) {
    private val lock = Any()

    @Volatile
    var mode: SyncMode = SyncMode.BOTH

    /** One sync; the vault changing meanwhile starts it over (up to 3 times). Errors are kept in [SyncState.status] too. */
    fun sync(): SyncResult = synchronized(lock) {
        try {
            syncWithRetries()
        } catch (e: Exception) {
            state.status = state.status.copy(lastError = e.message)
            throw e
        }
    }

    private fun syncWithRetries(): SyncResult {
        var attempt = 1
        while (true) {
            try {
                val result = syncOnce()
                val open = (state.status.conflicts + result.conflicts).distinctBy { Triple(it.key, it.mine, it.theirs) }.takeLast(50)
                state.status = SyncStatus(clock(), result.problem, result.peers, open, result.devices ?: state.status.devices)
                return result
            } catch (e: SyncStaleException) {
                if (attempt++ >= 3) throw e
                // The vault changed while we worked: start over (and merge everything again).
                state.remoteVersion = null
            }
        }
    }

    /**
     * Joins sync for the first time (docs/design/phone-sync.md, "First join"): pulls what the other devices have
     * without publishing, then brings this phone's own [legacyBoard] file (its board.json) in beside what came (tasks
     * already there are skipped), sets that file aside as `.migrated`, and publishes. When the pull didn't get
     * everything (a device half read, contents not arrived), nothing is imported or published: join again later.
     */
    fun join(legacyBoard: File?): JoinResult = synchronized(lock) {
        val previous = mode
        mode = SyncMode.READ
        val pulled = try {
            syncWithRetries()
        } finally {
            mode = previous
        }
        if (!pulled.complete) return@synchronized JoinResult(pulled, 0, null)
        var imported = 0
        if (legacyBoard != null && legacyBoard.exists()) {
            // A board that can't be read stays where it is (BoardException): nothing is lost or set aside.
            imported = store.importBeside(BoardCodec.decode(legacyBoard.readText()))
            VaultFiles.setAside(legacyBoard)
        }
        JoinResult(pulled, imported, syncWithRetries())
    }

    /** Stops merging a device (a phone or computer that's gone). */
    fun forget(device: String) = synchronized(lock) {
        remote.forget(device)
        state.forgetPeer(device)
        state.remoteVersion = null
        state.status = state.status.copy(devices = state.status.devices.filter { it.device != device })
    }

    /** Forgets a conflict once it's been settled. */
    fun dismiss(key: String) = synchronized(lock) {
        state.status = state.status.copy(conflicts = state.status.conflicts.filter { it.key != key })
    }

    /** This device stops writing to the place: its snapshot there goes, and from now on the place is used as [newMode]. */
    fun retire(newMode: SyncMode) = synchronized(lock) {
        mode = newMode
        remote.forget(state.deviceId)
        state.published = null
        state.seenVersion = null
    }

    private fun syncOnce(): SyncResult {
        val now = clock()
        val mode = this.mode
        val reading = mode.reads

        // A new place (another folder, another gist): everything is published there again.
        if (state.remoteIdentity != remote.identity) {
            state.remoteIdentity = remote.identity
            state.remoteVersion = null
            state.seenVersion = null
            state.published = null
        }

        val known = if (reading) state.remoteVersion else state.seenVersion
        var local = store.readSync()
        val snapshot = remote.read(state.deviceId, known)
        if (!reading && snapshot != null && !snapshot.partial) state.seenVersion = snapshot.version
        val last = state.publishedAt
        val heartbeatDue = last == null || Duration.between(last, now) > SyncLimits.HEARTBEAT
        if (snapshot == null && fingerprint(local) == state.published && !heartbeatDue) return SyncResult(0, state.peers().size, emptyList(), published = false, skipped = true)

        // Devices not heard from in months are no longer merged (their old copies would bring back deleted tasks).
        val seen = (snapshot?.devices ?: emptyList()).filter { it.device != state.deviceId && Duration.between(it.at, now) < SyncLimits.MEMORY }.sortedWith(compareBy(Net.ordinal) { it.device })
        val peers = if (reading) seen else emptyList()

        // A delete counts against the version it deleted, and only if it happened after that version appeared there.
        val deleted = HashMap<Pair<String, String>, Instant>()
        for (t in peers.flatMap { it.deleted }) {
            val k = t.key to t.hash
            val at = deleted[k]
            deleted[k] = if (at != null && at.isAfter(t.at)) at else t.at
        }
        val publishedSince = state.publishedEntries.filter { it.since != null }.associate { (it.key to it.hash) to it.since!! }

        // A device with no sync history may hold copies of files deleted elsewhere since: its files count as old.
        val unpublished = if (state.publishedEntries.isEmpty() && state.peers().isEmpty()) NetTime.MIN else now
        val options = SyncPlanOptions(
            deleted = { e -> deleted[e.key to e.hash]?.let { at -> at.isAfter(e.since ?: publishedSince[e.key to e.hash] ?: unpublished) } == true },
            isValidTask = store::isValidTaskText,
        )

        var changes = 0
        val conflicts = mutableListOf<SyncConflictRecord>()
        val problems = mutableListOf<String>()
        var complete = snapshot?.partial != true
        for (peer in peers) {
            try {
                val entries = valid(peer) ?: continue
                val missing = fetch(peer, entries, local)
                if (missing != null) {
                    complete = false
                    missing.takeIf { it.isNotEmpty() }?.let { problems.add("${peer.name}: $it") }
                    continue
                }
                val base = state.loadBase(peer.device)
                val current = local
                val plan = SyncPlanner.plan(
                    SyncSide(state.deviceId, state.deviceName, current.entries, current.read),
                    SyncSide(peer.device, peer.name, entries) { e -> state.readBlob(e.hash) ?: throw SyncStaleException("${e.path} is missing.") },
                    base,
                    state::readBlob,
                    options,
                )
                // Both versions of a clash stay available (to keep one of them).
                for (conflict in plan.conflicts) state.writeBlob(conflict.mine, current.read(current.entries[conflict.key]!!))
                if (plan.ops.isNotEmpty()) {
                    store.applySync(plan.ops)
                    changes += plan.ops.size
                    local = store.readSync()
                }
                plan.conflicts.mapTo(conflicts) { SyncConflictRecord(it.key, it.path, it.peer, it.peerName, it.mine, it.theirs, it.base, now, it.result) }

                // Remember what both now have; text needs its content kept for the next three-way merge.
                val next = SyncPlanner.nextBase(local.entries, entries, base)
                for (entry in next.values) if (needsBase(entry.key) && !state.hasBlob(entry.hash)) state.writeBlob(entry.hash, local.read(entry))
                state.saveBase(peer.device, next)
            } catch (e: IOException) {
                problems.add("${peer.name}: ${e.message}")
                complete = false
            } catch (e: IllegalArgumentException) {
                problems.add("${peer.name}: ${e.message}")
                complete = false
            } catch (e: IllegalStateException) {
                problems.add("${peer.name}: ${e.message}")
                complete = false
            }
        }

        if (snapshot != null && complete && reading) state.remoteVersion = snapshot.version
        if (snapshot?.partial == true) problems.add("Another device's sync file couldn't be read (it may be from a newer version of Todo Tracker); it's tried again.")

        var published = false
        val fingerprint = fingerprint(local)
        if (mode.writes && (fingerprint != state.published || heartbeatDue || snapshot?.selfMissing == true)) {
            // What disappeared since the last publish was deleted here: say so (for months) so it stays gone everywhere.
            val fileKinds = setOf(SyncKind.TASK, SyncKind.RICH, SyncKind.FILE)
            val tombstones = (
                state.tombstones.filter { Duration.between(it.at, now) < SyncLimits.MEMORY && !local.entries.containsKey(it.key) } +
                    state.publishedEntries.filter { !local.entries.containsKey(it.key) && SyncKeys.kindOf(it.key) in fileKinds }.map { SyncTombstone(it.key, it.hash, now) }
                ).distinctBy { it.key to it.hash }
            val entriesNow = local.entries.values.map { it.copy(since = publishedSince[it.key to it.hash] ?: now) }.sortedWith(compareBy(Net.ordinal) { it.key })
            val mine = DeviceSnapshot(state.deviceId, state.deviceName, now, entriesNow, deleted = tombstones)
            val version = remote.publish(mine, local.read, if (reading) state.remoteVersion else state.seenVersion)
            state.tombstones = tombstones
            state.publishedEntries = entriesNow
            state.published = fingerprint
            state.publishedAt = now
            if (complete && reading) state.remoteVersion = version else if (!reading) state.seenVersion = version
            published = true
        }

        collectBlobs(conflicts)
        val devices = if (snapshot == null) null else seen.map { SyncDevice(it.device, it.name, it.at) }
        return SyncResult(changes, peers.size, conflicts, published, skipped = false, problem = problems.takeIf { it.isNotEmpty() }?.joinToString(" "), devices = devices, complete = complete)
    }

    /**
     * Brings the peer's contents not held yet into the local cache. Null when all are here; else why not ("" when one
     * just hasn't arrived yet): that device is merged next time, never in part.
     */
    private fun fetch(peer: DeviceSnapshot, entries: Map<String, SyncEntry>, local: SyncSnapshot): String? {
        for (entry in entries.values) {
            if (local.entries[entry.key]?.hash == entry.hash || state.hasBlob(entry.hash)) continue
            val content = try {
                remote.readContent(peer, entry)
            } catch (e: IOException) {
                return ""
            }
            // Damaged, or still being written: said (so a blob that keeps failing is seen), and tried again next time.
            if (SyncHash.of(content) != entry.hash) return "${entry.path} doesn't match its contents' hash (damaged, or still being written); it's tried again."
            state.writeBlob(entry.hash, content)
        }
        return null
    }

    private fun needsBase(key: String) = SyncKeys.kindOf(key).let { it == SyncKind.TASK || it == SyncKind.RICH || it == SyncKind.CONFIG || it == SyncKind.ORDER }

    private fun collectBlobs(fresh: List<SyncConflictRecord>) {
        val keep = state.peers().flatMap { p -> state.loadBase(p).values }.filter { needsBase(it.key) }.map { it.hash } +
            (state.status.conflicts + fresh).flatMap { listOfNotNull(it.mine, it.theirs, it.base) }
        state.collectBlobs(keep)
    }

    companion object {
        /** A hash of what the vault holds (sorted `key|path|hash` lines): unchanged means nothing to publish. */
        fun fingerprint(snapshot: SyncSnapshot): String =
            SyncHash.of(snapshot.entries.values.sortedWith(compareBy(Net.ordinal) { it.key }).joinToString("\n") { "${it.key}|${it.path}|${it.hash}" }.toByteArray(Charsets.UTF_8))

        private fun isDeviceId(id: String) = id.length in 2..64 && id.all { it in 'a'..'z' || it in 'A'..'Z' || it in '0'..'9' }

        /** The peer's entries this device accepts: well-formed ids, hashes and paths only (it's data from elsewhere). */
        fun valid(peer: DeviceSnapshot): Map<String, SyncEntry>? {
            if (!isDeviceId(peer.device) || !SyncJson.hasEntries(peer)) return null
            val entries = LinkedHashMap<String, SyncEntry>()
            for (e in peer.entries) {
                if (e.hash.length != 64 || !e.hash.all { it in '0'..'9' || it in 'a'..'f' }) continue
                val ok = when (SyncKeys.kindOf(e.key)) {
                    SyncKind.CONFIG -> e.path == SyncKeys.CONFIG_PATH
                    SyncKind.ORDER -> e.path == SyncKeys.ORDER_PATH
                    SyncKind.ACTIVITY -> e.path == SyncKeys.ACTIVITY_PATH
                    SyncKind.TASK -> Net.endsWithCi(e.path, ".md") && VaultFiles.isSyncablePath(e.path) && Net.parseGuidN(e.key.substring(2)) != null
                    SyncKind.RICH -> Net.endsWithCi(e.path, ".html") && VaultFiles.isSyncablePath(e.path) && Net.parseGuidN(e.key.substring(2)) != null
                    SyncKind.FILE -> e.key == SyncKeys.file(e.path) && VaultFiles.isSyncablePath(e.path)
                }
                // Older devices don't say since when they have a version: oldest possible (a later delete wins).
                if (ok) entries[e.key] = if (e.since == null) e.copy(since = NetTime.MIN) else e
            }
            return entries
        }
    }
}
