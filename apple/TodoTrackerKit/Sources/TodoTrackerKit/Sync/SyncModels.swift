import CryptoKit
import Foundation

// The sync protocol (docs/sync.md), exactly as the C# core speaks it: a phone is just another device.

/// What a sync entry holds; decides how two versions are merged.
public enum SyncKind: Sendable {
    /// A task's markdown file, keyed by the task's id so renames and moves merge with edits.
    case task
    /// A task's rich (HTML) page, keyed by the task's id.
    case rich
    /// Any other file (attachments, images, other notes), keyed by its path.
    case file
    /// Tabs, labels and the task order (`.todo-tracker/config.json`), merged field by field.
    case config
    /// The manual Now order, merged as a list.
    case order
    /// The activity log; every device's entries are kept.
    case activity
}

/// One syncable thing: `path` is vault-relative with forward slashes; `hash` is the SHA-256 of its bytes.
public struct SyncEntry: Equatable, Sendable {
    public var key: String
    public var path: String
    public var hash: String
    /// When the publishing device first had this version (a delete recorded later wins; an earlier one doesn't).
    public var since: Instant?

    public init(key: String, path: String, hash: String, since: Instant? = nil) {
        self.key = key
        self.path = path
        self.hash = hash
        self.since = since
    }

    public static func == (a: SyncEntry, b: SyncEntry) -> Bool {
        a.key.oEq(b.key) && a.path.oEq(b.path) && a.hash == b.hash && a.since == b.since
    }

    /// Same content at the same place (the path compared ordinally, as C# does).
    func samePlaceAndContent(_ other: SyncEntry) -> Bool { hash == other.hash && path.oEq(other.path) }

    var json: JSONValue {
        .object(JSONObject([("key", .string(key)), ("path", .string(path)), ("hash", .string(hash)), ("since", since.map { .instant($0) } ?? .null)]))
    }

    init?(json: JSONValue?) {
        guard let o = json?.objectValue, let key = o.get("key")?.stringValue, let path = o.get("path")?.stringValue, let hash = o.get("hash")?.stringValue else { return nil }
        self.init(key: key, path: path, hash: hash, since: o.get("since")?.stringValue.flatMap { Instant(parsing: $0) })
    }
}

/// Entries by key, compared ordinally (two paths that differ only in Unicode normalization are two entries).
public typealias SyncEntries = [OrdinalString: SyncEntry]

public enum SyncKeys {
    public static let config = "config"
    public static let order = "order"
    public static let activity = "activity"
    public static let configPath = ".todo-tracker/config.json"
    public static let orderPath = ".todo-tracker/order.json"
    public static let activityPath = ".todo-tracker/activity.jsonl"

    public static func task(_ id: UUID) -> String { "t/" + guidN(id) }
    public static func rich(_ id: UUID) -> String { "h/" + guidN(id) }
    public static func file(_ path: String) -> String { "f/" + path }

    public static func kind(of key: String) -> SyncKind {
        switch key {
        case config: return .config
        case order: return .order
        case activity: return .activity
        default:
            if key.oStarts("t/") { return .task }
            if key.oStarts("h/") { return .rich }
            return .file
        }
    }
}

public enum SyncHash {
    public static func of(_ content: Data) -> String {
        SHA256.hash(data: content).map { String(format: "%02x", $0) }.joined()
    }

    public static func of(_ text: String) -> String { of(Data(text.utf8)) }
}

public enum SyncLimits {
    /// Bigger files aren't synced.
    public static let maxFileBytes: Int64 = 50 * 1024 * 1024
    /// Deletes are remembered this long; devices not heard from this long are no longer merged.
    public static let memorySeconds: Int64 = 180 * 86_400
    /// A device that changed nothing still publishes this often, so others know it's alive.
    public static let heartbeatSeconds: Int64 = 7 * 86_400
}

/// A device deleted this version of this entry at this time.
public struct SyncTombstone: Equatable, Sendable {
    public var key: String
    public var hash: String
    public var at: Instant

    public init(key: String, hash: String, at: Instant) {
        self.key = key
        self.hash = hash
        self.at = at
    }

    var json: JSONValue { .object(JSONObject([("key", .string(key)), ("hash", .string(hash)), ("at", .instant(at))])) }

    init?(json: JSONValue?) {
        guard let o = json?.objectValue, let key = o.get("key")?.stringValue, let hash = o.get("hash")?.stringValue,
              let at = o.get("at")?.stringValue.flatMap({ Instant(parsing: $0) }) else { return nil }
        self.init(key: key, hash: hash, at: at)
    }
}

/// What one device published: everything in its vault, by key. Contents are stored separately by hash.
public struct DeviceSnapshot: Sendable {
    public var device: String
    public var name: String
    public var at: Instant
    public var entries: [SyncEntry]
    public var schema = 1
    /// What this device deleted lately (so a device that comes back with old copies doesn't bring them back).
    public var deleted: [SyncTombstone] = []

    public init(device: String, name: String, at: Instant, entries: [SyncEntry], deleted: [SyncTombstone] = []) {
        self.device = device
        self.name = name
        self.at = at
        self.entries = entries
        self.deleted = deleted
    }

    /// The device file's JSON, exactly as C# writes it (System.Text.Json web defaults).
    var json: JSONValue {
        .object(JSONObject([("device", .string(device)), ("name", .string(name)), ("at", .instant(at)),
                            ("entries", .array(entries.map(\.json))), ("schema", .int(schema)), ("deleted", .array(deleted.map(\.json)))]))
    }

    public func encoded() -> Data { Data(JSONText.write(json).utf8) }

    /// Reads a device file; nil when it isn't one (or isn't schema 1). A damaged one throws (never read as a device
    /// that deleted what it lost).
    public static func decode(_ data: Data) throws -> DeviceSnapshot? { try from(try JSONText.parse(data)) }

    static func from(_ value: JSONValue?) throws -> DeviceSnapshot? {
        guard let o = value?.objectValue, let device = o.get("device")?.stringValue, let entries = o.get("entries")?.arrayValue else { return nil }
        let schema = o.get("schema")?.int64Value ?? 1
        guard schema == 1 else { return nil }
        let at = o.get("at")?.stringValue.flatMap { Instant(parsing: $0) } ?? .minValue
        let parsed = entries.compactMap { SyncEntry(json: $0) }
        let deleted = (o.get("deleted")?.arrayValue ?? []).compactMap { SyncTombstone(json: $0) }
        guard parsed.count == entries.count else { throw JSONError(description: "A device file has damaged entries.") }
        var snapshot = DeviceSnapshot(device: device, name: o.get("name")?.stringValue ?? "", at: at, entries: parsed)
        snapshot.deleted = deleted
        return snapshot
    }

    public func byKey() -> SyncEntries {
        var result: SyncEntries = [:]
        for e in entries { result[OrdinalString(e.key)] = e }
        return result
    }
}

/// One side of a merge: its entries and a way to read an entry's content.
public struct SyncSide {
    public var device: String
    public var name: String
    public var entries: SyncEntries
    public var read: (SyncEntry) throws -> Data

    public init(device: String, name: String, entries: SyncEntries, read: @escaping (SyncEntry) throws -> Data) {
        self.device = device
        self.name = name
        self.entries = entries
        self.read = read
    }
}

/// This vault's syncable entries; `read` throws `SyncStaleError` if a file changed since.
public struct SyncSnapshot {
    public var entries: SyncEntries
    public var read: (SyncEntry) throws -> Data
}

/// Raised when the vault changed while a sync was being worked out; the sync starts over.
public struct SyncStaleError: Error, LocalizedError {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// Data from another device (or a remote) that isn't acceptable; that device is skipped this time.
public struct SyncDataError: Error, LocalizedError {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

public enum SyncOpKind: String, Sendable {
    /// Write `content` to `path`; `expected` nil means the file must not exist yet.
    case write
    /// Move the file at `path` to `to`.
    case move
    /// Move the file to the vault's trash.
    case delete
    /// Append these activity lines.
    case appendActivity
    /// Replace the tabs/labels/order settings.
    case setConfig
    /// Replace the manual Now order (a JSON array of task ids).
    case setOrder
}

/// One change to make to the vault; `expected` is the hash the file must still have (else the sync starts over).
public struct SyncOp: Sendable {
    public var kind: SyncOpKind
    public var path: String
    public var expected: String?
    public var content: Data?
    public var to: String?

    public init(_ kind: SyncOpKind, _ path: String, _ expected: String?, content: Data? = nil, to: String? = nil) {
        self.kind = kind
        self.path = path
        self.expected = expected
        self.content = content
        self.to = to
    }
}

/// Both devices changed the same lines; both versions' lines were kept (the result is the same on both devices).
/// `result` is the hash of what was written (choosing a side later only applies if the file is still that).
public struct SyncConflict: Equatable, Sendable {
    public var key, path, peer, peerName, mine, theirs: String
    public var base: String?
    public var result: String
}

public struct SyncPlan {
    public var ops: [SyncOp]
    public var conflicts: [SyncConflict]
}
