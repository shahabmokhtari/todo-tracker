#if canImport(SwiftUI)
import Foundation
import SwiftUI
import OneThingKit
#if os(iOS) && canImport(BackgroundTasks)
import BackgroundTasks
#endif
#if os(iOS)
import UIKit
#endif

/// Settings › Sync on an iPhone or iPad: syncing on its own with the other devices through one of `SyncBackends` (a
/// shared folder in iCloud Drive or OneDrive, or a GitHub gist), and when: at launch and back in front, a few seconds
/// after a change, every minute while open, and in the background when iOS allows. Each sync runs off the main thread,
/// one at a time; after one that brought something, the board is read again from the tasks folder.
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
    /// A folder picked without `OneThingSync` in it: offered to make it there.
    @Published public var pendingFolder: PendingFolder?

    /// A picked folder waiting for "Create it here".
    public struct PendingFolder: Equatable, Identifiable {
        public let url: URL
        public let subpath: String
        /// The backend it was picked for.
        public var kind: SyncPlaceKind = .folder
        public var id: String { url.path + "|" + subpath }
    }

    /// The background refresh iOS runs now and then (project.yml lists it in BGTaskSchedulerPermittedIdentifiers).
    public static let backgroundTaskId = "com.onething.ios.sync"

    private let syncer: DeviceSyncing
    private let store: SyncSettingsStore
    private let bookmarks: FolderBookmarks
    /// How long after a change it syncs (changes close together go in one sync).
    public var debounce: TimeInterval
    /// How often it looks for the other devices' changes while open.
    public var interval: TimeInterval
    /// After a sync that brought or sent something, or one that failed part way: the board is read again from the
    /// tasks folder.
    public var onSynced: (() -> Void)?

    private var pendingChange: Task<Void, Never>?
    private var timer: Timer?
    private var again = false
    private var running: Task<Void, Never>?
    /// The last sync stopped (or the forgetting after sync was turned off): whatever runs next waits for it to end
    /// first, so two never overlap. Each run waits for the one stopped before it, so this one is enough.
    private var stopping: Task<Void, Never>?
    private var runId = 0
    #if os(iOS)
    /// The time iOS gives the app after it went to the background, for the sync on its way.
    private var backgroundTask: UIBackgroundTaskIdentifier = .invalid
    #endif

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

    /// The backend set up (nil: sync is off).
    public var backend: SyncBackend? { settings.flatMap { SyncBackends.backend($0.kind) } }

    /// Where it syncs, in words ("iCloud Drive › Apps › OneThingSync", "A private GitHub gist").
    public var placeName: String? {
        guard let settings, let backend else { return nil }
        return backend.describe(settings)
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

    /// The app went to the background: no minute timer; iOS wakes it now and then instead. A change still waiting its
    /// few seconds (or a sync on its way) syncs now, in the little time iOS gives the app.
    public func stop() {
        timer?.invalidate()
        timer = nil
        if pendingChange != nil || running != nil {
            pendingChange?.cancel()
            pendingChange = nil
            syncNow()
            finishInBackground()
        }
        scheduleBackground()
    }

    /// Asks iOS for time to finish the sync; when time's up, it stops (what it hadn't published waits for the next one).
    private func finishInBackground() {
        #if os(iOS)
        guard backgroundTask == .invalid else { return }
        backgroundTask = UIApplication.shared.beginBackgroundTask(withName: "Sync") { [weak self] in
            MainActor.assumeIsolated {
                self?.cancelRunning()
                self?.endBackgroundTask()
            }
        }
        Task { [weak self] in
            await self?.finish()
            self?.endBackgroundTask()
        }
        #endif
    }

    #if os(iOS)
    private func endBackgroundTask() {
        guard backgroundTask != .invalid else { return }
        UIApplication.shared.endBackgroundTask(backgroundTask)
        backgroundTask = .invalid
    }
    #endif

    /// Something changed on this phone: synced a few seconds later (one sync for changes close together).
    public func changed() {
        guard isOn else { return }
        pendingChange?.cancel()
        let delay = debounce
        pendingChange = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            guard !Task.isCancelled else { return }
            self?.pendingChange = nil
            self?.syncNow()
        }
    }

    /// Syncs now (another sync asked for meanwhile runs right after this one).
    public func syncNow() {
        guard isOn else { return }
        if running != nil {
            again = true
            return
        }
        runId += 1
        let id = runId
        let before = stopping
        running = Task { [weak self] in
            // A sync stopped a moment ago may still be finishing: this one starts after it.
            await before?.value
            await self?.runLoop(id)
        }
    }

    /// Stops the sync running now and the one waiting after a change (sync turned off, or another place chosen):
    /// whatever it hadn't published yet isn't. The next sync (or forgetting) waits until it has really ended.
    public func cancel() {
        pendingChange?.cancel()
        pendingChange = nil
        cancelRunning()
    }

    /// The app's sync (the background refresh reaches it here).
    public private(set) static weak var current: SyncModel?

    /// iOS woke the app for a background refresh: the next one is asked for first (even before the app's sync is
    /// there), then one sync.
    public static func backgroundRefresh() async {
        submitBackground()
        await current?.syncInBackground()
    }

    /// iOS woke the app in the background: one sync, the next wake-up asked for before and after. When iOS says time's
    /// up, the sync stops (whatever it hadn't published waits for the next one).
    public func syncInBackground() async {
        scheduleBackground()
        await withTaskCancellationHandler {
            syncNow()
            await finish()
        } onCancel: {
            Task { @MainActor [weak self] in self?.cancelRunning() }
        }
        scheduleBackground()
    }

    private func cancelRunning() {
        if let running {
            running.cancel()
            // Not over yet: it ends at its next check. What comes next waits for it (it waited for the one before).
            stopping = running
        }
        running = nil
        again = false
        isSyncing = false
    }

    /// Waits for the sync running now (and any asked for meanwhile), and for one stopped before it to end.
    public func finish() async {
        while let running { await running.value }
        await stopping?.value
    }

    private func runLoop(_ id: Int) async {
        repeat {
            // Stopped (or another place chosen) before it began: nothing to say, nothing to run.
            guard id == runId, !Task.isCancelled else { return }
            again = false
            isSyncing = true
            do {
                if let result = try await syncer.run() {
                    if id == runId {
                        problem = nil
                        waiting = result.complete ? nil : (result.problem ?? "Some changes from your other devices haven’t downloaded yet. They come in on a later sync.")
                    }
                    if !result.skipped { onSynced?() }
                }
            } catch is CancellationError {
            } catch {
                if id == runId { problem = (error as? LocalizedError)?.errorDescription ?? error.localizedDescription }
                // Part of it may have come in before it failed.
                onSynced?()
            }
            guard id == runId, !Task.isCancelled else { return }
            isSyncing = false
            await readStatus()
        } while again && isOn && !Task.isCancelled
        if id == runId { running = nil }
    }

    private func readStatus() async {
        status = isOn ? await syncer.status() : .empty
    }

    private func scheduleBackground() {
        guard isOn else { return }
        Self.submitBackground()
    }

    private static func submitBackground() {
        #if os(iOS) && canImport(BackgroundTasks)
        guard Bundle.main.bundleURL.pathExtension == "app" else { return }
        let request = BGAppRefreshTaskRequest(identifier: Self.backgroundTaskId)
        request.earliestBeginDate = Date(timeIntervalSinceNow: 15 * 60)
        try? BGTaskScheduler.shared.submit(request)
        #endif
    }

    // MARK: Setting it up

    /// The folder picked in the Files app for a backend that syncs through a folder: `OneThingSync` (or a folder
    /// holding it) is used; else it's offered to make it there (`pendingFolder`).
    public func choose(folder url: URL, library: String, backend: SyncBackend = SyncBackends.folder) {
        let opened = url.startAccessingSecurityScopedResource()
        defer { if opened { url.stopAccessingSecurityScopedResource() } }
        let choice = SyncFolderChoice.choose(pickedName: url.lastPathComponent) { rel in
            var dir: ObjCBool = false
            return FileManager.default.fileExists(atPath: url.appendingPathComponent(rel).path, isDirectory: &dir) && dir.boolValue
        }
        switch choice {
        case .ready(let subpath): use(folder: url, subpath: subpath, library: library, kind: backend.kind)
        case .missing(let subpath): pendingFolder = PendingFolder(url: url, subpath: subpath, kind: backend.kind)
        }
    }

    /// "Create it here": makes `OneThingSync` in the folder picked and syncs there.
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
        use(folder: pending.url, subpath: pending.subpath, library: library, kind: pending.kind)
    }

    private func use(folder url: URL, subpath: String, library: String, kind: SyncPlaceKind) {
        do {
            let bookmark = try bookmarks.make(url)
            begin(SyncSettings(kind: kind, library: Self.libraryName(library), bookmark: bookmark, subpath: subpath, folderName: url.lastPathComponent))
        } catch {
            problem = "Couldn’t keep access to that folder: \(error.localizedDescription)"
        }
    }

    /// Syncs through a backend set up with a pasted token (kept in the Keychain).
    public func use(_ backend: SyncBackend, token: String, library: String) {
        let clean = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else {
            problem = "Paste a token first."
            return
        }
        guard store.setToken(clean) else {
            problem = "Couldn’t keep the token on this phone (its Keychain said no). Try again."
            return
        }
        begin(SyncSettings(kind: backend.kind, library: Self.libraryName(library)))
    }

    /// Syncs through a private gist with a pasted GitHub token (classic with the `gist` scope, or fine-grained with
    /// Gists: read and write).
    public func useGist(token: String, library: String) {
        use(SyncBackends.gist, token: token, library: library)
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
        // A new place: the sync running for the old one stops, what it remembered goes (this one is joined, pulling
        // first), and a token isn't kept for a backend that doesn't use one.
        cancel()
        if SyncBackends.backend(settings.kind)?.usesToken != true { store.removeToken() }
        store.save(settings)
        self.settings = settings
        problem = nil
        waiting = nil
        status = .empty
        // Once the old sync has ended, what it remembered about the old place goes; then the first sync joins this one.
        runId += 1
        let id = runId
        let syncer = self.syncer
        let before = stopping
        running = Task { [weak self] in
            await before?.value
            await syncer.reset()
            await self?.runLoop(id)
        }
    }

    /// Sync off: this phone keeps its tasks as they are; the place and the token are forgotten here.
    public func turnOff() {
        cancel()
        let syncer = self.syncer
        let before = stopping
        // Forgotten once the sync stopped has ended (it could write what it remembered after); a sync turned on again
        // meanwhile waits for this.
        stopping = Task {
            await before?.value
            await syncer.reset()
        }
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
    public static let tokenPage = SyncBackends.gistTokenPage
}
#endif
