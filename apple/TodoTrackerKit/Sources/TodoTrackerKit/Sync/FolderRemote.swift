import Foundation

/// How the folder remote touches files: plainly in tests and on a Mac, and on iOS inside the security scope of the
/// folder the person picked, through `NSFileCoordinator` (so the iCloud Drive / OneDrive file provider sees every read
/// and write). Paths are file URLs inside the folder.
public protocol SyncFolderAccess: AnyObject {
    /// Runs `body` with the folder reachable (security scope opened and closed around it).
    func withAccess<T>(_ body: () throws -> T) throws -> T
    /// File names in a folder (empty when it doesn't exist).
    func list(_ folder: URL) throws -> [String]
    func read(_ file: URL) throws -> Data
    /// Writes a whole file: a temp file (`.<name>.<8 hex>.tmp`, skipped by other devices) renamed over it.
    func write(_ data: Data, to file: URL) throws
    func exists(_ file: URL) -> Bool
    func modified(_ file: URL) -> Date?
    func size(_ file: URL) -> Int64?
    func setModified(_ file: URL, _ date: Date) throws
    func remove(_ file: URL) throws
    /// A file that isn't downloaded yet (an iCloud placeholder, `.<name>.icloud`): asks for it; true when it was one.
    func requestDownload(_ file: URL) -> Bool
}

/// `SyncFolderAccess` through `NSFileCoordinator`, optionally inside a security-scoped URL (the folder picked with
/// `UIDocumentPickerViewController`, kept as a bookmark).
public final class CoordinatedFolderAccess: SyncFolderAccess {
    private let scope: URL?
    private let fm = FileManager.default

    /// `securityScoped`: the folder came from the document picker (or a bookmark of it).
    public init(securityScoped scope: URL? = nil) { self.scope = scope }

    public func withAccess<T>(_ body: () throws -> T) throws -> T {
        let opened = scope?.startAccessingSecurityScopedResource() ?? false
        defer { if opened { scope?.stopAccessingSecurityScopedResource() } }
        return try body()
    }

    private func coordinate<T>(reading url: URL, _ body: (URL) throws -> T) throws -> T {
        var coordinationError: NSError?
        var result: Result<T, Error>?
        NSFileCoordinator().coordinate(readingItemAt: url, options: [], error: &coordinationError) { u in result = Result { try body(u) } }
        if let coordinationError { throw coordinationError }
        return try result!.get()
    }

    private func coordinate<T>(writing url: URL, options: NSFileCoordinator.WritingOptions, _ body: (URL) throws -> T) throws -> T {
        var coordinationError: NSError?
        var result: Result<T, Error>?
        NSFileCoordinator().coordinate(writingItemAt: url, options: options, error: &coordinationError) { u in result = Result { try body(u) } }
        if let coordinationError { throw coordinationError }
        return try result!.get()
    }

    public func list(_ folder: URL) throws -> [String] {
        guard fm.fileExists(atPath: folder.path) else { return [] }
        return try coordinate(reading: folder) { try fm.contentsOfDirectory(atPath: $0.path) }
    }

    public func read(_ file: URL) throws -> Data { try coordinate(reading: file) { try Data(contentsOf: $0) } }

    public func write(_ data: Data, to file: URL) throws {
        let folder = file.deletingLastPathComponent()
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let temp = folder.appendingPathComponent("." + file.lastPathComponent + "." + String(guidN(UUID()).prefix(8)) + ".tmp")
        try coordinate(writing: file, options: .forReplacing) { target in
            do {
                try data.write(to: temp)
                if rename(temp.path, target.path) != 0 { throw VaultFiles.posixError(target.path) }
            } catch {
                try? fm.removeItem(at: temp)
                throw error
            }
        }
    }

    public func exists(_ file: URL) -> Bool { fm.fileExists(atPath: file.path) }

    public func modified(_ file: URL) -> Date? { (try? fm.attributesOfItem(atPath: file.path))?[.modificationDate] as? Date }

    public func size(_ file: URL) -> Int64? { (try? fm.attributesOfItem(atPath: file.path))?[.size] as? Int64 }

    public func setModified(_ file: URL, _ date: Date) throws {
        try coordinate(writing: file, options: .contentIndependentMetadataOnly) { try fm.setAttributes([.modificationDate: date], ofItemAtPath: $0.path) }
    }

    public func remove(_ file: URL) throws {
        try coordinate(writing: file, options: .forDeleting) { try fm.removeItem(at: $0) }
    }

    public func requestDownload(_ file: URL) -> Bool {
        let placeholder = file.deletingLastPathComponent().appendingPathComponent("." + file.lastPathComponent + ".icloud")
        guard fm.fileExists(atPath: placeholder.path) else { return false }
        try? fm.startDownloadingUbiquitousItem(at: file)
        return true
    }
}

/// A shared folder as the meeting place (`Apps/TodoTrackerSync/<library>` in iCloud Drive or OneDrive):
/// `devices/<id>.json` per device and `blobs/<ab>/<hash>` for contents. Files are only ever added or replaced whole,
/// contents before the snapshot that lists them. Port of C#'s `FolderRemote`, byte for byte (sync/manifest.json).
public final class FolderRemote: SyncRemote {
    public let root: URL
    public let identity: String
    private let access: SyncFolderAccess
    private let clock: () -> Date
    private var lastCollect = Date.distantPast

    /// `identity` defaults to the folder's path (a new place publishes everything again).
    public init(root: URL, access: SyncFolderAccess = CoordinatedFolderAccess(), identity: String? = nil, clock: @escaping () -> Date = Date.init) {
        self.root = root
        self.access = access
        self.identity = identity ?? "folder:" + root.standardizedFileURL.path
        self.clock = clock
    }

    private var devicesDir: URL { root.appendingPathComponent("devices") }

    public func forget(_ device: String) async throws {
        guard device.utf16.count > 1, device.utf16.count <= 64, Ordinal.isAsciiLetterOrDigit(device) else { throw SyncDataError("Not a device id.") }
        try access.withAccess { try access.remove(devicesDir.appendingPathComponent(device + ".json")) }
    }

    /// The other devices there, without reading or downloading anything else.
    public func peek(self device: String) throws -> [SyncDevice] {
        try access.withAccess {
            try access.list(devicesDir).filter { $0.hasSuffix(".json") && !$0.hasPrefix(".") && !Ordinal.equalIgnoreCase(String($0.dropLast(5)), device) }.compactMap { name in
                guard let data = try? access.read(devicesDir.appendingPathComponent(name)), let snapshot = try? DeviceSnapshot.decode(data) else { return nil }
                return SyncDevice(device: snapshot.device, name: snapshot.name, at: snapshot.at)
            }
        }
    }

    public func read(self device: String, knownVersion: String?) async throws -> RemoteSnapshot? {
        try access.withAccess {
            try FileManager.default.createDirectory(at: devicesDir, withIntermediateDirectories: true)
            let names = try access.list(devicesDir)
            let selfMissing = !names.contains { Ordinal.equalIgnoreCase($0, device + ".json") }
            var files: [(name: String, content: Data)] = []
            var waiting = false
            for name in names.sorted(by: Ordinal.less) {
                if name.hasPrefix(".") {
                    // A device file iCloud hasn't downloaded yet: ask for it, and merge that device next time.
                    if name.hasSuffix(".json.icloud"), !Ordinal.equalIgnoreCase(name, "." + device + ".json.icloud") {
                        _ = access.requestDownload(devicesDir.appendingPathComponent(String(name.dropFirst().dropLast(7))))
                        waiting = true
                    }
                    continue
                }
                guard name.hasSuffix(".json"), !Ordinal.equalIgnoreCase(String(name.dropLast(5)), device) else { continue }
                // Being replaced right now: seen next time.
                if let content = try? access.read(devicesDir.appendingPathComponent(name)) { files.append((name, content)) }
            }

            // The other devices' files by content (times and sizes can stay the same across quick writes).
            let version = SyncHash.of(files.map { "\($0.name)|\(SyncHash.of($0.content))" }.joined(separator: "\n"))
            if version == knownVersion && !selfMissing && !waiting { return nil }
            var devices: [DeviceSnapshot] = []
            var partial = waiting
            for (name, content) in files {
                do {
                    if let snapshot = try DeviceSnapshot.decode(content), Ordinal.equalIgnoreCase(snapshot.device, String(name.dropLast(5))) { devices.append(snapshot) }
                } catch {
                    // Half-synced or from a newer version: skipped, and looked at again next time.
                    partial = true
                }
            }
            return RemoteSnapshot(devices: devices, version: version, partial: partial, selfMissing: selfMissing)
        }
    }

    public func readContent(_ device: DeviceSnapshot, _ entry: SyncEntry) async throws -> Data {
        try access.withAccess {
            let path = try blobPath(entry.hash)
            if !access.exists(path) {
                _ = access.requestDownload(path)
                throw SyncNotArrived("\(entry.path) from \(device.name) hasn't arrived yet.")
            }
            if let size = access.size(path), size > SyncLimits.maxFileBytes { throw SyncDataError("\(entry.path) from \(device.name) is too big to sync.") }
            return try access.read(path)
        }
    }

    public func publish(_ snapshot: DeviceSnapshot, read: (SyncEntry) throws -> Data, readVersion: String?) async throws -> String? {
        try access.withAccess {
            let now = clock()
            for entry in snapshot.entries {
                let path = try blobPath(entry.hash)
                if !access.exists(path) {
                    try access.write(try read(entry), to: path)
                } else if let modified = access.modified(path), modified < now.addingTimeInterval(-86_400) {
                    // Still in use: never "old" to a device cleaning up while it sees an older list of ours.
                    try? access.setModified(path, now)
                }
            }
            try access.write(snapshot.encoded(), to: devicesDir.appendingPathComponent(snapshot.device + ".json"))
            if now.timeIntervalSince(lastCollect) > 6 * 3600 {
                lastCollect = now
                try collect()
            }
            // Our own file doesn't change the version the other devices' files have.
            return readVersion
        }
    }

    /// Removes contents no device lists any more (only old ones: a device may be between writing contents and its list).
    public func collect(minimumAge: TimeInterval = 7 * 86_400) throws {
        let blobs = root.appendingPathComponent("blobs")
        var used = Set<String>()
        for name in try access.list(devicesDir) where name.hasSuffix(".json") && !name.hasPrefix(".") {
            // A device file that can't be read now: keep everything.
            guard let data = try? access.read(devicesDir.appendingPathComponent(name)), let snapshot = try? DeviceSnapshot.decode(data) else { return }
            used.formUnion(snapshot.entries.map(\.hash))
        }
        let cutoff = clock().addingTimeInterval(-minimumAge)
        for prefix in try access.list(blobs) where !prefix.hasPrefix(".") {
            let folder = blobs.appendingPathComponent(prefix)
            for name in (try? access.list(folder)) ?? [] where !name.hasPrefix(".") && !used.contains(name) {
                let file = folder.appendingPathComponent(name)
                if let modified = access.modified(file), modified < cutoff { try? access.remove(file) }
            }
        }
    }

    func blobPath(_ hash: String) throws -> URL {
        guard hash.utf16.count == 64, Ordinal.isLowerHex(hash) else { throw SyncDataError("Not a content hash: \(hash)") }
        return root.appendingPathComponent("blobs").appendingPathComponent(String(hash.prefix(2))).appendingPathComponent(hash)
    }
}
