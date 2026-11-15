import XCTest
@testable import OneThingKit
@testable import OneThingTV

/// The Apple TV keeps only a cache (tvOS may empty it): what sync remembers never outlives the tasks it was about.
final class TVStorageTests: XCTestCase {
    private var base: URL!

    override func setUpWithError() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("tv-\(UUID().uuidString)", isDirectory: true)
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: base)
    }

    func testTheTasksAndWhatSyncRemembersLiveInTheCache() {
        let folders = TVStorage.prepare(base: base)
        XCTAssertEqual(folders.vault, base.appendingPathComponent("vault", isDirectory: true))
        XCTAssertEqual(folders.sync, base.appendingPathComponent("sync", isDirectory: true))
        XCTAssertFalse(folders.wasEmptied)
    }

    func testWhenTvOSEmptiedTheTasksWhatSyncRememberedGoesTooSoItJoinsAgain() throws {
        var folders = TVStorage.prepare(base: base)
        _ = try VaultStore(root: folders.vault)
        try FileManager.default.createDirectory(at: folders.sync.appendingPathComponent("gist-abc"), withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: folders.sync.appendingPathComponent("device.json"))
        try Data().write(to: folders.sync.appendingPathComponent("gist-abc/joined"))

        // Opened again with the tasks still there: nothing goes.
        folders = TVStorage.prepare(base: base)
        XCTAssertFalse(folders.wasEmptied)
        XCTAssertTrue(FileManager.default.fileExists(atPath: folders.sync.appendingPathComponent("gist-abc/joined").path))

        // tvOS emptied the tasks folder: the sync state (and the device id that agreed on those files) goes, so the
        // next sync pulls everything first instead of telling the others the tasks were deleted.
        try FileManager.default.removeItem(at: folders.vault)
        folders = TVStorage.prepare(base: base)
        XCTAssertTrue(folders.wasEmptied)
        XCTAssertFalse(FileManager.default.fileExists(atPath: folders.sync.path))
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
