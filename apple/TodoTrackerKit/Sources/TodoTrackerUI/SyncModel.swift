#if canImport(SwiftUI)
import Foundation
import SwiftUI
import TodoTrackerKit
#if os(iOS) && canImport(BackgroundTasks)
import BackgroundTasks
#endif

/// Settings › Sync on an iPhone or iPad: syncing on its own with the other devices (through iCloud Drive's
/// `Apps/TodoTrackerSync` or a GitHub gist), and when: at launch and back in front, a few seconds after a change,
/// every minute while open, and in the background when iOS allows. Each sync runs off the main thread; after one that
/// brought something, the board is read again from the tasks folder.
@MainActor
public final class SyncModel: ObservableObject {
    /// The place set up (nil: sync is off).
    @Published public private(set) var settings: SyncSettings?
    @Published public private(set) var status: SyncStatus = .empty
    @Published public private(set) var isSyncing = false
    /// The last sync didn't get everything (a device's changes not downloaded yet, still arriving): said on the screen.
    @Published public private(set) var waiting: String?
    /// Why the last sync didn't work (nil: it did).
    @Published public private(set) var problem: String?
    /// A folder picked without `TodoTrackerSync` in it: offered to make it there.
    @Published public var pendingFolder: PendingFolder?

    /// A picked folder waiting for "Create it here".
    public struct PendingFolder: Equatable, Identifiable {
        public let url: URL
        public let subpath: String
        public var id: String { url.path + "|" + subpath }
    }

    /// The background refresh iOS runs now and then (project.yml lists it in BGTaskSchedulerPermittedIdentifiers).
    public static let backgroundTaskId = "com.todotracker.ios.sync"

    private let syncer: DeviceSyncing
    private let store: SyncSettingsStore
    private let bookmarks: FolderBookmarks
    /// How long after a change it syncs (changes close together go in one sync).
    public var debounce: TimeInterval
    /// How often it looks for the other devices' changes while open.
    public var interval: TimeInterval
    /// After a sync that brought or sent something: the board is read again from the tasks folder.
    public var onSynced: ((SyncResult) -> Void)?
    /// While true (connected to a computer), nothing syncs.
    public var isPaused: () -> Bool = { false }

    private var pendingChange: Task<Void, Never>?
    private var timer: Timer?
    private var again = false
    private var running: Task<Void, Never>?

    public init(syncer: DeviceSyncing, settings: SyncSettingsStore, bookmarks: FolderBookmarks = SystemFolderBookmarks(), debounce: TimeInterval = 5, interval: TimeInterval = 60) {
        self.syncer = syncer
        self.store = settings
        self.bookmarks = bookmarks
        self.debounce = debounce
        self.interval = interval
        self.settings = settings.load()
        Self.current = self
        Task { await readStatus() }
    }

    public var isOn: Bool { settings != nil }

    /// Where it syncs, in words ("iCloud Drive › TodoTrackerSync", "A private GitHub gist").
    public var placeName: String? {
        guard let settings else { return nil }
        switch settings.kind {
        case .folder: return settings.folderName.isEmpty ? "A shared folder" : (settings.subpath.isEmpty ? settings.folderName : "\(settings.folderName) › \(settings.subpath.replacingOccurrences(of: "/", with: " › "))")
        case .gist: return "A private GitHub gist"
        }
    }

    // MARK: When it syncs

    /// The app started (or came back to the front): sync now, then every minute while it's open.
    public func start() {
        timer?.invalidate()
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            Task { @MainActor in self?.syncNow() }
        }
        syncNow()
    }

    /// The app went to the background: no minute timer; iOS wakes it now and then instead.
    public func stop() {
        timer?.invalidate()
        timer = nil
        scheduleBackground()
    }

    /// Something changed on this phone: synced a few seconds later (one sync for changes close together).
    public func changed() {
        guard isOn else { return }
        pendingChange?.cancel()
        let delay = debounce
        pendingChange = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            guard !Task.isCancelled else { return }
            self?.syncNow()
        }
    }

    /// Syncs now (another sync asked for meanwhile runs right after this one).
    public func syncNow() {
        guard isOn, !isPaused() else { return }
        if running != nil {
            again = true
            return
        }
        running = Task { [weak self] in
            await self?.runLoop()
        }
    }

    /// Waits for the sync running now (and any asked for meanwhile).
    public func finish() async {
        while let running { await running.value }
    }

    /// The app's sync (the background refresh reaches it here).
    public private(set) static weak var current: SyncModel?

    /// iOS woke the app for a background refresh.
    public static func backgroundRefresh() async { await current?.syncInBackground() }

    /// iOS woke the app in the background: one sync, then the next wake-up is asked for.
    public func syncInBackground() async {
        syncNow()
        await finish()
        scheduleBackground()
    }

    private func runLoop() async {
        repeat {
            again = false
            isSyncing = true
            do {
                if let result = try await syncer.run() {
                    problem = nil
                    waiting = result.complete ? nil : (result.problem ?? "Some changes from your other devices haven’t downloaded yet. They come in on a later sync.")
                    if !result.skipped { onSynced?(result) }
                }
            } catch is CancellationError {
            } catch {
                problem = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
            isSyncing = false
            await readStatus()
        } while again && isOn && !isPaused()
        running = nil
    }

    private func readStatus() async {
        status = isOn ? await syncer.status() : .empty
    }

    private func scheduleBackground() {
        #if os(iOS) && canImport(BackgroundTasks)
        guard isOn, Bundle.main.bundleURL.pathExtension == "app" else { return }
        let request = BGAppRefreshTaskRequest(identifier: Self.backgroundTaskId)
        request.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(request)
        #endif
    }

    // MARK: Setting it up

    /// The folder picked in the Files app: `TodoTrackerSync` (or a folder holding it) is used; else it's offered to
    /// make it there (`pendingFolder`).
    public func choose(folder url: URL, library: String) {
        let opened = url.startAccessingSecurityScopedResource()
        defer { if opened { url.stopAccessingSecurityScopedResource() } }
        let choice = SyncFolderChoice.choose(pickedName: url.lastPathComponent) { rel in
            var dir: ObjCBool = false
            return FileManager.default.fileExists(atPath: url.appendingPathComponent(rel).path, isDirectory: &dir) && dir.boolValue
        }
        switch choice {
        case .ready(let subpath): use(folder: url, subpath: subpath, library: library)
        case .missing(let subpath): pendingFolder = PendingFolder(url: url, subpath: subpath)
        }
    }

    /// "Create it here": makes `TodoTrackerSync` in the folder picked and syncs there.
    public func createPendingFolder(library: String) {
        guard let pending = pendingFolder else { return }
        pendingFolder = nil
        let opened = pending.url.startAccessingSecurityScopedResource()
        defer { if opened { pending.url.stopAccessingSecurityScopedResource() } }
        do {
            try FileManager.default.createDirectory(at: pending.url.appendingPathComponent(pending.subpath, isDirectory: true), withIntermediateDirectories: true)
        } catch {
            problem = "Couldn’t make the folder there: \(error.localizedDescription)"
            return
        }
        use(folder: pending.url, subpath: pending.subpath, library: library)
    }

    private func use(folder url: URL, subpath: String, library: String) {
        do {
            let bookmark = try bookmarks.make(url)
            begin(SyncSettings(kind: .folder, library: Self.libraryName(library), bookmark: bookmark, subpath: subpath, folderName: url.lastPathComponent))
        } catch {
            problem = "Couldn’t keep access to that folder: \(error.localizedDescription)"
        }
    }

    /// Syncs through a private gist with a pasted GitHub token (classic with the `gist` scope, or fine-grained with
    /// Gists: read and write). The token stays in the Keychain.
    public func useGist(token: String, library: String) {
        let clean = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else {
            problem = "Paste a GitHub token first."
            return
        }
        guard store.setToken(clean) else {
            problem = "Couldn’t keep the token on this phone (its Keychain said no). Try again."
            return
        }
        begin(SyncSettings(kind: .gist, library: Self.libraryName(library)))
    }

    /// Another library name (devices with the same one meet): the next sync joins it.
    public func setLibrary(_ name: String) {
        guard var settings else { return }
        let clean = Self.libraryName(name)
        guard clean != settings.library else { return }
        settings.library = clean
        settings.joined = false
        settings.id = UUID().uuidString
        begin(settings)
    }

    private func begin(_ settings: SyncSettings) {
        store.save(settings)
        self.settings = settings
        problem = nil
        waiting = nil
        status = .empty
        syncNow()
    }

    /// Sync off: this phone keeps its tasks as they are; the place and the token are forgotten here.
    public func turnOff() {
        pendingChange?.cancel()
        store.clear()
        settings = nil
        status = .empty
        problem = nil
        waiting = nil
    }

    /// Stops merging a device that's gone.
    public func forget(_ device: String) {
        Task {
            do {
                try await syncer.forget(device)
            } catch {
                problem = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription
            }
            await readStatus()
        }
    }

    static func libraryName(_ typed: String) -> String {
        let clean = SyncLibrary.clean(typed)
        return clean.isEmpty ? SyncLibrary.defaultName : clean
    }

    /// GitHub's page to make a token that can only use gists.
    public static let tokenPage = URL(string: "https://github.com/settings/tokens/new?scopes=gist&description=Todo%20Tracker")!
}
#endif
