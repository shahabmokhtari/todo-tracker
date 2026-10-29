import Foundation
import XCTest
@testable import TodoTrackerKit

/// Two phones syncing through a shared folder (or a gist): what one does arrives on the other, byte for byte.
final class SyncEngineTests: XCTestCase {
    private let tz = TimeZone(secondsFromGMT: -18000)!
    private var now = Fixtures.date("2026-01-05T14:00:00.000Z")

    private final class Phone {
        let store: VaultStore
        let engine: SyncEngine
        let vault: URL

        init(_ temp: TempDir, _ name: String, remote: SyncRemote, tz: TimeZone, clock: @escaping () -> Date, legacy: URL? = nil) throws {
            vault = temp.url.appendingPathComponent(name).appendingPathComponent("vault")
            store = try VaultStore(root: vault, timeZone: tz, clock: clock, legacyBoard: legacy)
            let state = try SyncState(directory: temp.url.appendingPathComponent(name).appendingPathComponent("sync"), deviceDirectory: temp.url.appendingPathComponent(name).appendingPathComponent("device"), deviceName: name)
            engine = SyncEngine(store: store, remote: remote, state: state, clock: clock)
        }

        func text(_ rel: String) -> String? { (try? Data(contentsOf: vault.appendingPathComponent(rel))).map { String(decoding: $0, as: UTF8.self) } }
        func titles() throws -> [String] { try store.read { $0.items.map(\.title) } }
    }

    /// Time goes on (a second each time a phone looks at the clock), as it does between syncs.
    private func tick() -> Date {
        now = now.addingTimeInterval(1)
        return now
    }

    private func pair(_ temp: TempDir, remote: () -> SyncRemote) throws -> (Phone, Phone) {
        let clock = { [unowned self] in self.tick() }
        return (try Phone(temp, "Laptop", remote: remote(), tz: tz, clock: clock), try Phone(temp, "Phone", remote: remote(), tz: tz, clock: clock))
    }

    func testATaskMadeOnOnePhoneArrivesOnTheOtherByteForByte() async throws {
        let temp = TempDir()
        let shared = temp.url.appendingPathComponent("iCloud/Apps/TodoTrackerSync/Todo Tracker")
        let (a, b) = try pair(temp) { FolderRemote(root: shared) }
        try await a.engine.sync()
        try await b.engine.sync()

        let id = try a.store.update { try $0.addTask(NewTask("Plan the trip"), now: self.now).id }
        let rel = try XCTUnwrap(a.store.path(of: id))
        try await a.engine.sync()
        let result = try await b.engine.sync()
        XCTAssertGreaterThan(result.changes, 0)
        XCTAssertEqual(b.text(rel), a.text(rel))
        XCTAssertEqual(try b.titles(), ["Plan the trip"])

        // Nothing changed on either side: nothing to do.
        let again = try await b.engine.sync()
        XCTAssertTrue(again.skipped)
    }

    func testEditsOnBothSidesMergeAndADeleteGoesToTheTrash() async throws {
        let temp = TempDir()
        let (a, b) = try pair(temp) { FolderRemote(root: temp.url.appendingPathComponent("shared")) }
        let root = try a.store.update { board -> UUID in
            let root = try board.addTask(NewTask("Trip"), now: self.now)
            _ = try board.addTask(NewTask("Book flights", parentId: root.id), now: self.now)
            _ = try board.addTask(NewTask("Book hotel", parentId: root.id), now: self.now)
            return root.id
        }
        try await a.engine.sync()
        try await b.engine.sync()
        try await a.engine.sync()

        // One checks a step, the other renames the other step.
        try a.store.update { board in try board.complete(board.find(root)!.children[0].id, now: self.now) }
        try b.store.update { board in try board.update(board.find(root)!.children[1].id, title: "Book a hotel", now: self.now) }
        try await a.engine.sync()
        try await b.engine.sync()
        try await a.engine.sync()
        for phone in [a, b] {
            let steps = try phone.store.read { board in board.find(root)!.children.map { "\($0.title)\($0.isDone ? " ✓" : "")" } }
            XCTAssertEqual(steps, ["Book flights ✓", "Book a hotel"])
        }
        let rel = try XCTUnwrap(a.store.path(of: root))
        XCTAssertEqual(a.text(rel), b.text(rel))

        // Deleted on one: deleted on the other too, into its trash (never lost).
        try a.store.update { try $0.delete(root, now: self.now) }
        try await a.engine.sync()
        try await b.engine.sync()
        XCTAssertNil(b.text(rel))
        let trash = try FileManager.default.contentsOfDirectory(atPath: b.vault.appendingPathComponent(".todo-tracker/trash").path)
        XCTAssertTrue(trash.contains { $0.hasSuffix("Trip.md") }, "\(trash)")
        XCTAssertEqual(try b.titles(), [])
    }

    func testJoiningBringsTheOldBoardInBesideWhatCame() async throws {
        let temp = TempDir()
        let github = FakeGitHub()
        let clock = { [unowned self] in self.tick() }
        var gistIds: [String: String] = [:]
        func remote(_ who: String) -> GistRemote { GistRemote(http: github, token: { "t" }, loadId: { gistIds[who] }, saveId: { gistIds[who] = $0 }) }

        let laptop = try Phone(temp, "Laptop", remote: remote("a"), tz: tz, clock: clock)
        let shared = try laptop.store.update { try $0.addTask(NewTask("Shared task"), now: self.now) }
        try await laptop.engine.sync()

        // The phone's own board (from before): a copy of the shared task (same id, used against the computer) and one
        // of its own.
        let board = TaskBoard()
        let copy = try board.addTask(NewTask("Shared task (old copy)"), now: now)
        copy.id = shared.id
        _ = try board.addTask(NewTask("Only on the phone"), now: now)
        let legacy = temp.url.appendingPathComponent("board.json")
        try BoardCodec.encode(board).write(to: legacy)
        let fixed = try BoardCodec.decode(Data(contentsOf: legacy))
        XCTAssertEqual(fixed.items.map(\.id).first, shared.id)

        let phone = try Phone(temp, "Phone", remote: remote("b"), tz: tz, clock: clock)
        try await phone.engine.join(importing: legacy)
        XCTAssertEqual(try phone.titles().sorted(), ["Only on the phone", "Shared task"])
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path + ".migrated"))

        try await laptop.engine.sync()
        XCTAssertEqual(try laptop.titles().sorted(), ["Only on the phone", "Shared task"])
        XCTAssertEqual(github.gists.count, 1)
    }

    private func blobs(_ shared: URL) -> [URL] {
        let root = shared.appendingPathComponent("blobs")
        return (FileManager.default.enumerator(atPath: root.path)?.allObjects as? [String] ?? []).filter { $0.count > 3 }.map { root.appendingPathComponent($0) }
    }

    func testJoiningWaitsUntilEverythingHasArrived() async throws {
        let temp = TempDir()
        let shared = temp.url.appendingPathComponent("shared")
        let clock = { [unowned self] in self.tick() }
        let laptop = try Phone(temp, "Laptop", remote: FolderRemote(root: shared), tz: tz, clock: clock)
        let id = try laptop.store.update { try $0.addTask(NewTask("Shared task"), now: self.now).id }
        try await laptop.engine.sync()
        let taskBlob = try XCTUnwrap(blobs(shared).first { (try? String(contentsOf: $0, encoding: .utf8))?.contains("# Shared task") == true })
        try FileManager.default.removeItem(at: taskBlob)

        let board = TaskBoard()
        _ = try board.addTask(NewTask("Only on the phone"), now: now)
        let legacy = temp.url.appendingPathComponent("board.json")
        try BoardCodec.encode(board).write(to: legacy)
        let phone = try Phone(temp, "Phone", remote: FolderRemote(root: shared), tz: tz, clock: clock)
        let first = try await phone.engine.join(importing: legacy)
        XCTAssertFalse(first.complete)
        XCTAssertFalse(first.published)
        XCTAssertNotNil(first.problem)
        XCTAssertEqual(try phone.titles(), [])
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path), "nothing imported yet")

        // The laptop publishes again (the missing content is written again); now the join goes through.
        try laptop.store.update { try $0.update(id, details: "More", now: self.now) }
        try await laptop.engine.sync()
        let second = try await phone.engine.join(importing: legacy)
        XCTAssertTrue(second.complete)
        XCTAssertEqual(try phone.titles().sorted(), ["Only on the phone", "Shared task"])
    }

    func testADamagedContentIsSaidAndWrittenAgainByTheDeviceThatHasIt() async throws {
        let temp = TempDir()
        let shared = temp.url.appendingPathComponent("shared")
        let (a, b) = try pair(temp) { FolderRemote(root: shared) }
        let id = try a.store.update { try $0.addTask(NewTask("Plan"), now: self.now).id }
        try await a.engine.sync()
        let blob = try XCTUnwrap(blobs(shared).first { (try? String(contentsOf: $0, encoding: .utf8))?.contains("# Plan") == true })
        try Data("cut short".utf8).write(to: blob)
        let broken = try await b.engine.sync()
        XCTAssertTrue(broken.problem?.contains("didn't arrive whole") == true, broken.problem ?? "")
        XCTAssertEqual(try b.titles(), [])

        try a.store.update { try $0.update(id, details: "v2", now: self.now) }
        try a.store.update { try $0.update(id, details: nil, now: self.now) }
        try await a.engine.sync()
        let fixed = try await b.engine.sync()
        XCTAssertNil(fixed.problem)
        XCTAssertEqual(try b.titles(), ["Plan"])
    }
}
