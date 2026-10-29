import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// Stands in for the phone's sync: counts the syncs and can do something to the tasks folder (what another device
/// brought).
final class FakeSyncer: DeviceSyncing, @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    private var publishes = 0
    private var resetCount = 0
    var delay: UInt64 = 0
    /// Done during a sync (say, a task arriving from another device).
    var during: (() throws -> Void)?
    /// The sync fails after `during` (part of it came in).
    var failure: Error?
    var result = SyncResult(changes: 1, peers: 1, conflicts: [], published: true, skipped: false)

    var runs: Int { lock.withLock { count } }
    /// Syncs that got as far as publishing (as the engine does, not once cancelled).
    var published: Int { lock.withLock { publishes } }
    var resets: Int { lock.withLock { resetCount } }

    func run() async throws -> SyncResult? {
        lock.withLock { count += 1 }
        if delay > 0 { try? await Task.sleep(nanoseconds: delay) }
        try during?()
        if let failure { throw failure }
        try Task.checkCancellation()
        lock.withLock { publishes += 1 }
        return result
    }

    func reset() async { lock.withLock { resetCount += 1 } }

    func status() async -> SyncStatus {
        var status = SyncStatus()
        status.lastSync = runs > 0 ? Instant(Date()) : nil
        status.devices = [SyncDevice(device: "laptop1", name: "Laptop", at: Instant(Date()))]
        return status
    }

    func forget(_ device: String) async throws {}
}

/// Bookmarks that are just the folder's path (a test can't make security-scoped ones).
final class PathBookmarks: FolderBookmarks, @unchecked Sendable {
    func make(_ url: URL) throws -> Data { Data(url.path.utf8) }
    func resolve(_ bookmark: Data) throws -> (url: URL, stale: Bool) { (URL(fileURLWithPath: String(decoding: bookmark, as: UTF8.self)), false) }
}

/// iPhone and iPad syncing on their own: the tasks folder as the phone's store (moved in from board.json once), when it
/// syncs, reading the folder again after a sync, and the three ways a phone works (a computer's tasks, syncing on its
/// own, or only on this device) never losing a task when switching.
@MainActor
final class SyncModelTests: XCTestCase {
    private var folder: URL!
    private var suite: String!
    private var secrets: InMemorySecretStore!

    override func setUp() async throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        suite = "SyncModelTests-\(UUID().uuidString)"
        secrets = InMemorySecretStore()
        FakeComputer.mode = .up
        FakeComputer.requests = []
        let computer = TaskBoard()
        _ = try computer.addTask(NewTask("On the computer"), now: Date())
        FakeComputer.board = try BoardCodec.encode(computer)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: folder)
        UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite)
    }

    private var defaults: UserDefaults { UserDefaults(suiteName: suite)! }

    private func waitUntil(_ what: String = "", _ condition: () -> Bool) async throws {
        for _ in 0..<250 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("Timed out \(what)")
    }

    /// The board.json an earlier version of the app kept.
    private func oldBoard(_ titles: [String]) throws {
        let board = TaskBoard()
        for title in titles { _ = try board.addTask(NewTask(title), now: Date()) }
        try BoardFileStore(url: folder.appendingPathComponent("board.json")).save(board)
    }

    private func syncModel(_ syncer: DeviceSyncing, on: Bool = true) -> SyncModel {
        let settings = SyncSettingsStore(defaults: defaults, secrets: secrets)
        if on { settings.save(SyncSettings(kind: .gist, joined: true)) }
        return SyncModel(syncer: syncer, settings: settings, bookmarks: PathBookmarks(), debounce: 0.1, interval: 3600)
    }

    private func phone() -> PhoneSetup {
        PhoneSetup(pairings: PairingStore(defaults: defaults, secrets: secrets), cacheURL: folder.appendingPathComponent("computer-board.json"), phoneName: "Test iPhone") { _ in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [FakeComputer.self]
            return URLSession(configuration: configuration)
        }
    }

    // MARK: The tasks folder

    func testTheOldBoardMovesIntoTheTasksFolderOnceAndIsKept() throws {
        try oldBoard(["Plan the trip", "Call the bank"])
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        XCTAssertEqual(try store.load().items.map(\.title).sorted(), ["Call the bank", "Plan the trip"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("board.json.migrated").path), "set aside, never deleted")
        XCTAssertFalse(FileManager.default.fileExists(atPath: folder.appendingPathComponent("board.json").path))
        let files = try FileManager.default.subpathsOfDirectory(atPath: folder.appendingPathComponent("vault").path).filter { $0.hasSuffix(".md") }
        XCTAssertEqual(files.count, 2, "one file per task: \(files)")

        // Opened again (the next launch): nothing comes in twice.
        let again = try VaultBoardStore.open(base: folder, importLeftover: true)
        XCTAssertEqual(try again.load().items.count, 2)
    }

    func testABoardLeftNextToAFullTasksFolderComesInBesideIt() throws {
        _ = try VaultBoardStore.open(base: folder, importLeftover: true).apply(TaskBoard()) { try $0.addTask(NewTask("Already here"), now: Date()) }
        try oldBoard(["From before"])

        // Joining a sync first: it waits (brought in after what the other devices have).
        _ = try VaultBoardStore.open(base: folder, importLeftover: false)
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("board.json").path))

        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        XCTAssertEqual(try store.load().items.map(\.title).sorted(), ["Already here", "From before"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: folder.appendingPathComponent("board.json.migrated").path))
    }

    func testAChangeIsSavedInTheTasksFolderAndSyncedAFewSecondsLater() async throws {
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        let syncer = FakeSyncer()
        let model = BoardModel(store: store, sync: syncModel(syncer))
        model.quickText = "Plan the trip"
        model.capture()
        model.quickText = "Call the bank"
        model.capture()
        XCTAssertEqual(try store.load().items.map(\.title).sorted(), ["Call the bank", "Plan the trip"], "saved in the folder at once")
        XCTAssertEqual(syncer.runs, 0, "not yet")

        try await waitUntil("the sync") { syncer.runs == 1 }
        try await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(syncer.runs, 1, "one sync for changes close together")

        // A task the model shows is a copy: changing it goes through the folder.
        let task = try XCTUnwrap(model.board.items.first { $0.title == "Plan the trip" })
        model.complete(task.id)
        XCTAssertEqual(try store.load().find(task.id)?.isDone, true)
    }

    func testWhatASyncBringsIsShownAfterIt() async throws {
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        let syncer = FakeSyncer()
        syncer.during = { _ = try store.vault.update { try $0.addTask(NewTask("From the laptop"), now: Date()) } }
        let sync = syncModel(syncer)
        let model = BoardModel(store: store, sync: sync)
        sync.syncNow()
        await sync.finish()
        XCTAssertEqual(model.board.items.map(\.title), ["From the laptop"])
        XCTAssertEqual(sync.status.devices.map(\.name), ["Laptop"])
        XCTAssertNotNil(sync.status.lastSync)
    }

    func testASyncAskedForWhileOneRunsRunsOnceAfterIt() async throws {
        let syncer = FakeSyncer()
        syncer.delay = 100_000_000
        let sync = syncModel(syncer)
        sync.syncNow()
        try await waitUntil("the first sync") { syncer.runs == 1 }
        sync.syncNow()
        sync.syncNow()
        await sync.finish()
        XCTAssertEqual(syncer.runs, 2, "the ones asked for meanwhile: one more")
    }

    func testNotEverythingDownloadedIsSaid() async throws {
        let syncer = FakeSyncer()
        syncer.result.complete = false
        let sync = syncModel(syncer)
        sync.syncNow()
        await sync.finish()
        XCTAssertEqual(sync.waiting, "Some changes from your other devices haven’t downloaded yet. They come in on a later sync.")
    }

    // MARK: Setting it up

    func testAGistIsSetUpWithAPastedTokenKeptAsASecret() async throws {
        let syncer = FakeSyncer()
        let sync = syncModel(syncer, on: false)
        XCTAssertFalse(sync.isOn)
        sync.syncNow()
        XCTAssertEqual(syncer.runs, 0, "off: nothing syncs")

        sync.useGist(token: "  ghp_pasted \n", library: "  ")
        XCTAssertEqual(secrets.read("gist-token"), "ghp_pasted")
        XCTAssertEqual(sync.settings?.kind, .gist)
        XCTAssertEqual(sync.settings?.library, "Todo Tracker")
        XCTAssertEqual(sync.settings?.joined, false, "the first sync joins")
        await sync.finish()
        XCTAssertEqual(syncer.runs, 1)
        XCTAssertEqual(SyncModel.tokenPage.absoluteString, "https://github.com/settings/tokens/new?scopes=gist&description=Todo%20Tracker")
    }

    func testAFolderWithoutTodoTrackerSyncIsOfferedToBeMadeThere() async throws {
        let syncer = FakeSyncer()
        let sync = syncModel(syncer, on: false)
        let drive = folder.appendingPathComponent("iCloud Drive", isDirectory: true)
        try FileManager.default.createDirectory(at: drive, withIntermediateDirectories: true)
        sync.choose(folder: drive, library: "Todo Tracker")
        XCTAssertEqual(sync.pendingFolder?.subpath, "Apps/TodoTrackerSync")
        XCTAssertFalse(sync.isOn)

        sync.createPendingFolder(library: "Todo Tracker")
        var isDirectory: ObjCBool = false
        XCTAssertTrue(FileManager.default.fileExists(atPath: drive.appendingPathComponent("Apps/TodoTrackerSync").path, isDirectory: &isDirectory) && isDirectory.boolValue)
        XCTAssertEqual(sync.settings?.subpath, "Apps/TodoTrackerSync")
        XCTAssertEqual(sync.placeName, "iCloud Drive › Apps › TodoTrackerSync")
        XCTAssertNotNil(SyncSettingsStore(defaults: defaults, secrets: secrets).load()?.bookmark, "the folder is kept for the next launch")
        await sync.finish()
    }

    // MARK: One or the other

    func testTurningSyncOffKeepsTheTasks() async throws {
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        let sync = syncModel(FakeSyncer())
        let model = BoardModel(store: store, phone: phone(), sync: sync)
        model.quickText = "Plan the trip"
        model.capture()
        XCTAssertEqual(model.modeDescription, "Your tasks are on this iPhone and sync on their own with your other devices.")

        sync.turnOff()
        await sync.finish()
        XCTAssertFalse(sync.isOn)
        XCTAssertEqual(model.board.items.map(\.title), ["Plan the trip"])
        XCTAssertEqual(try store.load().items.map(\.title), ["Plan the trip"])
        XCTAssertEqual(model.modeDescription, "Your tasks are only on this iPhone.")
    }

    func testConnectingToAComputerTurnsSyncOffAndDisconnectingBringsThePhonesTasksBack() async throws {
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        let syncer = FakeSyncer()
        let sync = syncModel(syncer)
        let model = BoardModel(store: store, phone: phone(), sync: sync)
        model.quickText = "On this phone"
        model.capture()
        await sync.finish()

        model.open(link: URL(string: "todotracker://pair?v=1&hosts=192.168.1.20:5318&code=c0de&fp=bkHHwIb84H1Te4ePQXbl3y-dAm4kMyZc47CFWjSYarI&name=Studio%20Mac")!)
        try await waitUntil("pairing") { model.board.items.contains { $0.title == "On the computer" } }
        XCTAssertFalse(sync.isOn, "one or the other")
        XCTAssertTrue(model.modeDescription.hasPrefix("Connected to Studio Mac"))
        let before = syncer.runs
        sync.syncNow()
        XCTAssertEqual(syncer.runs, before, "nothing syncs while connected")
        XCTAssertEqual(try store.load().items.map(\.title), ["On this phone"], "the tasks folder isn't touched")

        model.disconnectComputer()
        XCTAssertEqual(model.board.items.map(\.title), ["On this phone"])
        model.complete(try XCTUnwrap(model.board.items.first).id)
        XCTAssertEqual(try store.load().items.first?.isDone, true)
    }
}
