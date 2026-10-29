import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// Nothing a phone keeps is lost or sent twice: tasks from an earlier version that can't be read are never covered by
/// an empty tasks folder, a sync stops when it's turned off, and what fails part way is still shown.
@MainActor
final class SyncSafetyTests: XCTestCase {
    private var folder: URL!
    private var suite: String!
    private var secrets: InMemorySecretStore!

    override func setUp() async throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        suite = "SyncSafetyTests-\(UUID().uuidString)"
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
    private var legacy: URL { folder.appendingPathComponent("board.json") }

    private func boardData(_ titles: [String]) throws -> Data {
        let board = TaskBoard()
        for title in titles { _ = try board.addTask(NewTask(title), now: Date()) }
        return try BoardCodec.encode(board)
    }

    private func write(_ data: Data, _ name: String) throws { try data.write(to: folder.appendingPathComponent(name)) }

    private func files() -> [String] { ((try? FileManager.default.contentsOfDirectory(atPath: folder.path)) ?? []).sorted() }

    private var vaultConfig: String { folder.appendingPathComponent("vault/.todo-tracker/config.json").path }

    private func syncModel(_ syncer: DeviceSyncing) -> SyncModel {
        let settings = SyncSettingsStore(defaults: defaults, secrets: secrets)
        settings.save(SyncSettings(kind: .gist, joined: true))
        return SyncModel(syncer: syncer, settings: settings, bookmarks: PathBookmarks(), debounce: 0.05, interval: 3600)
    }

    private func waitUntil(_ what: String = "", _ condition: () -> Bool) async throws {
        for _ in 0..<250 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("Timed out \(what)")
    }

    // MARK: The tasks from before

    func testADamagedBoardComesBackFromItsBackup() throws {
        try write(Data("{ not json".utf8), "board.json")
        try write(try boardData(["Plan the trip"]), "board.json.bak")
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        XCTAssertEqual(try store.load().items.map(\.title), ["Plan the trip"])
        XCTAssertTrue(files().contains { $0.hasPrefix("board.json.corrupt-") }, "the damaged file is kept: \(files())")
    }

    func testABackupWithoutTheBoardComesIn() throws {
        try write(try boardData(["Plan the trip"]), "board.json.bak")
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        XCTAssertEqual(try store.load().items.map(\.title), ["Plan the trip"])
    }

    func testARestoreCutShortComesIn() throws {
        try write(try boardData(["Plan the trip"]), "board.json.restoring")
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        XCTAssertEqual(try store.load().items.map(\.title), ["Plan the trip"])
        XCTAssertTrue(files().contains("board.json.restoring.migrated"))
    }

    func testABoardThatCantBeReadIsNeverCoveredByAnEmptyTasksFolder() throws {
        try write(Data("{ not json".utf8), "board.json")
        let before = try Data(contentsOf: legacy)
        let opened = VaultBoardStore.openOnDevice(base: folder, importLeftover: true)
        XCTAssertNil(opened.store)
        XCTAssertTrue(opened.message?.hasPrefix("Couldn't open your tasks") == true, opened.message ?? "")
        XCTAssertFalse(FileManager.default.fileExists(atPath: vaultConfig), "no fresh tasks folder made over them")
        XCTAssertEqual(try Data(contentsOf: legacy), before, "left as it is")
        XCTAssertEqual(files(), ["board.json", "vault"])

        // The app: read-only, says why.
        let model = BoardModel(store: opened.store, openError: opened.message, fallback: opened.fallback)
        XCTAssertTrue(model.isReadOnly)
        XCTAssertEqual(model.status, opened.message)
        model.quickText = "New"
        model.capture()
        XCTAssertEqual(try Data(contentsOf: legacy), before)
    }

    func testABoardFromANewerVersionIsLeftAlone() throws {
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: try boardData(["Plan the trip"])) as? [String: Any])
        json["schemaVersion"] = 99
        try write(try JSONSerialization.data(withJSONObject: json), "board.json")
        let before = try Data(contentsOf: legacy)
        let opened = VaultBoardStore.openOnDevice(base: folder, importLeftover: true)
        XCTAssertNil(opened.store)
        XCTAssertNotNil(opened.message)
        XCTAssertFalse(FileManager.default.fileExists(atPath: vaultConfig))
        XCTAssertEqual(try Data(contentsOf: legacy), before)
    }

    func testATasksFolderThatCantBeMadeShowsTheTasksFromBeforeReadOnly() throws {
        try write(try boardData(["Plan the trip"]), "board.json")
        // Something in the way of the tasks folder (a disk error would do the same).
        try write(Data("x".utf8), "vault")
        let opened = VaultBoardStore.openOnDevice(base: folder, importLeftover: true)
        XCTAssertNil(opened.store)
        XCTAssertEqual(opened.fallback?.items.map(\.title), ["Plan the trip"])
        let model = BoardModel(store: nil, openError: opened.message, fallback: opened.fallback)
        XCTAssertEqual(model.board.items.map(\.title), ["Plan the trip"])
        XCTAssertTrue(model.isReadOnly)
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path), "untouched")
    }

    func testALeftoverThatCantComeInDoesntStopTheTasksFolderOpening() throws {
        _ = try VaultBoardStore.open(base: folder, importLeftover: true).apply(TaskBoard()) { try $0.addTask(NewTask("Already here"), now: Date()) }
        try write(Data("{ not json".utf8), "board.json")
        let opened = VaultBoardStore.openOnDevice(base: folder, importLeftover: true)
        let store = try XCTUnwrap(opened.store)
        XCTAssertEqual(try store.load().items.map(\.title), ["Already here"])
        XCTAssertNotNil(opened.message, "said")
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path), "kept, tried again later")
    }

    func testChangesStayOffAfterConnectingAndDisconnectingWhenTheTasksCouldntBeOpened() async throws {
        let phone = PhoneSetup(pairings: PairingStore(defaults: defaults, secrets: secrets), cacheURL: folder.appendingPathComponent("computer-board.json"), phoneName: "Test iPhone") { _ in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [FakeComputer.self]
            return URLSession(configuration: configuration)
        }
        let model = BoardModel(store: nil, phone: phone, openError: "Couldn't open your tasks (test).", fallback: try BoardCodec.decode(try boardData(["From before"])))
        model.open(link: URL(string: "todotracker://pair?v=1&hosts=192.168.1.20:5318&code=c0de&fp=bkHHwIb84H1Te4ePQXbl3y-dAm4kMyZc47CFWjSYarI&name=Studio%20Mac")!)
        try await waitUntil("pairing") { model.board.items.contains { $0.title == "On the computer" } }
        model.disconnectComputer()
        XCTAssertTrue(model.isReadOnly)
        XCTAssertEqual(model.board.items.map(\.title), ["From before"])
        let item = try XCTUnwrap(model.board.items.first)
        model.complete(item.id)
        XCTAssertFalse(item.isDone, "no change to tasks that weren't opened")
    }

    // MARK: Stopping a sync

    func testTurningSyncOffStopsTheSyncRunningSoNothingIsPublishedAfter() async throws {
        let syncer = FakeSyncer()
        syncer.delay = 200_000_000
        let sync = syncModel(syncer)
        sync.syncNow()
        try await waitUntil("started") { syncer.runs == 1 }
        sync.turnOff()
        try await Task.sleep(nanoseconds: 400_000_000)
        XCTAssertEqual(syncer.published, 0)
        XCTAssertFalse(sync.isSyncing)
        try await waitUntil("forgotten") { syncer.resets == 1 }
    }

    func testTurningSyncOffDropsTheSyncWaitingAfterAChange() async throws {
        let syncer = FakeSyncer()
        let sync = syncModel(syncer)
        sync.changed()
        sync.turnOff()
        try await Task.sleep(nanoseconds: 200_000_000)
        XCTAssertEqual(syncer.runs, 0)
    }

    func testConnectingToAComputerStopsTheSyncRunning() async throws {
        let syncer = FakeSyncer()
        syncer.delay = 200_000_000
        let sync = syncModel(syncer)
        let phone = PhoneSetup(pairings: PairingStore(defaults: defaults, secrets: secrets), cacheURL: folder.appendingPathComponent("computer-board.json"), phoneName: "Test iPhone") { _ in
            let configuration = URLSessionConfiguration.ephemeral
            configuration.protocolClasses = [FakeComputer.self]
            return URLSession(configuration: configuration)
        }
        let model = BoardModel(store: try VaultBoardStore.open(base: folder, importLeftover: true), phone: phone, sync: sync)
        sync.syncNow()
        try await waitUntil("started") { syncer.runs == 1 }
        model.open(link: URL(string: "todotracker://pair?v=1&hosts=192.168.1.20:5318&code=c0de&fp=bkHHwIb84H1Te4ePQXbl3y-dAm4kMyZc47CFWjSYarI&name=Studio%20Mac")!)
        try await waitUntil("pairing") { model.isRemote }
        try await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(syncer.published, 0)
        XCTAssertFalse(sync.isOn)
    }

    func testABackgroundSyncStopsWhenIOSSaysTimesUp() async throws {
        let syncer = FakeSyncer()
        syncer.delay = 300_000_000
        let sync = syncModel(syncer)
        let background = Task { await sync.syncInBackground() }
        try await waitUntil("started") { syncer.runs == 1 }
        background.cancel()
        await background.value
        try await Task.sleep(nanoseconds: 400_000_000)
        XCTAssertEqual(syncer.published, 0)
    }

    func testWhatASyncThatFailedBroughtIsShownToo() async throws {
        let store = try VaultBoardStore.open(base: folder, importLeftover: true)
        let syncer = FakeSyncer()
        syncer.during = { _ = try store.vault.update { try $0.addTask(NewTask("From the laptop"), now: Date()) } }
        syncer.failure = SyncSetupError("The other device's file is damaged.")
        let sync = syncModel(syncer)
        let model = BoardModel(store: store, sync: sync)
        sync.syncNow()
        await sync.finish()
        XCTAssertEqual(sync.problem, "The other device's file is damaged.")
        XCTAssertEqual(model.board.items.map(\.title), ["From the laptop"])
    }

    // MARK: Secrets

    func testAFolderPlaceForgetsAnEarlierGistToken() async throws {
        let sync = syncModel(FakeSyncer())
        sync.useGist(token: "ghp_old", library: "Todo Tracker")
        XCTAssertEqual(secrets.read("gist-token"), "ghp_old")
        let picked = folder.appendingPathComponent("TodoTrackerSync", isDirectory: true)
        try FileManager.default.createDirectory(at: picked, withIntermediateDirectories: true)
        sync.choose(folder: picked, library: "Todo Tracker")
        XCTAssertEqual(sync.settings?.kind, .folder)
        XCTAssertNil(secrets.read("gist-token"))
        await sync.finish()
    }

    #if canImport(Security)
    func testKeychainSecretsStayOnThisDevice() {
        XCTAssertEqual(KeychainSecretStore.accessibility as String, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly as String, "never restored onto another device")
    }
    #endif
}
