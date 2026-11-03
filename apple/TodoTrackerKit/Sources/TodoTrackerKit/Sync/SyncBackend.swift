import Foundation

/// How a sync backend is set up on the Sync screen.
public enum SyncSetupStep: Equatable, Sendable {
    /// Pick a folder in the Files app (`SyncModel.choose(folder:library:)`); the app keeps a bookmark of it.
    case pickFolder
    /// Paste a token made on `page` (`SyncModel.use(_:token:library:)`); it's kept in the Keychain.
    case token(page: URL)
}

/// What a backend may use to reach its place: the settings chosen, where they (and the token) are kept, and the
/// device's ways out (folder bookmarks and access, the network, the clock).
public struct SyncBackendContext {
    public let settings: SyncSettings
    public let store: SyncSettingsStore
    public let bookmarks: FolderBookmarks
    public let http: HTTPTransport
    public let access: (URL) -> SyncFolderAccess
    public let clock: () -> Date

    public init(settings: SyncSettings, store: SyncSettingsStore, bookmarks: FolderBookmarks, http: HTTPTransport,
                access: @escaping (URL) -> SyncFolderAccess, clock: @escaping () -> Date) {
        self.settings = settings
        self.store = store
        self.bookmarks = bookmarks
        self.http = http
        self.access = access
        self.clock = clock
    }
}

/// A place devices meet to sync (a shared folder, a GitHub gist…), described once: its id in the settings, what the
/// Sync screen says about it, how it's set up, and how its `SyncRemote` is made. Another backend is one more
/// `SyncBackend` in `SyncBackends.all`: the phone's syncer, the sync model and the Sync screen only go through these.
public struct SyncBackend: Identifiable {
    public let kind: SyncPlaceKind
    public var id: String { kind.rawValue }
    /// Its name on the Sync screen ("GitHub gist").
    public let name: String
    /// An SF Symbol for it.
    public let symbol: String
    public let setup: SyncSetupStep
    /// The button that sets it up ("Sync through a gist").
    public let action: String
    /// What to do, under its section on the Sync screen. Given what the device is ("iPhone", "iPad").
    public let help: (_ device: String) -> String
    /// Where it syncs, in words, for the settings chosen.
    public let describe: (SyncSettings) -> String
    /// The remote for the settings chosen (a `SyncSetupError` when something it needs is missing).
    public let makeRemote: (SyncBackendContext) throws -> SyncRemote

    public init(kind: SyncPlaceKind, name: String, symbol: String, setup: SyncSetupStep, action: String, help: @escaping (_ device: String) -> String,
                describe: @escaping (SyncSettings) -> String, makeRemote: @escaping (SyncBackendContext) throws -> SyncRemote) {
        self.kind = kind
        self.name = name
        self.symbol = symbol
        self.setup = setup
        self.action = action
        self.help = help
        self.describe = describe
        self.makeRemote = makeRemote
    }

    /// Whether it keeps a token (choosing one that doesn't forgets the token kept).
    public var usesToken: Bool {
        if case .token = setup { return true }
        return false
    }
}

/// The sync backends this app offers, in the order the Sync screen shows them.
public enum SyncBackends {
    /// A shared folder picked in the Files app: iCloud Drive's or OneDrive's `Apps/TodoTrackerSync`, the folder the
    /// computers use.
    public static let folder = SyncBackend(
        kind: .folder, name: "iCloud Drive or OneDrive", symbol: "icloud", setup: .pickFolder, action: "Choose the folder…",
        help: { _ in "Pick Apps › TodoTrackerSync in iCloud Drive (or OneDrive), the folder your computers use. If it isn’t there yet, pick iCloud Drive and choose Create it here." },
        describe: { settings in
            if settings.folderName.isEmpty { return "A shared folder" }
            return settings.subpath.isEmpty ? settings.folderName : "\(settings.folderName) › \(settings.subpath.replacingOccurrences(of: "/", with: " › "))"
        },
        makeRemote: { context in
            let setup = context.settings
            guard let bookmark = setup.bookmark else { throw SyncSetupError("Pick the TodoTrackerSync folder again in Settings › Sync.") }
            let resolved: (url: URL, stale: Bool)
            do {
                resolved = try context.bookmarks.resolve(bookmark)
            } catch {
                throw SyncSetupError("The sync folder can't be opened any more. Pick it again in Settings › Sync.")
            }
            if resolved.stale, let fresh = try? context.bookmarks.make(resolved.url), var now = context.store.load(), now.key == setup.key {
                now.bookmark = fresh
                context.store.save(now)
            }
            var root = resolved.url
            for part in setup.subpath.split(separator: "/") { root.appendPathComponent(String(part), isDirectory: true) }
            root.appendPathComponent(setup.library, isDirectory: true)
            return FolderRemote(root: root, access: context.access(resolved.url), clock: context.clock)
        })

    /// GitHub's page to make a token that can only use gists.
    public static let gistTokenPage = URL(string: "https://github.com/settings/tokens/new?scopes=gist&description=Todo%20Tracker")!

    /// A private GitHub gist, with a pasted token (classic with the `gist` scope, or fine-grained with Gists: read and
    /// write).
    public static let gist = SyncBackend(
        kind: .gist, name: "GitHub gist", symbol: "chevron.left.forwardslash.chevron.right", setup: .token(page: gistTokenPage), action: "Sync through a gist",
        help: { device in "Make a token that can only use gists (the gist scope), copy it and paste it here. It stays in this \(device)’s Keychain. On your computers, turn on GitHub gist sync too." },
        describe: { _ in "A private GitHub gist" },
        makeRemote: { context in
            let store = context.store
            let library = context.settings.library
            return GistRemote(http: context.http, token: {
                guard let token = store.token else { throw SyncSetupError("Paste a GitHub token (with the gist scope) in Settings › Sync.") }
                return token
            }, loadId: { store.gistId(library) }, saveId: { store.setGistId($0, library) }, library: library)
        })

    public static let all: [SyncBackend] = [folder, gist]

    /// The backend kept in the settings (nil: one this version doesn't know).
    public static func backend(_ kind: SyncPlaceKind) -> SyncBackend? { all.first { $0.kind == kind } }
}
