import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/export.json is what the app's server sends from /api/export (written and checked by the C# tests):
/// the Mac app shows that board when it uses the server, so it must read everything in it.
final class ExportFixtureTests: XCTestCase {
    private func fixture() throws -> Data {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        return try Data(contentsOf: root.appendingPathComponent("tests/fixtures/export.json"))
    }

    func testTheServersExportReads() throws {
        let board = try BoardCodec.decode(try fixture())

        let launch = try XCTUnwrap(board.items.first { $0.title == "Launch the site" })
        XCTAssertEqual(launch.priority, .high)
        XCTAssertEqual(launch.children.map(\.title), ["Write the copy", "Pick images"])
        XCTAssertTrue(launch.children[1].isDone)
        XCTAssertEqual(board.groups.first { $0.id == launch.groupId }?.name, "Launch team")
        // Notes from Obsidian and from connected apps.
        XCTAssertEqual(Set(launch.notes.map(\.author.kind)), [.vault, .connector])
        XCTAssertTrue(board.items.contains { $0.title == "Old task" && $0.isDone })
        // Snoozed until another task is done.
        XCTAssertEqual(board.items.first { $0.title == "Announce it" }?.waitingFor?.id, launch.id)
    }

    func testAuthorsFromANewerVersionStillRead() throws {
        let json = #"{"kind":"robot","name":"R2"}"#.data(using: .utf8)!
        XCTAssertEqual(try JSONDecoder().decode(Actor.self, from: json).kind, .system)
    }

    func testTheExportMakesAnAgenda() throws {
        let board = try BoardCodec.decode(try fixture())
        let agenda = Agenda.build(board, now: BoardCodec.parseDate("2026-01-05T09:30:00.000Z")!)
        XCTAssertFalse(agenda.now.contains { $0.item.title == "Old task" })
        XCTAssertEqual(agenda.waiting.first { $0.item.title == "Announce it" }?.waitingFor?.title, "Launch the site")
    }
}
