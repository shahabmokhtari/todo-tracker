import XCTest
@testable import OneThingKit
@testable import OneThingTV

/// The Apple TV keeps only a cache (tvOS may empty it): what sync remembers never outlives the tasks it was about.
final class TVStorageTests: XCTestCase {
    private var base: URL!
    /// Where the TV keeps its device id (the Keychain on the TV): not in the cache, so it outlives a purge.
    private var identity: InMemorySecretStore!

    override func setUpWithError() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("tv-\(UUID().uuidString)", isDirectory: true)
        identity = InMemorySecretStore()
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: base)
    }

    func testTheTasksAndWhatSyncRemembersLiveInTheCache() {
        let folders = TVStorage.prepare(base: base, identity: identity)
        XCTAssertEqual(folders.vault, base.appendingPathComponent("vault", isDirectory: true))
        XCTAssertEqual(folders.sync, base.appendingPathComponent("sync", isDirectory: true))
        XCTAssertFalse(folders.wasEmptied)
    }

    func testWhenTvOSEmptiedTheTasksWhatSyncRememberedGoesTooSoItJoinsAgain() throws {
        var folders = TVStorage.prepare(base: base, identity: identity)
        _ = try VaultStore(root: folders.vault)
        try FileManager.default.createDirectory(at: folders.sync.appendingPathComponent("gist-abc"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: folders.sync.appendingPathComponent("device.json"))
        try Data().write(to: folders.sync.appendingPathComponent("gist-abc/joined"))

        // Opened again with the tasks still there: nothing goes.
        folders = TVStorage.prepare(base: base, identity: identity)
        XCTAssertFalse(folders.wasEmptied)
        XCTAssertTrue(FileManager.default.fileExists(atPath: folders.sync.appendingPathComponent("gist-abc/joined").path))

        // tvOS emptied the tasks folder: what sync remembered goes, so the next sync pulls everything first instead of
        // telling the others the tasks were deleted. The device id stays (see below).
        try FileManager.default.removeItem(at: folders.vault)
        folders = TVStorage.prepare(base: base, identity: identity)
        XCTAssertTrue(folders.wasEmptied)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folders.sync.appendingPathComponent("gist-abc").path))
    }

    func testTheTVKeepsItsDeviceIdWhenTvOSEmptiesTheCache() throws {
        var folders = TVStorage.prepare(base: base, identity: identity)
        let first = try SyncState(directory: folders.sync.appendingPathComponent("gist-abc"), deviceDirectory: folders.sync, deviceName: "Apple TV").deviceId
        XCTAssertFalse(first.isEmpty)

        // Opened again (the id is now kept outside the cache too), then tvOS empties the whole cache.
        folders = TVStorage.prepare(base: base, identity: identity)
        try FileManager.default.removeItem(at: base)
        folders = TVStorage.prepare(base: base, identity: identity)
        let again = try SyncState(directory: folders.sync.appendingPathComponent("gist-abc"), deviceDirectory: folders.sync, deviceName: "Apple TV").deviceId
        XCTAssertEqual(again, first, "a new id would leave the old one in the gist as a stranger with no common base")
    }

    func testATokenIsCleanedBeforeItIsKept() {
        XCTAssertEqual(TVPairing.check(token: "  ghp_abcdefghijklmnopqrstuvwxyz0123456789\n"), .ok("ghp_abcdefghijklmnopqrstuvwxyz0123456789"))
        XCTAssertEqual(TVPairing.check(token: "github_pat_11ABCDEFG0123456789_abcdefghijklmnop"), .ok("github_pat_11ABCDEFG0123456789_abcdefghijklmnop"))
        XCTAssertEqual(TVPairing.check(token: ""), .problem("Type or paste your GitHub token first."))
        XCTAssertEqual(TVPairing.check(token: "hello there"), .problem("That doesn’t look like a GitHub token. It starts with ghp_ or github_pat_."))
    }

    func testTheTVIsCalledAppleTVWhenSyncing() {
        XCTAssertEqual(TVPairing.deviceName(""), "Apple TV")
        XCTAssertEqual(TVPairing.deviceName("Living Room"), "Living Room")
    }
}
