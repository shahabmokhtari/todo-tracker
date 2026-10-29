import Foundation
import XCTest
@testable import TodoTrackerKit

/// Nothing is lost: the trash keeps what was put there for 30 days from when it was put there, and a change that fails
/// part way (bringing an old board in, applying a sync) leaves everything as it was.
final class VaultSafetyTests: XCTestCase {
    private let tz = TimeZone(secondsFromGMT: -18000)!
    private var now = Fixtures.date("2026-03-01T14:00:00.000Z")

    private func trashNames(_ root: URL) -> [String] {
        ((try? FileManager.default.contentsOfDirectory(atPath: root.appendingPathComponent(".todo-tracker/trash").path)) ?? []).sorted()
    }

    func testATaskLastEditedLongAgoStaysInTheTrashAfterItWasPutThere() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let id = try store.update { try $0.addTask(NewTask("Old idea"), now: self.now).id }
        let file = temp.url.appendingPathComponent("Work/Old idea.md")
        // Last edited 60 days ago: moving it to the trash keeps that time.
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-60 * 86_400)], ofItemAtPath: file.path)
        try store.update { try $0.delete(id, now: self.now) }
        XCTAssertEqual(trashNames(temp.url), ["20260301-140000 Old idea.md"])

        _ = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        XCTAssertEqual(trashNames(temp.url), ["20260301-140000 Old idea.md"])
    }

    func testTheTrashIsEmptiedOfWhatWasPutThereMoreThan30DaysAgo() throws {
        let temp = TempDir()
        _ = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let trash = temp.url.appendingPathComponent(".todo-tracker/trash")
        try FileManager.default.createDirectory(at: trash, withIntermediateDirectories: true)
        for name in ["20260129-130000 Gone.md", "20260130-150000 Kept.md", "Not ours.md", "2026-bad Kept too.md"] {
            try Data("x".utf8).write(to: trash.appendingPathComponent(name))
            try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-90 * 86_400)], ofItemAtPath: trash.appendingPathComponent(name).path)
        }
        _ = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        XCTAssertEqual(trashNames(temp.url), ["2026-bad Kept too.md", "20260130-150000 Kept.md", "Not ours.md"])
    }

    func testAnOldBoardThatCantBeSavedChangesNothing() throws {
        let temp = TempDir()
        let vault = temp.url.appendingPathComponent("vault")
        let store = try VaultStore(root: vault, timeZone: tz, clock: { self.now })
        let board = TaskBoard()
        _ = try board.addTask(NewTask("From the old board"), now: now)
        let legacy = temp.url.appendingPathComponent("board.json")
        try BoardCodec.encode(board).write(to: legacy)

        // The Work folder can't be written to: saving fails part way.
        let work = vault.appendingPathComponent("Work").path
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: work)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: work) }
        XCTAssertThrowsError(try store.importBeside(legacyBoard: legacy))
        XCTAssertEqual(try store.read { $0.items.map(\.title) }, [])
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path), "the old board is only set aside once it's in")

        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: work)
        XCTAssertEqual(try store.importBeside(legacyBoard: legacy), 1)
        XCTAssertEqual(try store.read { $0.items.map(\.title) }, ["From the old board"])
    }

    func testASyncThatFailsPartWayIsUndone() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let text = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# Plan\n"
        try FileManager.default.createDirectory(at: temp.url.appendingPathComponent("Personal/Locked"), withIntermediateDirectories: true)
        let locked = temp.url.appendingPathComponent("Personal/Locked").path
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: locked)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked) }
        let ops = [SyncOp(.write, "Work/Plan.md", nil, content: Data(text.utf8)),
                   SyncOp(.write, "Personal/Locked/photo.png", nil, content: Data("png".utf8))]
        XCTAssertThrowsError(try store.applySync(ops))
        XCTAssertFalse(FileManager.default.fileExists(atPath: temp.url.appendingPathComponent("Work/Plan.md").path))
        XCTAssertEqual(try store.read { $0.items.count }, 0)
    }

    private let planText = "---\nid: 7b0c2f9e-0000-4000-8000-000000000001\nstatus: open\ncreated: 2026-01-05T09:00:00.000Z\n---\n# Plan\n"

    func testAFileThatCantBeReadIsNeverLeftOutOfSyncOrWrittenOver() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let file = temp.url.appendingPathComponent("Work/Plan.md")
        try Data(planText.utf8).write(to: file)
        XCTAssertEqual(try store.read { $0.items.map(\.title) }, ["Plan"])

        // Not downloaded / no permission: the task stays (its last good version), it's a problem, and sync waits.
        try FileManager.default.setAttributes([.posixPermissions: 0o000], ofItemAtPath: file.path)
        store.markDirty()
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: file.path) }
        XCTAssertEqual(try store.read { $0.items.map(\.title) }, ["Plan"])
        XCTAssertEqual(store.problems.map(\.path), ["Work/Plan.md"])
        XCTAssertThrowsError(try store.readSync()) { XCTAssertTrue($0 is SyncNotArrived, "\($0)") }
        XCTAssertTrue(store.isPresent("Work/Plan.md"))

        // A change to it is refused (never a skeleton over the real file); other tasks still save.
        XCTAssertThrowsError(try store.update { try $0.update($0.items[0].id, title: "Plan B", now: self.now) })
        try store.update { _ = try $0.addTask(NewTask("Other"), now: self.now) }
        try FileManager.default.setAttributes([.posixPermissions: 0o644], ofItemAtPath: file.path)
        XCTAssertEqual(try String(contentsOf: file, encoding: .utf8), planText)
    }

    func testAnICloudPlaceholderStopsTheSyncAndCountsAsThere() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        try Data().write(to: temp.url.appendingPathComponent("Work/.Plan.md.icloud"))
        XCTAssertTrue(store.isPresent("Work/Plan.md"))
        XCTAssertThrowsError(try store.readSync()) { XCTAssertTrue($0 is SyncNotArrived, "\($0)") }
    }

    func testAnEarlierBoardSetAsideIsNeverReplaced() throws {
        let temp = TempDir()
        let legacy = temp.url.appendingPathComponent("board.json")
        try Data("earlier".utf8).write(to: temp.url.appendingPathComponent("board.json.migrated"))
        let board = TaskBoard()
        _ = try board.addTask(NewTask("From before"), now: now)
        try BoardCodec.encode(board).write(to: legacy)
        let store = try VaultStore(root: temp.url.appendingPathComponent("vault"), timeZone: tz, clock: { self.now })
        XCTAssertEqual(try store.importBeside(legacyBoard: legacy), 1)
        XCTAssertEqual(try String(contentsOf: temp.url.appendingPathComponent("board.json.migrated"), encoding: .utf8), "earlier")
        XCTAssertTrue(FileManager.default.fileExists(atPath: legacy.path + ".migrated 2"))
    }

    func testTwoNamesTheFileSystemTreatsAsOneAreNeverBothWritten() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let ops = [SyncOp(.write, "Work/Caf\u{E9}.png", nil, content: Data("nfc".utf8)),
                   SyncOp(.write, "Work/Cafe\u{301}.png", nil, content: Data("nfd".utf8))]
        XCTAssertThrowsError(try store.applySync(ops)) { XCTAssertTrue($0 is SyncDataError, "\($0)") }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: temp.url.appendingPathComponent("Work").path), [])
    }

    func testANewFileNeverReplacesOneThatAppeared() throws {
        let temp = TempDir()
        let path = temp.url.appendingPathComponent("Work/new.txt").path
        try VaultFiles.writeAtomic(path, Data("first".utf8), exclusive: true)
        XCTAssertThrowsError(try VaultFiles.writeAtomic(path, Data("second".utf8), exclusive: true))
        XCTAssertEqual(try String(contentsOfFile: path, encoding: .utf8), "first")
        let other = temp.url.appendingPathComponent("Work/other.txt").path
        try Data("other".utf8).write(to: URL(fileURLWithPath: other))
        XCTAssertThrowsError(try VaultFiles.move(other, path))
        XCTAssertEqual(try String(contentsOfFile: path, encoding: .utf8), "first")
        // A case-only rename is still a rename.
        try VaultFiles.move(path, temp.url.appendingPathComponent("Work/NEW.txt").path)
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: temp.url.appendingPathComponent("Work").path).contains("NEW.txt"))
    }

    func testSettingsThatCameWithAFailedSyncAreTakenAwayAgain() throws {
        let temp = TempDir()
        let store = try VaultStore(root: temp.url, timeZone: tz, clock: { self.now })
        let config = temp.url.appendingPathComponent(".todo-tracker/config.json")
        try FileManager.default.removeItem(at: config)
        try FileManager.default.createDirectory(at: temp.url.appendingPathComponent("Personal/Locked"), withIntermediateDirectories: true)
        let locked = temp.url.appendingPathComponent("Personal/Locked").path
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: locked)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked) }
        let ops = [SyncOp(.setConfig, SyncKeys.configPath, nil, content: Data("{}\n".utf8)),
                   SyncOp(.write, "Personal/Locked/photo.png", nil, content: Data("png".utf8))]
        XCTAssertThrowsError(try store.applySync(ops))
        // The reload after undoing writes the settings it has again; never the half-applied ones.
        XCTAssertNotEqual(try? String(contentsOf: config, encoding: .utf8), "{}\n")
    }
}
