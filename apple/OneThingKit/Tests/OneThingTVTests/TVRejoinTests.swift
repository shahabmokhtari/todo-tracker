import XCTest
@testable import OneThingKit
@testable import OneThingTV

/// tvOS empties the TV's cache (all of it, or only some task files): the TV joins again as the same device, pulls
/// first, and its old copy never undoes edits, brings deleted tasks back or makes "(from Laptop)" copies.
final class TVRejoinTests: XCTestCase {
    private var now = Date(timeIntervalSince1970: 1_800_000_000)
    private var root: URL!
    private var shared: URL!
    private var laptop: VaultStore!
    private var laptopSync: SyncEngine!
    private let identity = InMemorySecretStore()

    private func tick() -> Date {
        now = now.addingTimeInterval(1)
        return now
    }

    override func setUpWithError() throws {
        root = FileManager.default.temporaryDirectory.appendingPathComponent("tv-rejoin-\(UUID().uuidString)", isDirectory: true)
        shared = root.appendingPathComponent("shared", isDirectory: true)
        try FileManager.default.createDirectory(at: shared, withIntermediateDirectories: true)
        let clock = { [unowned self] in self.tick() }
        laptop = try VaultStore(root: root.appendingPathComponent("laptop"), clock: clock)
        let state = try SyncState(directory: root.appendingPathComponent("laptop-sync/p"), deviceDirectory: root.appendingPathComponent("laptop-sync"), deviceName: "Laptop")
        laptopSync = SyncEngine(store: laptop, remote: FolderRemote(root: shared), state: state, clock: clock)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: root)
    }

    private var tvBase: URL { root.appendingPathComponent("tv-cache", isDirectory: true) }

    /// The TV opened as the app opens it: its cache made safe, its copy of the tasks checked, then its sync.
    private func openTV() throws -> (engine: SyncEngine, store: VaultStore, emptied: Bool) {
        let folders = TVStorage.prepare(base: tvBase, identity: identity)
        let clock = { [unowned self] in self.tick() }
        let store = try VaultStore(root: folders.vault, clock: clock)
        let missing = TVStorage.forgetIfTasksMissing(folders, vault: store)
        let state = try SyncState(directory: folders.sync.appendingPathComponent("p"), deviceDirectory: folders.sync, deviceName: "Apple TV")
        return (SyncEngine(store: store, remote: FolderRemote(root: shared), state: state, clock: clock), store, folders.wasEmptied || missing)
    }

    private func titles(_ store: VaultStore) throws -> [String] {
        try store.read { $0.items.map(\.title) }.sorted()
    }

    func testAfterTvOSEmptiesTheCacheTheLaptopsEditsAndDeletesStand() async throws {
        let trip = try laptop.update { try $0.addTask(NewTask("Plan the trip"), now: self.now) }
        let bank = try laptop.update { try $0.addTask(NewTask("Call the bank"), now: self.now) }
        try await laptopSync.sync()

        let tv = try openTV()
        XCTAssertFalse(tv.emptied)
        try await tv.engine.join(importing: nil)
        try await laptopSync.sync()

        // Later, on the laptop: one task edited, another edited then deleted.
        now = now.addingTimeInterval(3600)
        try laptop.update { try $0.update(trip.id, title: "Plan the trip to Rome", now: self.now) }
        try laptop.update { try $0.update(bank.id, title: "Call the bank about the card", now: self.now) }
        try await laptopSync.sync()
        try laptop.update { try $0.delete(bank.id, now: self.now) }
        try await laptopSync.sync()

        // tvOS empties the TV's whole cache, and the TV joins again.
        try FileManager.default.removeItem(at: tvBase)
        let again = try openTV()
        try await again.engine.join(importing: nil)
        try await laptopSync.sync()

        XCTAssertEqual(try titles(again.store), ["Plan the trip to Rome"])
        XCTAssertEqual(try titles(laptop), ["Plan the trip to Rome"], "no edit undone, no deleted task back, no copies")
    }

    func testTaskFilesMissingFromTheTVAreNeverSentAsDeleted() async throws {
        try laptop.update { try $0.addTask(NewTask("Water the plants"), now: self.now) }
        let gone = try laptop.update { try $0.addTask(NewTask("Old idea"), now: self.now) }
        try laptop.update { try $0.addTask(NewTask("Book the dentist"), now: self.now) }
        try await laptopSync.sync()

        let tv = try openTV()
        try await tv.engine.join(importing: nil)
        try await laptopSync.sync()

        // A delete made on the laptop reaches the TV: that's not tvOS emptying anything.
        try laptop.update { try $0.delete(gone.id, now: self.now) }
        try await laptopSync.sync()
        try await tv.engine.sync()
        let reopened = try openTV()
        XCTAssertFalse(reopened.emptied)
        try await reopened.engine.sync()

        // tvOS removes one task file but leaves the settings and what sync remembered.
        let vault = tvBase.appendingPathComponent("vault")
        let files = (FileManager.default.enumerator(atPath: vault.path)?.allObjects as? [String] ?? []).filter { $0.hasSuffix(".md") }
        let dentist = try XCTUnwrap(files.first { $0.localizedCaseInsensitiveContains("dentist") })
        try FileManager.default.removeItem(at: vault.appendingPathComponent(dentist))

        let after = try openTV()
        XCTAssertTrue(after.emptied, "a task the TV last sent is gone: it joins again instead of sending a delete")
        try await after.engine.join(importing: nil)
        try await laptopSync.sync()

        XCTAssertEqual(try titles(laptop), ["Book the dentist", "Water the plants"])
        XCTAssertEqual(try titles(after.store), ["Book the dentist", "Water the plants"])
    }
}
