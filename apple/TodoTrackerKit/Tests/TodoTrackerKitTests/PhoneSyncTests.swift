import Foundation
import XCTest
@testable import TodoTrackerKit

/// Bookmarks that are just the path (a test can't make security-scoped ones), optionally saying they're stale.
final class FakeBookmarks: FolderBookmarks, @unchecked Sendable {
    var stale = false
    var made: [URL] = []

    func make(_ url: URL) throws -> Data {
        made.append(url)
        return Data(("v\(made.count):" + url.path).utf8)
    }

    func resolve(_ bookmark: Data) throws -> (url: URL, stale: Bool) {
        let text = String(decoding: bookmark, as: UTF8.self)
        guard let colon = text.firstIndex(of: ":") else { throw CocoaError(.fileNoSuchFile) }
        return (URL(fileURLWithPath: String(text[text.index(after: colon)...]), isDirectory: true), stale)
    }
}

/// An iPhone syncing on its own (Settings › Sync): the place it was given (iCloud Drive's TodoTrackerSync folder or a
/// gist), joining the computers' library the first time, and later syncs both ways.
final class PhoneSyncTests: XCTestCase {
    private let tz = TimeZone(secondsFromGMT: -18000)!
    private var now = Fixtures.date("2026-01-05T14:00:00.000Z")
    private var suite = ""

    override func setUp() {
        suite = "PhoneSyncTests-\(UUID().uuidString)"
    }

    override func tearDown() {
        UserDefaults(suiteName: suite)?.removePersistentDomain(forName: suite)
    }

    private func tick() -> Date {
        now = now.addingTimeInterval(1)
        return now
    }

    /// A computer's tasks folder syncing through `Apps/TodoTrackerSync/<library>` (as the Mac and Windows apps do).
    private final class Computer {
        let store: VaultStore
        let engine: SyncEngine

        init(_ temp: TempDir, sharedRoot: URL, tz: TimeZone, clock: @escaping () -> Date) throws {
            store = try VaultStore(root: temp.url.appendingPathComponent("laptop/Todo Tracker"), timeZone: tz, clock: clock)
            let state = try SyncState(directory: temp.url.appendingPathComponent("laptop-sync/icloud"), deviceDirectory: temp.url.appendingPathComponent("laptop-sync"), deviceName: "Laptop")
            engine = SyncEngine(store: store, remote: FolderRemote(root: sharedRoot), state: state, clock: clock)
        }

        func titles() throws -> [String] { try store.read { $0.items.map(\.title) }.sorted() }
    }

    private struct Phone {
        let store: VaultStore
        let syncer: PhoneSyncer
        let settings: SyncSettingsStore
        let legacy: URL
    }

    private func phone(_ temp: TempDir, bookmarks: FolderBookmarks = FakeBookmarks(), http: HTTPTransport = FakeGitHub(), secrets: SecretStore = InMemorySecretStore()) throws -> Phone {
        let base = temp.url.appendingPathComponent("phone/TodoTracker")
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
        let store = try VaultStore(root: base.appendingPathComponent("vault"), timeZone: tz, clock: { [unowned self] in self.tick() })
        let settings = SyncSettingsStore(defaults: UserDefaults(suiteName: suite)!, secrets: secrets)
        let legacy = base.appendingPathComponent("board.json")
        let syncer = PhoneSyncer(store: store, directory: base.appendingPathComponent("sync"), settings: settings, bookmarks: bookmarks, http: http,
                                 deviceName: "Test iPhone", legacyBoard: legacy, access: { _ in CoordinatedFolderAccess() }, clock: { [unowned self] in self.tick() })
        return Phone(store: store, syncer: syncer, settings: settings, legacy: legacy)
    }

    private func titles(_ store: VaultStore) throws -> [String] { try store.read { $0.items.map(\.title) }.sorted() }

    // MARK: The folder picked

    func testThePickedFolderIsTodoTrackerSyncOrHoldsIt() {
        XCTAssertEqual(SyncFolderChoice.choose(pickedName: "TodoTrackerSync") { _ in false }, .ready(subpath: ""))
        XCTAssertEqual(SyncFolderChoice.choose(pickedName: "Apps") { $0 == "TodoTrackerSync" }, .ready(subpath: "TodoTrackerSync"))
        XCTAssertEqual(SyncFolderChoice.choose(pickedName: "iCloud Drive") { $0 == "Apps/TodoTrackerSync" }, .ready(subpath: "Apps/TodoTrackerSync"))
        XCTAssertEqual(SyncFolderChoice.choose(pickedName: "Apps") { _ in false }, .missing(subpath: "TodoTrackerSync"))
        XCTAssertEqual(SyncFolderChoice.choose(pickedName: "iCloud Drive") { _ in false }, .missing(subpath: "Apps/TodoTrackerSync"))
    }

    func testLibraryNamesAreCleanedAsTheComputersDo() {
        XCTAssertEqual(SyncLibrary.clean("  Todo Tracker  "), "Todo Tracker")
        XCTAssertEqual(SyncLibrary.clean("Work/../Stuff!"), "WorkStuff")
        XCTAssertEqual(SyncLibrary.clean(String(repeating: "a", count: 80)).count, 60)
    }

    func testTheSettingsAndTheBookmarkAreKeptAndTheTokenIsASecret() {
        let secrets = InMemorySecretStore()
        let store = SyncSettingsStore(defaults: UserDefaults(suiteName: suite)!, secrets: secrets)
        XCTAssertNil(store.load())
        let saved = SyncSettings(kind: .folder, library: "Todo Tracker", bookmark: Data("bookmark".utf8), subpath: "Apps/TodoTrackerSync", folderName: "iCloud Drive")
        store.save(saved)
        XCTAssertEqual(SyncSettingsStore(defaults: UserDefaults(suiteName: suite)!, secrets: secrets).load(), saved, "kept across a restart")

        store.setToken("ghp_secret")
        XCTAssertEqual(secrets.read("gist-token"), "ghp_secret")
        XCTAssertNil(UserDefaults(suiteName: suite)!.dictionary(forKey: "phoneSync")?.values.compactMap { $0 as? String }.first { $0.contains("ghp_") })
        store.clear()
        XCTAssertNil(store.load())
        XCTAssertNil(store.token, "turning sync off forgets the token")
    }

    func testAStaleBookmarkIsMadeAgainAndKept() async throws {
        let temp = TempDir()
        let picked = temp.url.appendingPathComponent("iCloud/Apps/TodoTrackerSync")
        try FileManager.default.createDirectory(at: picked, withIntermediateDirectories: true)
        let bookmarks = FakeBookmarks()
        let phone = try phone(temp, bookmarks: bookmarks)
        phone.settings.save(SyncSettings(kind: .folder, bookmark: try bookmarks.make(picked), folderName: "TodoTrackerSync"))
        bookmarks.stale = true

        _ = try await phone.syncer.run()
        let kept = try XCTUnwrap(phone.settings.load()?.bookmark)
        XCTAssertEqual(String(decoding: kept, as: UTF8.self), "v2:" + picked.path, "made again")
        XCTAssertTrue(FileManager.default.fileExists(atPath: picked.appendingPathComponent("Todo Tracker/devices").path), "the library folder inside TodoTrackerSync")
    }

    // MARK: Joining and syncing

    func testJoiningTheComputersLibraryBringsTheirTasksAndTheOldBoardBesideWithoutCopies() async throws {
        let temp = TempDir()
        let sync = temp.url.appendingPathComponent("iCloud/Apps/TodoTrackerSync")
        let clock = { [unowned self] in self.tick() }
        let laptop = try Computer(temp, sharedRoot: sync.appendingPathComponent("Todo Tracker"), tz: tz, clock: clock)
        let shared = try laptop.store.update { try $0.addTask(NewTask("Plan the trip"), now: self.now) }
        _ = try laptop.store.update { try $0.addTask(NewTask("Call the bank"), now: self.now) }
        try await laptop.engine.sync()

        // The phone's own board from before: a copy of one of the laptop's tasks (same id) and one of its own.
        let phone = try phone(temp)
        let board = TaskBoard()
        let copy = try board.addTask(NewTask("Plan the trip (old copy)"), now: now)
        copy.id = shared.id
        _ = try board.addTask(NewTask("Water the plants"), now: now)
        try BoardCodec.encode(board).write(to: phone.legacy)

        // iCloud Drive › Apps picked: TodoTrackerSync is inside.
        let bookmarks = FakeBookmarks()
        phone.settings.save(SyncSettings(kind: .folder, bookmark: try bookmarks.make(sync.deletingLastPathComponent()), subpath: "TodoTrackerSync", folderName: "Apps"))
        let joinedRun = try await phone.syncer.run()
        let joined = try XCTUnwrap(joinedRun)
        XCTAssertTrue(joined.complete)
        XCTAssertTrue(joined.published)
        XCTAssertEqual(phone.settings.load()?.joined, true)
        XCTAssertEqual(try titles(phone.store), ["Call the bank", "Plan the trip", "Water the plants"], "no copies, nothing twice")
        XCTAssertTrue(FileManager.default.fileExists(atPath: phone.legacy.path + ".migrated"), "the old board is kept")
        XCTAssertFalse(FileManager.default.fileExists(atPath: phone.legacy.path))

        try await laptop.engine.sync()
        XCTAssertEqual(try laptop.titles(), ["Call the bank", "Plan the trip", "Water the plants"])
        let status = await phone.syncer.status()
        XCTAssertEqual(status.devices.map(\.name), ["Laptop"])
    }

    func testAJoinWaitsUntilEverythingArrivedBeforeAnythingGoesOut() async throws {
        let temp = TempDir()
        let sync = temp.url.appendingPathComponent("iCloud/Apps/TodoTrackerSync")
        let laptop = try Computer(temp, sharedRoot: sync.appendingPathComponent("Todo Tracker"), tz: tz, clock: { [unowned self] in self.tick() })
        let id = try laptop.store.update { try $0.addTask(NewTask("Plan the trip"), now: self.now).id }
        try await laptop.engine.sync()
        // The task's content hasn't downloaded yet.
        let blobs = sync.appendingPathComponent("Todo Tracker/blobs")
        for name in FileManager.default.enumerator(atPath: blobs.path)!.allObjects as! [String] where name.count > 3 {
            let url = blobs.appendingPathComponent(name)
            if (try? String(contentsOf: url, encoding: .utf8))?.contains("# Plan the trip") == true { try FileManager.default.removeItem(at: url) }
        }

        let phone = try phone(temp)
        let bookmarks = FakeBookmarks()
        phone.settings.save(SyncSettings(kind: .folder, bookmark: try bookmarks.make(sync), folderName: "TodoTrackerSync"))
        let firstRun = try await phone.syncer.run()
        let first = try XCTUnwrap(firstRun)
        XCTAssertFalse(first.complete)
        XCTAssertFalse(first.published, "nothing goes out before everything came in")
        XCTAssertEqual(phone.settings.load()?.joined, false)

        try laptop.store.update { try $0.update(id, details: "Again", now: self.now) }
        try await laptop.engine.sync()
        let secondRun = try await phone.syncer.run()
        let second = try XCTUnwrap(secondRun)
        XCTAssertTrue(second.complete)
        XCTAssertEqual(phone.settings.load()?.joined, true)
        XCTAssertEqual(try titles(phone.store), ["Plan the trip"])
    }

    func testChangesGoBothWaysAndEditsOnBothMerge() async throws {
        let temp = TempDir()
        let sync = temp.url.appendingPathComponent("iCloud/Apps/TodoTrackerSync")
        let laptop = try Computer(temp, sharedRoot: sync.appendingPathComponent("Todo Tracker"), tz: tz, clock: { [unowned self] in self.tick() })
        try await laptop.engine.sync()
        let phone = try phone(temp)
        let bookmarks = FakeBookmarks()
        phone.settings.save(SyncSettings(kind: .folder, bookmark: try bookmarks.make(sync), folderName: "TodoTrackerSync"))
        _ = try await phone.syncer.run()

        // Made on the phone: on the laptop after both synced.
        let id = try phone.store.update { try $0.addTask(NewTask("Plan the trip", details: "Pack light.\n\nThe hotel is booked."), now: self.now).id }
        _ = try await phone.syncer.run()
        try await laptop.engine.sync()
        XCTAssertEqual(try laptop.titles(), ["Plan the trip"])
        // A minute later the phone sees the laptop has it too (what both agree on is what later edits merge against).
        _ = try await phone.syncer.run()

        // Edited on both: the title on the laptop, a note on the phone; both stay.
        try laptop.store.update { try $0.update(id, title: "Plan the summer trip", now: self.now) }
        _ = try phone.store.update { try $0.addNote(id, text: "Booked the train", now: self.now) }
        try await laptop.engine.sync()
        _ = try await phone.syncer.run()
        try await laptop.engine.sync()
        for store in [phone.store, laptop.store] {
            let item = try XCTUnwrap(try store.read { $0.find(id).map { ($0.title, $0.notes.map(\.text)) } })
            XCTAssertEqual(item.0, "Plan the summer trip")
            XCTAssertEqual(item.1, ["Booked the train"])
            XCTAssertEqual(try store.read { $0.items.count }, 1, "merged, not copied")
        }
        let status = await phone.syncer.status()
        XCTAssertTrue(status.conflicts.isEmpty)
        XCTAssertNotNil(status.lastSync)
    }

    func testAGistNeedsAToken() async throws {
        let temp = TempDir()
        let github = FakeGitHub()
        let secrets = InMemorySecretStore()
        let phone = try phone(temp, http: github, secrets: secrets)
        phone.settings.save(SyncSettings(kind: .gist))
        do {
            _ = try await phone.syncer.run()
            XCTFail("no token")
        } catch let error as SyncSetupError {
            XCTAssertTrue(error.message.contains("GitHub token"))
        }

        phone.settings.setToken("t")
        _ = try await phone.syncer.run()
        XCTAssertEqual(github.gists.count, 1, "made on the first publish")
        XCTAssertNotNil(phone.settings.gistId("Todo Tracker"), "remembered")
    }

    func testSyncOffDoesNothing() async throws {
        let temp = TempDir()
        let phone = try phone(temp)
        let result = try await phone.syncer.run()
        XCTAssertNil(result)
    }
}
