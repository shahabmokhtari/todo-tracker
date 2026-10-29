import CryptoKit
import Foundation

/// What a remote holds: every other device's last snapshot, and a version token for all of it.
public struct RemoteSnapshot: Sendable {
    public var devices: [DeviceSnapshot]
    public var version: String
    /// Some device's snapshot couldn't be read this time (try again; don't treat this version as merged).
    public var partial = false
    /// This device's own snapshot isn't there (deleted, or a new place): publish again.
    public var selfMissing = false

    public init(devices: [DeviceSnapshot], version: String, partial: Bool = false, selfMissing: Bool = false) {
        self.devices = devices
        self.version = version
        self.partial = partial
        self.selfMissing = selfMissing
    }
}

/// A content that isn't in the place (yet): the device may still be uploading, or iCloud hasn't downloaded it.
public struct SyncNotArrived: Error, LocalizedError {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// Where devices meet: a shared folder (iCloud Drive, OneDrive), a gist… Each device only ever writes its own
/// snapshot (and contents), so two devices never write the same thing. Port of C#'s `ISyncRemote`.
public protocol SyncRemote: AnyObject {
    /// Which place this is: when it changes, everything is published again.
    var identity: String { get }

    /// The other devices' snapshots, or nil when nothing changed since `knownVersion`.
    func read(self device: String, knownVersion: String?) async throws -> RemoteSnapshot?

    /// The content of one of a device's entries (`SyncNotArrived` when it isn't there yet).
    func readContent(_ device: DeviceSnapshot, _ entry: SyncEntry) async throws -> Data

    /// Publishes this device's snapshot (contents first, the snapshot last). Returns the remote's new version when it
    /// is certain nothing else changed since `readVersion`, else nil.
    func publish(_ snapshot: DeviceSnapshot, read: (SyncEntry) throws -> Data, readVersion: String?) async throws -> String?

    /// Removes a device's snapshot (a phone that's gone), so it's no longer merged.
    func forget(_ device: String) async throws
}

/// What one sync did.
public struct SyncResult: Sendable {
    public var changes: Int
    public var peers: Int
    public var conflicts: [SyncConflictRecord]
    public var published: Bool
    public var skipped: Bool
    /// Devices that couldn't be merged this time, and why (the others were).
    public var problem: String?
    /// The other devices seen (nil when the remote didn't change).
    public var devices: [SyncDevice]?
    /// Every other device was merged whole (nothing was missing or still arriving).
    public var complete = true
}

/// Syncs the phone's tasks folder through a remote: merges each other device's latest snapshot into the vault
/// (three-way, against what the two devices last agreed on), then publishes this vault's snapshot. Nothing is merged or
/// published when neither side changed. One sync at a time (an actor). Port of C#'s `SyncEngine`.
public actor SyncEngine {
    public let store: VaultStore
    public let remote: SyncRemote
    public let state: SyncState
    private let clock: () -> Date

    public init(store: VaultStore, remote: SyncRemote, state: SyncState, clock: @escaping () -> Date = Date.init) {
        self.store = store
        self.remote = remote
        self.state = state
        self.clock = clock
    }

    /// One sync: merge what came, publish what changed. Starts over (up to 3 times) when the tasks folder changed while
    /// syncing; cancelled part way it leaves the folder whole and the next sync starts over.
    @discardableResult
    public func sync() async throws -> SyncResult { try await run(publish: true) }

    /// Joining an existing sync for the first time: everything the others have comes in first, then the phone's own
    /// `board.json` (from before the tasks folder) is brought in beside it — a task already there (same id) is never
    /// copied — and only then is this device published. A phone that used remote mode against a computer holds copies
    /// of that computer's tasks with the same ids; importing them first would turn each into a "(from iPhone)" copy.
    @discardableResult
    public func join(importing legacyBoard: URL?) async throws -> SyncResult {
        var first = try await run(publish: false)
        // Not everything came in yet (a device's files still arriving, or one that couldn't be read): importing now
        // could copy tasks that are on their way. Nothing is imported or published; join again later.
        guard first.complete else {
            first.problem = [first.problem, "Not everything from your other devices has arrived yet; your tasks from before are brought in once it has."].compactMap { $0 }.joined(separator: " ")
            return first
        }
        // Sync turned off (or another place chosen) meanwhile: nothing is brought in or published.
        try Task.checkCancellation()
        if let legacyBoard { try store.importBeside(legacyBoard: legacyBoard) }
        return try await run(publish: true)
    }

    /// How the last sync went (for the Sync screen).
    public var status: SyncStatus { state.status }

    /// Forgets a conflict once it's been settled.
    public func dismiss(_ key: String) {
        var status = state.status
        status.conflicts.removeAll { $0.key == key }
        state.status = status
    }

    /// Stops merging a device (a phone or computer that's gone).
    public func forget(_ device: String) async throws {
        try await remote.forget(device)
        try state.forgetPeer(device)
        state.remoteVersion = nil
        var status = state.status
        status.devices.removeAll { $0.device == device }
        state.status = status
    }

    private func run(publish: Bool) async throws -> SyncResult {
        var attempt = 1
        while true {
            do {
                let result = try await syncOnce(publish: publish)
                var open: [SyncConflictRecord] = []
                for c in state.status.conflicts + result.conflicts where !open.contains(where: { $0.key == c.key && $0.mine == c.mine && $0.theirs == c.theirs }) { open.append(c) }
                var status = SyncStatus(lastSync: Instant(clock()), lastError: result.problem, peers: result.peers, conflicts: Array(open.suffix(50)), devices: state.status.devices)
                if let devices = result.devices { status.devices = devices }
                state.status = status
                return result
            } catch is SyncStaleError where attempt < 3 {
                // The vault changed while we worked: start over (and merge everything again).
                state.remoteVersion = nil
                attempt += 1
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                var status = state.status
                status.lastError = error.localizedDescription
                state.status = status
                throw error
            }
        }
    }

    private struct TombstoneKey: Hashable {
        let key: OrdinalString
        let hash: String
        init(_ key: String, _ hash: String) {
            self.key = OrdinalString(key)
            self.hash = hash
        }
    }

    private func syncOnce(publish: Bool) async throws -> SyncResult {
        let now = Instant(clock())

        // A new place (another folder, another gist): everything is published there again.
        if state.remoteIdentity != remote.identity {
            state.remoteIdentity = remote.identity
            state.remoteVersion = nil
            state.published = nil
        }

        var local = try store.readSync()
        let remoteSnapshot = try await remote.read(self: state.deviceId, knownVersion: state.remoteVersion)
        let heartbeatDue = state.publishedAt.map { now.seconds(since: $0) > Double(SyncLimits.heartbeatSeconds) } ?? true
        if remoteSnapshot == nil && Self.fingerprint(local) == state.published && !heartbeatDue {
            return SyncResult(changes: 0, peers: state.peers().count, conflicts: [], published: false, skipped: true)
        }

        // Devices not heard from in months are no longer merged (their old copies would bring back deleted tasks).
        let peers = (remoteSnapshot?.devices ?? [])
            .filter { $0.device != state.deviceId && now.seconds(since: $0.at) < Double(SyncLimits.memorySeconds) }
            .sorted { Ordinal.less($0.device, $1.device) }

        // A delete counts against the version it deleted, and only if it happened after that version appeared there.
        var deleted: [TombstoneKey: Instant] = [:]
        for t in peers.flatMap(\.deleted) {
            let k = TombstoneKey(t.key, t.hash)
            if let at = deleted[k], at > t.at { continue }
            deleted[k] = t.at
        }
        var publishedSince: [TombstoneKey: Instant] = [:]
        for e in state.publishedEntries { if let since = e.since { publishedSince[TombstoneKey(e.key, e.hash)] = since } }

        // A device with no sync history may hold copies of files deleted elsewhere since: its files count as old, so
        // existing deletes apply. Later, a file that wasn't published yet is new.
        let unpublished = state.publishedEntries.isEmpty && state.peers().isEmpty ? Instant.minValue : now
        let store = self.store
        let options = SyncPlanOptions(
            deleted: { e in
                guard let at = deleted[TombstoneKey(e.key, e.hash)] else { return false }
                return at > (e.since ?? publishedSince[TombstoneKey(e.key, e.hash)] ?? unpublished)
            },
            isValidTask: { store.isValidTaskText($0, $1) })

        var changes = 0
        var conflicts: [SyncConflictRecord] = []
        var problems: [String] = []
        var complete = !(remoteSnapshot?.partial ?? false)
        for peer in peers {
            do {
                guard let entries = Self.valid(peer) else { continue }
                let fetched = try await fetch(peer, entries, local)
                if !fetched.ok {
                    if let problem = fetched.problem { problems.append(problem) }
                    complete = false
                    continue
                }
                let base = try state.loadBase(peer.device)
                let state = self.state
                let plan = try SyncPlanner.plan(
                    local: SyncSide(device: state.deviceId, name: state.deviceName, entries: local.entries, read: local.read),
                    peer: SyncSide(device: peer.device, name: peer.name, entries: entries) { e in
                        guard let data = state.readBlob(e.hash) else { throw SyncStaleError("\(e.path) is missing.") }
                        return data
                    },
                    base: base,
                    readBase: { state.readBlob($0) },
                    options: options)

                // Both versions of a clash stay available (to keep one of them).
                for conflict in plan.conflicts { try state.writeBlob(conflict.mine, try local.read(local.entries[OrdinalString(conflict.key)]!)) }

                if !plan.ops.isEmpty {
                    try store.applySync(plan.ops)
                    changes += plan.ops.count
                    local = try store.readSync()
                }
                conflicts += plan.conflicts.map { SyncConflictRecord(key: $0.key, path: $0.path, peer: $0.peer, peerName: $0.peerName, mine: $0.mine, theirs: $0.theirs, base: $0.base, at: now, result: $0.result) }

                // Remember what both now have; text needs its content kept for the next three-way merge.
                let next = SyncPlanner.nextBase(local: local.entries, peer: entries, oldBase: base)
                for entry in next.values where Self.needsBase(entry.key) && !state.hasBlob(entry.hash) {
                    try state.writeBlob(entry.hash, try local.read(entry))
                }
                try state.saveBase(peer.device, next)
            } catch let error as SyncStaleError {
                throw error
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                // One device's odd data never stops this one syncing with the others (or publishing).
                problems.append("\(peer.name): \(error.localizedDescription)")
                complete = false
            }
        }

        if let remoteSnapshot, complete { state.remoteVersion = remoteSnapshot.version }
        if remoteSnapshot?.partial == true {
            problems.append("Another device's sync file couldn't be read (it may be from a newer version of Todo Tracker); it's tried again.")
        }

        var published = false
        let fingerprint = Self.fingerprint(local)
        if publish && (fingerprint != state.published || heartbeatDue || remoteSnapshot?.selfMissing == true) {
            // What disappeared since the last publish was deleted here: say so (for months) so it stays gone everywhere.
            var tombstones: [SyncTombstone] = []
            var seen = Set<TombstoneKey>()
            let gone = state.tombstones.filter { now.seconds(since: $0.at) < Double(SyncLimits.memorySeconds) && local.entries[OrdinalString($0.key)] == nil }
                + state.publishedEntries.filter { e in
                    let kind = SyncKeys.kind(of: e.key)
                    // A file that is still there (unreadable for now, or an iCloud placeholder) wasn't deleted.
                    return local.entries[OrdinalString(e.key)] == nil && (kind == .task || kind == .rich || kind == .file) && !store.isPresent(e.path)
                }.map { SyncTombstone(key: $0.key, hash: $0.hash, at: now) }
            for t in gone where seen.insert(TombstoneKey(t.key, t.hash)).inserted { tombstones.append(t) }
            let entriesNow = local.entries.values.map { e -> SyncEntry in
                var copy = e
                copy.since = publishedSince[TombstoneKey(e.key, e.hash)] ?? now
                return copy
            }.sorted { Ordinal.less($0.key, $1.key) }
            let mine = DeviceSnapshot(device: state.deviceId, name: state.deviceName, at: now, entries: entriesNow, deleted: tombstones)
            // Sync turned off meanwhile: what came in stays, nothing goes out.
            try Task.checkCancellation()
            let version = try await remote.publish(mine, read: local.read, readVersion: state.remoteVersion)
            state.tombstones = tombstones
            state.publishedEntries = entriesNow
            state.published = fingerprint
            state.publishedAt = now
            if complete { state.remoteVersion = version }
            published = true
        }

        collectBlobs(conflicts)
        let devices = remoteSnapshot == nil ? nil : peers.map { SyncDevice(device: $0.device, name: $0.name, at: $0.at) }
        return SyncResult(changes: changes, peers: peers.count, conflicts: conflicts, published: published, skipped: false,
                          problem: problems.isEmpty ? nil : problems.joined(separator: " "), devices: devices, complete: complete)
    }

    /// Brings the peer's contents we don't have yet into the local cache. False if some haven't arrived.
    private func fetch(_ peer: DeviceSnapshot, _ entries: SyncEntries, _ local: SyncSnapshot) async throws -> (ok: Bool, problem: String?) {
        for entry in entries.values {
            if local.entries[OrdinalString(entry.key)]?.hash == entry.hash || state.hasBlob(entry.hash) { continue }
            let content: Data
            do {
                content = try await remote.readContent(peer, entry)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                // Not there yet, still downloading (iCloud placeholders), or too big: this device is merged later.
                return (false, nil)
            }
            // Still being written (or damaged): said, and tried again next time (that device rewrites it when it syncs).
            if SyncHash.of(content) != entry.hash { return (false, "\(entry.path) from \(peer.name) didn't arrive whole; it's tried again.") }
            try state.writeBlob(entry.hash, content)
        }
        return (true, nil)
    }

    /// The peer's entries we accept: well-formed ids, hashes and paths only (it's data from elsewhere).
    static func valid(_ peer: DeviceSnapshot) -> SyncEntries? {
        guard peer.device.utf16.count > 1, peer.device.utf16.count <= 64, Ordinal.isAsciiLetterOrDigit(peer.device) else { return nil }
        var entries: SyncEntries = [:]
        for e in peer.entries {
            guard e.hash.utf16.count == 64, Ordinal.isLowerHex(e.hash) else { continue }
            let ok: Bool
            switch SyncKeys.kind(of: e.key) {
            case .config: ok = e.path == SyncKeys.configPath
            case .order: ok = e.path == SyncKeys.orderPath
            case .activity: ok = e.path == SyncKeys.activityPath
            case .task: ok = e.path.oEndsIgnoreCase(".md") && VaultFiles.isSyncablePath(e.path) && parseGuidN(e.key.sub(2)) != nil
            case .rich: ok = e.path.oEndsIgnoreCase(".html") && VaultFiles.isSyncablePath(e.path) && parseGuidN(e.key.sub(2)) != nil
            case .file: ok = e.key.oEq(SyncKeys.file(e.path)) && VaultFiles.isSyncablePath(e.path)
            }
            if ok {
                // Older devices don't say since when they have a version: oldest possible (a later delete wins).
                var entry = e
                if entry.since == nil { entry.since = .minValue }
                entries[OrdinalString(e.key)] = entry
            }
        }
        return entries
    }

    private static func needsBase(_ key: String) -> Bool {
        let kind = SyncKeys.kind(of: key)
        return kind == .task || kind == .rich || kind == .config || kind == .order
    }

    static func fingerprint(_ snapshot: SyncSnapshot) -> String {
        SyncHash.of(snapshot.entries.values.sorted { Ordinal.less($0.key, $1.key) }.map { "\($0.key)|\($0.path)|\($0.hash)" }.joined(separator: "\n"))
    }

    private func collectBlobs(_ fresh: [SyncConflictRecord]) {
        var keep: [String] = []
        for peer in state.peers() {
            for e in ((try? state.loadBase(peer)) ?? [:]).values where Self.needsBase(e.key) { keep.append(e.hash) }
        }
        for c in state.status.conflicts + fresh { keep += [c.mine, c.theirs] + (c.base.map { [$0] } ?? []) }
        state.collectBlobs(keep: keep)
    }
}
