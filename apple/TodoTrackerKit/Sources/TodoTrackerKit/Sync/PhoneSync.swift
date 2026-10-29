import Foundation

/// Where a phone syncs on its own: a shared folder it was given (iCloud Drive's `Apps/TodoTrackerSync`), or a private
/// GitHub gist.
public enum SyncPlaceKind: String, Sendable {
    case folder
    case gist
}

/// How this phone syncs on its own (nil when it doesn't). Kept in UserDefaults; the gist token is in the Keychain.
public struct SyncSettings: Equatable, Sendable {
    public var kind: SyncPlaceKind
    /// Which set of tasks (a library): devices with the same name meet. "Todo Tracker" unless the person changed it.
    public var library: String
    /// The folder the person picked (a bookmark of it, so the app may open it again after a restart).
    public var bookmark: Data?
    /// From the picked folder to `TodoTrackerSync` ("" when the picked folder is that one).
    public var subpath: String
    /// The picked folder's name, to show.
    public var folderName: String
    /// The first sync (everything from the other devices in before anything of this phone goes out) is done.
    public var joined: Bool
    /// Made new each time a place is chosen (choosing again is a new place, even the same folder).
    public var id: String

    public init(kind: SyncPlaceKind, library: String = SyncLibrary.defaultName, bookmark: Data? = nil, subpath: String = "", folderName: String = "", joined: Bool = false, id: String = UUID().uuidString) {
        self.id = id
        self.kind = kind
        self.library = library
        self.bookmark = bookmark
        self.subpath = subpath
        self.folderName = folderName
        self.joined = joined
    }

    /// Which place and library this is: another one starts a new engine (and a first sync there).
    public var key: String { "\(kind.rawValue)|\(library)|\(id)" }
}

/// Library names, as the computers clean them (letters, digits, spaces, `-` and `_`; at most 60).
public enum SyncLibrary {
    public static let defaultName = "Todo Tracker"

    public static func clean(_ name: String) -> String {
        let kept = String(String.UnicodeScalarView(name.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) || $0 == " " || $0 == "-" || $0 == "_" }))
        let trimmed = kept.trimmingCharacters(in: .whitespaces)
        return trimmed.count > 60 ? String(trimmed.prefix(60)).trimmingCharacters(in: .whitespaces) : trimmed
    }
}

/// Keeps the sync settings (UserDefaults) and the gist token (the Keychain).
public final class SyncSettingsStore: @unchecked Sendable {
    private let defaults: UserDefaults
    private let secrets: SecretStore
    static let settingsKey = "phoneSync"
    static let tokenKey = "gist-token"
    static let gistKey = "phoneSyncGist"

    public init(defaults: UserDefaults = .standard, secrets: SecretStore) {
        self.defaults = defaults
        self.secrets = secrets
    }

    public func load() -> SyncSettings? {
        guard let saved = defaults.dictionary(forKey: Self.settingsKey),
              let kind = (saved["kind"] as? String).flatMap(SyncPlaceKind.init(rawValue:)) else { return nil }
        let library = (saved["library"] as? String).map(SyncLibrary.clean) ?? ""
        return SyncSettings(kind: kind, library: library.isEmpty ? SyncLibrary.defaultName : library, bookmark: saved["bookmark"] as? Data,
                            subpath: saved["subpath"] as? String ?? "", folderName: saved["folderName"] as? String ?? "", joined: saved["joined"] as? Bool ?? false,
                            id: saved["id"] as? String ?? "")
    }

    public func save(_ settings: SyncSettings) {
        var saved: [String: Any] = ["kind": settings.kind.rawValue, "library": settings.library, "subpath": settings.subpath,
                                    "folderName": settings.folderName, "joined": settings.joined, "id": settings.id]
        if let bookmark = settings.bookmark { saved["bookmark"] = bookmark }
        defaults.set(saved, forKey: Self.settingsKey)
    }

    /// Sync off: the place and the token are forgotten (the tasks on this phone stay as they are).
    public func clear() {
        defaults.removeObject(forKey: Self.settingsKey)
        secrets.remove(Self.tokenKey)
    }

    public var token: String? { secrets.read(Self.tokenKey).flatMap { $0.isEmpty ? nil : $0 } }

    /// No gist any more (a folder is used): the token isn't kept.
    public func removeToken() { secrets.remove(Self.tokenKey) }

    @discardableResult
    public func setToken(_ token: String) -> Bool { secrets.write(Self.tokenKey, token) }

    /// The gist a library uses (found or made on the first sync).
    public func gistId(_ library: String) -> String? { defaults.string(forKey: Self.gistKey + "-" + Self.hash(library)) }

    public func setGistId(_ id: String, _ library: String) { defaults.set(id, forKey: Self.gistKey + "-" + Self.hash(library)) }

    static func hash(_ text: String) -> String { String(SyncHash.of(text).prefix(16)) }
}

/// Bookmarks of the folder the person picked (so the app can open it again after a restart).
public protocol FolderBookmarks: AnyObject, Sendable {
    func make(_ url: URL) throws -> Data
    /// The folder, and whether the bookmark should be made again (it moved, or the system says it's stale).
    func resolve(_ bookmark: Data) throws -> (url: URL, stale: Bool)
}

/// The system's bookmarks. On iOS a folder from the document picker keeps its security scope in a plain bookmark.
public final class SystemFolderBookmarks: FolderBookmarks, @unchecked Sendable {
    public init() {}

    public func make(_ url: URL) throws -> Data {
        let opened = url.startAccessingSecurityScopedResource()
        defer { if opened { url.stopAccessingSecurityScopedResource() } }
        return try url.bookmarkData(options: [], includingResourceValuesForKeys: nil, relativeTo: nil)
    }

    public func resolve(_ bookmark: Data) throws -> (url: URL, stale: Bool) {
        var stale = false
        let url = try URL(resolvingBookmarkData: bookmark, options: [], relativeTo: nil, bookmarkDataIsStale: &stale)
        return (url, stale)
    }
}

/// What to do with the folder the person picked: computers meet in `Apps/TodoTrackerSync` in iCloud Drive.
public enum SyncFolderChoice: Equatable, Sendable {
    /// `TodoTrackerSync` is there (`subpath` from the picked folder).
    case ready(subpath: String)
    /// It isn't: it can be made here (`subpath` from the picked folder).
    case missing(subpath: String)

    public static let folder = "TodoTrackerSync"

    /// `exists`: whether a folder (a path relative to the picked one) is there.
    public static func choose(pickedName: String, exists: (String) -> Bool) -> SyncFolderChoice {
        if Ordinal.equalIgnoreCase(pickedName, folder) { return .ready(subpath: "") }
        if exists(folder) { return .ready(subpath: folder) }
        if exists("Apps/" + folder) { return .ready(subpath: "Apps/" + folder) }
        return Ordinal.equalIgnoreCase(pickedName, "Apps") ? .missing(subpath: folder) : .missing(subpath: "Apps/" + folder)
    }
}

/// Sync isn't set up as it should be (no token, a folder that can't be opened): said in words the person reads.
public struct SyncSetupError: Error, LocalizedError {
    public let message: String
    public init(_ message: String) { self.message = message }
    public var errorDescription: String? { message }
}

/// Runs this phone's sync (what the Sync screen and the schedule call). An actor: one sync at a time, off the main
/// thread.
public protocol DeviceSyncing: AnyObject, Sendable {
    /// Syncs once with the place set up (the first time, joining it). Nil when sync is off.
    func run() async throws -> SyncResult?
    func status() async -> SyncStatus
    func forget(_ device: String) async throws
    /// Sync is off (or another place was chosen): what it remembered about the places goes (the next one is joined
    /// from scratch, pulling first).
    func reset() async
}

/// The phone syncing on its own: builds the engine for the place in the settings (again when they change) over the
/// app's one tasks folder, and the first time joins (everything comes in before anything goes out).
public actor PhoneSyncer: DeviceSyncing {
    public let store: VaultStore
    private let directory: URL
    private let settings: SyncSettingsStore
    private let bookmarks: FolderBookmarks
    private let http: HTTPTransport
    private let deviceName: String
    private let legacyBoard: URL?
    private let access: (URL) -> SyncFolderAccess
    private let clock: () -> Date
    private var current: (key: String, engine: SyncEngine)?

    /// - Parameters:
    ///   - directory: the app's private sync folder (what sync remembers; never in the tasks folder or the cloud).
    ///   - legacyBoard: the phone's `board.json` from before the tasks folder, brought in beside what came on joining.
    ///   - access: how the picked folder is reached (inside its security scope, through a file coordinator).
    public init(store: VaultStore, directory: URL, settings: SyncSettingsStore, bookmarks: FolderBookmarks = SystemFolderBookmarks(),
                http: HTTPTransport = GitHubSession.shared, deviceName: String, legacyBoard: URL? = nil,
                access: @escaping (URL) -> SyncFolderAccess = { CoordinatedFolderAccess(securityScoped: $0) }, clock: @escaping () -> Date = Date.init) {
        self.store = store
        self.directory = directory
        self.settings = settings
        self.bookmarks = bookmarks
        self.http = http
        self.deviceName = deviceName
        self.legacyBoard = legacyBoard
        self.access = access
        self.clock = clock
    }

    public func run() async throws -> SyncResult? {
        guard let setup = settings.load() else {
            current = nil
            return nil
        }
        let engine = try engine(for: setup)
        let marker = Self.stateDirectory(directory, setup).appendingPathComponent("joined")
        // Joined is remembered next to what sync remembers (never in a backup): a device restored from one joins again.
        if FileManager.default.fileExists(atPath: marker.path) { return try await engine.sync() }
        let legacy = legacyBoard.flatMap { FileManager.default.fileExists(atPath: $0.path) ? $0 : nil }
        let result = try await engine.join(importing: legacy)
        // Joined once everything came in (else it's tried again next time, still pulling first). Not if sync was turned
        // off or moved meanwhile.
        try Task.checkCancellation()
        if result.complete || result.published, var now = settings.load(), now.key == setup.key {
            try Data().write(to: marker)
            now.joined = true
            settings.save(now)
        }
        return result
    }

    public func reset() async {
        current = nil
        let fm = FileManager.default
        for name in (try? fm.contentsOfDirectory(atPath: directory.path)) ?? [] where name != "device.json" {
            try? fm.removeItem(at: directory.appendingPathComponent(name))
        }
    }

    /// Whether this device has joined the place set up (false after a restore from a backup, or a new place).
    public static func hasJoined(directory: URL, settings: SyncSettings) -> Bool {
        FileManager.default.fileExists(atPath: stateDirectory(directory, settings).appendingPathComponent("joined").path)
    }

    /// What sync remembers about one place and library (another one never sees this one's deletes or bases).
    static func stateDirectory(_ directory: URL, _ setup: SyncSettings) -> URL {
        directory.appendingPathComponent("\(setup.kind.rawValue)-\(SyncSettingsStore.hash(setup.key))", isDirectory: true)
    }

    /// The device id and what it agreed on stay on this device: restored onto another iPhone, two devices would
    /// publish as one.
    private func excludeFromBackup() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        var url = directory
        try url.setResourceValues(values)
    }

    public func status() async -> SyncStatus {
        guard let setup = settings.load(), let engine = try? engine(for: setup) else { return .empty }
        return await engine.status
    }

    public func forget(_ device: String) async throws {
        guard let setup = settings.load() else { return }
        try await engine(for: setup).forget(device)
    }

    private func engine(for setup: SyncSettings) throws -> SyncEngine {
        if let current, current.key == setup.key { return current.engine }
        let remote: SyncRemote
        switch setup.kind {
        case .folder:
            guard let bookmark = setup.bookmark else { throw SyncSetupError("Pick the TodoTrackerSync folder in iCloud Drive again.") }
            let resolved: (url: URL, stale: Bool)
            do {
                resolved = try bookmarks.resolve(bookmark)
            } catch {
                throw SyncSetupError("The sync folder can't be opened any more. Pick it again in Settings › Sync.")
            }
            if resolved.stale, let fresh = try? bookmarks.make(resolved.url), var now = settings.load(), now.key == setup.key {
                now.bookmark = fresh
                settings.save(now)
            }
            var root = resolved.url
            for part in setup.subpath.split(separator: "/") { root.appendPathComponent(String(part), isDirectory: true) }
            root.appendPathComponent(setup.library, isDirectory: true)
            remote = FolderRemote(root: root, access: access(resolved.url), clock: clock)
        case .gist:
            let settings = self.settings
            let library = setup.library
            remote = GistRemote(http: http, token: {
                guard let token = settings.token else { throw SyncSetupError("Paste a GitHub token (with the gist scope) in Settings › Sync.") }
                return token
            }, loadId: { settings.gistId(library) }, saveId: { settings.setGistId($0, library) }, library: library)
        }
        try excludeFromBackup()
        let state = try SyncState(directory: Self.stateDirectory(directory, setup), deviceDirectory: directory, deviceName: deviceName)
        let engine = SyncEngine(store: store, remote: remote, state: state, clock: clock)
        current = (setup.key, engine)
        return engine
    }
}
