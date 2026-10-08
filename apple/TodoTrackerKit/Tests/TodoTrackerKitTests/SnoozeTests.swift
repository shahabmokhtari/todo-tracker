import XCTest
@testable import TodoTrackerKit

/// Snooze choices and wording shared with C# and the web app (tests/fixtures/snooze.json), and "after another task".
final class SnoozeTests: XCTestCase {
    private func fixture() throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let file = root.appendingPathComponent("tests/fixtures/snooze.json")
        return try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as! [String: Any]
    }

    private func date(_ text: Any?) -> Date { BoardCodec.parseDate(text as! String)! }

    func testChoicesMatchTheSharedFixture() throws {
        let cases = try fixture()["choices"] as! [[String: Any]]
        XCTAssertGreaterThanOrEqual(cases.count, 4)
        for c in cases {
            let zone = TimeZone(identifier: c["zone"] as! String)!
            let expected = (c["expect"] as! [[String: Any]]).map { "\($0["id"]!) \($0["label"]!) \(date($0["at"]).timeIntervalSince1970)" }
            let actual = Snooze.choices(now: date(c["now"]), timeZone: zone).map { "\($0.id) \($0.label) \($0.at.timeIntervalSince1970)" }
            XCTAssertEqual(actual, expected, c["name"] as! String)
        }
    }

    func testDescribeMatchesTheSharedFixture() throws {
        for c in try fixture()["describe"] as! [[String: Any]] {
            let zone = TimeZone(identifier: c["zone"] as! String)!
            XCTAssertEqual(Snooze.describe(date(c["at"]), now: date(c["now"]), timeZone: zone), c["text"] as! String)
        }
    }

    private let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testWaitingForAnotherTaskUntilItIsDone() throws {
        let board = TaskBoard()
        let keys = try board.addTask(NewTask("Get the keys"), now: t0)
        let move = try board.addTask(NewTask("Move in"), now: t0)
        try board.scheduleNextAction(move.id, at: t0.addingTimeInterval(3600), notify: true, now: t0)

        try board.waitFor(move.id, after: keys.id, now: t0)

        XCTAssertNil(move.nextActionAt, "waiting for a task replaces a snooze until a time")
        XCTAssertEqual(Agenda.state(of: move, now: t0), .waiting)
        let dashboard = Agenda.build(board, now: t0)
        XCTAssertEqual(dashboard.waiting.map(\.item.id), [move.id])
        XCTAssertEqual(dashboard.waiting.first?.waitingFor?.id, keys.id)

        try board.complete(keys.id, now: t0.addingTimeInterval(60))

        XCTAssertNil(move.afterId)
        let after = Agenda.build(board, now: t0.addingTimeInterval(60))
        XCTAssertEqual(after.focus?.item.id, move.id)
        XCTAssertTrue(after.focus?.needsAttention ?? false)
        XCTAssertEqual(after.focus?.dueReminder?.message, "\"Get the keys\" is done: back to \"Move in\"")
    }

    func testWaitForRefusesItselfItsOwnPartsAndCircles() throws {
        let board = TaskBoard()
        let a = try board.addTask(NewTask("A"), now: t0)
        let child = try board.addTask(NewTask("A child", parentId: a.id), now: t0)
        let b = try board.addTask(NewTask("B"), now: t0)
        let done = try board.addTask(NewTask("Done"), now: t0)
        try board.complete(done.id, now: t0)
        try board.waitFor(b.id, after: a.id, now: t0)

        XCTAssertThrowsError(try board.waitFor(a.id, after: a.id, now: t0))
        XCTAssertThrowsError(try board.waitFor(a.id, after: child.id, now: t0))
        XCTAssertThrowsError(try board.waitFor(child.id, after: a.id, now: t0))
        XCTAssertThrowsError(try board.waitFor(a.id, after: b.id, now: t0))
        XCTAssertThrowsError(try board.waitFor(a.id, after: done.id, now: t0))
    }

    func testSnoozingOrBringingBackEndsTheWaitAndDeletingTheOtherTaskReleasesIt() throws {
        let board = TaskBoard()
        let a = try board.addTask(NewTask("A"), now: t0)
        let b = try board.addTask(NewTask("B"), now: t0)
        let c = try board.addTask(NewTask("C"), now: t0)
        try board.waitFor(b.id, after: a.id, now: t0)
        try board.waitFor(c.id, after: a.id, now: t0)

        try board.clearNextAction(b.id, now: t0)
        XCTAssertNil(b.afterId)
        try board.delete(a.id, now: t0)
        XCTAssertNil(c.afterId)
        XCTAssertEqual(Agenda.state(of: c, now: t0), .actionable)
    }

    func testTheWaitIsSavedAndReadBack() throws {
        let board = TaskBoard()
        let a = try board.addTask(NewTask("A"), now: t0)
        let b = try board.addTask(NewTask("B"), now: t0)
        try board.waitFor(b.id, after: a.id, now: t0)

        let again = try BoardCodec.decode(try BoardCodec.encode(board))

        XCTAssertEqual(try again.get(b.id).afterId, a.id)
        XCTAssertEqual(try again.get(b.id).waitingFor?.id, a.id)
    }
}
