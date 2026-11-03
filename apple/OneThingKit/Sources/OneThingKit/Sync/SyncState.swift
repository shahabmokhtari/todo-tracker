import Foundation

/// A sync that couldn't be merged by itself; both versions are kept (see `SyncConflict`).
public struct SyncConflictRecord: Equatable, Sendable {
    public var key, path, peer, peerName, mine, theirs: String
    public var base: String?
    public var at: Instant
    /// The hash of what the merge wrote (choosing a side only applies while the file is still that).
    public var result: String?

    var json: JSONValue {
        .object(JSONObject([("key", .string(key)), ("path", .string(path)), ("peer", .string(peer)), ("peerName", .string(peerName)), ("mine", .string(mine)),
                            ("theirs", .string(theirs)), ("base", base.map { .string($0) } ?? .null), ("at", .instant(at)), ("result", result.map { .string($0) } ?? .null)]))
    }

    init(key: String, path: String, peer: String, peerName: String, mine: String, theirs: String, base: String?, at: Instant, result: String?) {
        (self.key, self.path, self.peer, self.peerName, self.mine, self.theirs, self.base, self.at, self.result) = (key, path, peer, peerName, mine, theirs, base, at, result)
    }

    init?(json: JSONValue) {
        guard let o = json.objectValue, let key = o["key"]?.stringValue, let path = o["path"]?.stringValue, let peer = o["peer"]?.stringValue,
              let mine = o["mine"]?.stringValue, let theirs = o["theirs"]?.stringValue else { return nil }
        self.init(key: key, path: path, peer: peer, peerName: o["peerName"]?.stringValue ?? peer, mine: mine, theirs: theirs, base: o["base"]?.stringValue,
                  at: o["at"]?.stringValue.flatMap { Instant(parsing: $0) } ?? .minValue, result: o["result"]?.stringValue)
    }
}

/// Another device syncing through the same place, and when it last published.
public struct SyncDevice: Equatable, Sendable {
    public var device: String
    public var name: String
    public var at: Instant
}

/// How the last sync went, for the status line and the Sync screen.
public struct SyncStatus: Equatable, Sendable {
    public var lastSync: Instant?
    public var lastError: String?
    public var peers = 0
    /// Open conflicts (up to 50): "Changed on both: Trip — with Laptop".
    public var conflicts: [SyncConflictRecord] = []
    public var devices: [SyncDevice] = []

    public static let empty = SyncStatus()
}

/// What one tasks folder remembers about syncing with one place, kept in the app's private storage (never in the
/// vault or the cloud): this device's id, what it last agreed on with each other device (the merge base), the
/// contents those bases need, what it last published, and the open conflicts. Same content as C#'s `SyncState`.
public final class SyncState {
    private let dir: URL
    private let fm = FileManager.default
    private let lock = NSRecursiveLock()

    public let deviceId: String
    public let deviceName: String

    /// `directory`: this place's state; `deviceDirectory`: shared by every place this vault syncs with (one device id
    /// per vault).
    public init(directory: URL, deviceDirectory: URL, deviceName: String? = nil) throws {
        dir = directory
        try fm.createDirectory(at: directory, withIntermediateDirectories: true)
        try fm.createDirectory(at: deviceDirectory, withIntermediateDirectories: true)
        let devicePath = deviceDirectory.appendingPathComponent("device.json")
        var info = (try? Data(contentsOf: devicePath)).flatMap { try? JSONText.parse($0).objectValue }
        if info?["id"]?.stringValue == nil {
            info = JSONObject([("id", .string(guidN(UUID()))), ("name", .string(deviceName ?? Self.defaultName))])
            try Self.write(devicePath, .object(info!))
        }
        deviceId = info!["id"]!.stringValue!
        self.deviceName = deviceName ?? info?["name"]?.stringValue ?? Self.defaultName
    }

    /// A fixed name until the app passes the person's device name (looking up the host name can block on slow DNS).
    private static var defaultName: String { TaskBoard.thisDevice }

    // ---- Marks ----------------------------------------------------------------------

    private var marksURL: URL { dir.appendingPathComponent("marks.json") }

    private func mark(_ key: String) -> JSONValue? { read(marksURL)?.objectValue?[key] }

    private func setMark(_ key: String, _ value: JSONValue?) {
        lock.lock()
        defer { lock.unlock() }
        var marks = read(marksURL)?.objectValue ?? JSONObject()
        marks[key] = value
        try? Self.write(marksURL, .object(marks))
    }

    /// The remote's version (a gist's ETag) when we last merged everything it had.
    public var remoteVersion: String? {
        get { mark("remoteVersion")?.stringValue }
        set { setMark("remoteVersion", newValue.map { .string($0) }) }
    }

    /// A hash of what we last published (to skip publishing an unchanged vault).
    public var published: String? {
        get { mark("published")?.stringValue }
        set { setMark("published", newValue.map { .string($0) }) }
    }

    /// When we last published.
    public var publishedAt: Instant? {
        get { mark("publishedAt")?.stringValue.flatMap { Instant(parsing: $0) } }
        set { setMark("publishedAt", newValue.map { .instant($0) }) }
    }

    /// Which place we last synced with (a new one gets everything published again).
    public var remoteIdentity: String? {
        get { mark("remoteIdentity")?.stringValue }
        set { setMark("remoteIdentity", newValue.map { .string($0) }) }
    }

    /// The entries we last published (what disappeared since was deleted here).
    public var publishedEntries: [SyncEntry] {
        get { (read(dir.appendingPathComponent("published.json"))?.arrayValue ?? []).compactMap { SyncEntry(json: $0) } }
        set { try? Self.write(dir.appendingPathComponent("published.json"), .array(newValue.map(\.json))) }
    }

    /// What this device deleted lately.
    public var tombstones: [SyncTombstone] {
        get { (read(dir.appendingPathComponent("deleted.json"))?.arrayValue ?? []).compactMap { SyncTombstone(json: $0) } }
        set { try? Self.write(dir.appendingPathComponent("deleted.json"), .array(newValue.map(\.json))) }
    }

    // ---- Bases and contents ---------------------------------------------------------

    private var basesDir: URL { dir.appendingPathComponent("bases") }
    private var blobDir: URL { dir.appendingPathComponent("blobs") }

    public func loadBase(_ peer: String) throws -> SyncEntries {
        var result: SyncEntries = [:]
        for e in (read(try basePath(peer))?.arrayValue ?? []).compactMap({ SyncEntry(json: $0) }) { result[OrdinalString(e.key)] = e }
        return result
    }

    public func saveBase(_ peer: String, _ entries: SyncEntries) throws {
        try Self.write(try basePath(peer), .array(entries.sorted { $0.key < $1.key }.map(\.value.json)))
    }

    /// Drops what we agreed on with a device that's gone.
    public func forgetPeer(_ peer: String) throws { try? fm.removeItem(at: try basePath(peer)) }

    /// The devices we have a base with.
    public func peers() -> [String] {
        ((try? fm.contentsOfDirectory(atPath: basesDir.path)) ?? []).filter { $0.hasSuffix(".json") }.map { String($0.dropLast(5)) }
    }

    public func readBlob(_ hash: String) -> Data? { (try? blobPath(hash)).flatMap { fm.contents(atPath: $0.path) } }

    public func hasBlob(_ hash: String) -> Bool { (try? blobPath(hash)).map { fm.fileExists(atPath: $0.path) } ?? false }

    public func writeBlob(_ hash: String, _ content: Data) throws {
        let path = try blobPath(hash)
        if fm.fileExists(atPath: path.path) { return }
        try fm.createDirectory(at: path.deletingLastPathComponent(), withIntermediateDirectories: true)
        try content.write(to: path, options: .atomic)
    }

    /// Drops cached contents no merge base or open conflict needs any more.
    public func collectBlobs(keep: [String]) {
        let needed = Set(keep)
        guard let enumerator = fm.enumerator(atPath: blobDir.path) else { return }
        while let rel = enumerator.nextObject() as? String {
            let name = (rel as NSString).lastPathComponent
            if name.count == 64 && !needed.contains(name) { try? fm.removeItem(atPath: blobDir.appendingPathComponent(rel).path) }
        }
    }

    // ---- Status ---------------------------------------------------------------------

    public var status: SyncStatus {
        get {
            guard let o = read(dir.appendingPathComponent("status.json"))?.objectValue else { return .empty }
            var s = SyncStatus()
            s.lastSync = o["lastSync"]?.stringValue.flatMap { Instant(parsing: $0) }
            s.lastError = o["lastError"]?.stringValue
            s.peers = Int(o["peers"]?.int64Value ?? 0)
            s.conflicts = (o["conflicts"]?.arrayValue ?? []).compactMap { SyncConflictRecord(json: $0) }
            s.devices = (o["devices"]?.arrayValue ?? []).compactMap { d in
                guard let o = d.objectValue, let id = o["device"]?.stringValue else { return nil }
                return SyncDevice(device: id, name: o["name"]?.stringValue ?? id, at: o["at"]?.stringValue.flatMap { Instant(parsing: $0) } ?? .minValue)
            }
            return s
        }
        set {
            var o = JSONObject()
            if let at = newValue.lastSync { o["lastSync"] = .instant(at) }
            if let error = newValue.lastError { o["lastError"] = .string(error) }
            o["peers"] = .int(newValue.peers)
            o["conflicts"] = .array(newValue.conflicts.map(\.json))
            o["devices"] = .array(newValue.devices.map { .object(JSONObject([("device", .string($0.device)), ("name", .string($0.name)), ("at", .instant($0.at))])) })
            try? Self.write(dir.appendingPathComponent("status.json"), .object(o))
        }
    }

    // ---- Files ----------------------------------------------------------------------

    private func basePath(_ peer: String) throws -> URL { basesDir.appendingPathComponent(try Self.safeId(peer) + ".json") }

    private func blobPath(_ hash: String) throws -> URL {
        let id = try Self.safeId(hash)
        return blobDir.appendingPathComponent(String(id.prefix(2))).appendingPathComponent(id)
    }

    /// Ids come from other devices: only letters and digits reach a file name.
    static func safeId(_ id: String) throws -> String {
        guard id.count > 1, id.count <= 128, Ordinal.isAsciiLetterOrDigit(id) else { throw SyncDataError("Not a valid sync id: \(id)") }
        return id
    }

    private func read(_ url: URL) -> JSONValue? { fm.contents(atPath: url.path).flatMap { try? JSONText.parse($0) } }

    private static func write(_ url: URL, _ value: JSONValue) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(JSONText.write(value, indented: true).utf8).write(to: url, options: .atomic)
    }
}
