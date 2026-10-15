import XCTest
@testable import TodoTrackerKit

/// Repeat rules shared with C# and Kotlin (tests/fixtures/repeat.json): reading, next days, and moving a task on.
final class RecurrenceTests: XCTestCase {
    private func fixture() throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let file = root.appendingPathComponent("tests/fixtures/repeat.json")
        return try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as! [String: Any]
    }

    private func date(_ text: Any?) -> Date? { (text as? String).flatMap { BoardCodec.parseDate($0) } }

    func testRulesReadAndWriteLikeTheSharedFixture() throws {
        for c in try fixture()["parse"] as! [[String: Any]] {
            let text = c["text"] as! String
            if c["error"] != nil {
                XCTAssertNil(Recurrence.parseOrNil(text), text)
                XCTAssertThrowsError(try Recurrence.parse(text), text) { error in
                    XCTAssertTrue((error as! Recurrence.RuleError).message.contains("every day"))
                }
                continue
            }
            let canonical = c["canonical"] as! String
            XCTAssertEqual(try Recurrence.parse(text).description, canonical, text)
            XCTAssertEqual(try Recurrence.parse(canonical).description, canonical)
        }
    }

    func testNextDaysMatchTheSharedFixture() throws {
        for c in try fixture()["next"] as! [[String: Any]] {
            let rule = try Recurrence.parse(c["rule"] as? String)
            let expected = (c["next"] as? String).flatMap { LocalDay($0) }
            let next = rule.next(anchor: LocalDay(c["anchor"] as! String)!, after: LocalDay(c["after"] as! String)!)
            XCTAssertEqual(next, expected, "\(rule) after \(c["after"]!)")
        }
    }

    func testLocalDaysCountLikeTheCalendar() {
        XCTAssertEqual(LocalDay("1970-01-01")!.epochDay, 0)
        XCTAssertEqual(LocalDay("2026-01-05")!.weekday, 1)
        XCTAssertEqual(LocalDay(epochDay: LocalDay("2024-02-29")!.epochDay + 366), LocalDay("2025-03-01"))
        XCTAssertNil(LocalDay("2026-02-29"))
    }

    func testRepeatingTasksMoveOnLikeTheSharedFixture() throws {
        for c in try fixture()["roll"] as! [[String: Any]] {
            let name = c["name"] as! String
            let board = TaskBoard()
            board.timeZone = TimeZone(identifier: c["zone"] as! String)!
            let now = date(c["now"])!
            let item = try board.addTask(NewTask("Water the plants", deadline: date(c["due"])), now: now.addingTimeInterval(-30 * 86400))
            item.repeatRule = try Recurrence.parse(c["rule"] as? String)
            if let scheduled = date(c["scheduled"]) { item.nextActionAt = scheduled }

            try board.complete(item.id, now: now)

            let expect = c["expect"] as! [String: Any]
            XCTAssertEqual(item.isDone, expect["done"] as! Bool, name)
            if item.isDone { continue }
            XCTAssertEqual(item.deadline, date(expect["due"]), name)
            XCTAssertEqual(item.nextActionAt, date(expect["scheduled"]), name)
            XCTAssertEqual(item.repeatRule?.description, expect["rule"] as? String, name)
        }
    }

    func testDoneTodayCountsATaskThatMovedOnAndReopenPutsItBack() throws {
        let utc = TimeZone(identifier: "UTC")!
        let now = date("2026-01-05T10:00:00Z")!
        let due = date("2026-01-05T17:00:00Z")!
        let board = TaskBoard()
        board.timeZone = utc
        let item = try board.addTask(NewTask("Stand-up notes", deadline: due), now: now)
        item.repeatRule = try Recurrence.parse("every day")

        try board.complete(item.id, now: now)
        XCTAssertFalse(item.isDone)
        XCTAssertEqual(Agenda.doneToday(board, now: now.addingTimeInterval(60), timeZone: utc).map(\.id), [item.id])

        try board.reopen(item.id, now: now.addingTimeInterval(120))
        XCTAssertEqual(item.deadline, due)
        XCTAssertNil(item.nextActionAt)
        XCTAssertTrue(Agenda.doneToday(board, now: now.addingTimeInterval(180), timeZone: utc).isEmpty)
    }

    func testARepeatingChecklistMovesOnWhenItsLastStepIsDoneAndUndoPutsBackWhatItHad() throws {
        let now = date("2026-01-05T10:00:00Z")!
        let board = TaskBoard()
        board.timeZone = TimeZone(identifier: "UTC")!
        let review = try board.addTask(NewTask("Weekly review", deadline: date("2026-01-02T17:00:00Z")), now: now)
        review.repeatRule = try Recurrence.parse("every week")
        review.nextActionAt = date("2026-01-02T08:30:00Z")
        let steps = try board.addSteps(parentId: review.id, titles: ["Inbox zero", "Plan the week"], stepDelay: nil, now: now)

        try board.complete(steps[0].id, now: now)
        try board.complete(steps[1].id, now: now)
        XCTAssertFalse(review.isDone)
        XCTAssertEqual(review.deadline, date("2026-01-09T17:00:00Z"))
        XCTAssertEqual(review.repeatRule?.description, "every week on Friday")
        XCTAssertTrue(steps.allSatisfy { !$0.isDone })

        try board.reopen(review.id, now: now.addingTimeInterval(60))
        XCTAssertEqual(review.deadline, date("2026-01-02T17:00:00Z"))
        XCTAssertEqual(review.nextActionAt, date("2026-01-02T08:30:00Z"))
    }

    func testTheRuleSurvivesTheBoardFile() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Rent"), now: date("2026-01-05T10:00:00Z")!)
        item.repeatRule = try Recurrence.parse("every month on the last day")

        XCTAssertEqual(try BoardCodec.decode(BoardCodec.encode(board)).find(item.id)?.repeatRule?.description, "every month on the last day")

        item.deadline = date("2026-01-31T17:00:00Z")
        try board.complete(item.id, now: date("2026-01-31T10:00:00Z")!)
        XCTAssertEqual(try BoardCodec.decode(BoardCodec.encode(board)).find(item.id)?.repeatUndo, RepeatUndo(deadline: date("2026-01-31T17:00:00Z"), nextActionAt: nil))
    }
}
