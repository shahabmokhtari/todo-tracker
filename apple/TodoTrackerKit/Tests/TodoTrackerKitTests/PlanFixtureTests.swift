import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/plan.json (written by the C# core): what the Plan view shows for some days, and what planning a task
/// does. The same rules as PlanDays.cs and TaskBoard.Plan.cs.
final class PlanFixtureTests: XCTestCase {
    private func load() throws -> (fixture: [String: Any], board: TaskBoard, zone: TimeZone, now: Date) {
        let f = try Fixtures.json("plan.json")
        let board = try BoardCodec.decode(try JSONSerialization.data(withJSONObject: try XCTUnwrap(f["board"])))
        let zone = try XCTUnwrap(TimeZone(identifier: try XCTUnwrap(f["zone"] as? String)))
        board.timeZone = zone
        return (f, board, zone, Fixtures.date(try XCTUnwrap(f["now"] as? String)))
    }

    private func id(_ value: Any?) -> String? { (value as? String)?.lowercased() }
    private func text(_ id: UUID) -> String { id.uuidString.lowercased() }
    private func instant(_ date: Date?) -> String? { date.map { Self.formatter.string(from: $0) } }
    private static let formatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    func testWhileItsBlockIsOnItIsTheThingToDoNow() throws {
        let (f, board, _, now) = try load()
        XCTAssertEqual(Agenda.build(board, now: now).focus.map { text($0.item.id) }, id(f["focus"]))
    }

    func testTheDaysShowWhatTheSharedFixtureSays() throws {
        let (f, board, zone, now) = try load()
        for c in try XCTUnwrap(f["cases"] as? [[String: Any]]) {
            let from = try XCTUnwrap(LocalDay(c["from"] as? String ?? ""))
            let group = (c["group"] as? String).flatMap(UUID.init(uuidString:))
            let plan = PlanDays.build(board, from: from, days: try XCTUnwrap(c["days"] as? Int), timeZone: zone, now: now, groupId: group)
            let label = "\(c["from"]!) \(c["days"]!) \(c["group"] ?? "all")"

            let blocks = try XCTUnwrap(c["blocks"] as? [[String: Any]])
            XCTAssertEqual(plan.blocks.map { text($0.id) }, blocks.map { id($0["id"])! }, label)
            for (item, expected) in zip(plan.blocks, blocks) {
                XCTAssertEqual(instant(item.plannedAt), expected["start"] as? String, label)
                XCTAssertEqual(item.plannedLength, expected["minutes"] as? Int, label)
                XCTAssertEqual(item.isDone, expected["done"] as? Bool, label)
            }
            XCTAssertEqual(plan.deadlines.map { text($0.id) }, try XCTUnwrap(c["deadlines"] as? [String]).map { $0.lowercased() }, label)
            XCTAssertEqual(plan.unplanned.map { text($0.item.id) }, try XCTUnwrap(c["unplanned"] as? [String]).map { $0.lowercased() }, label)
            let later = try XCTUnwrap(c["later"] as? [[String: Any]])
            XCTAssertEqual(plan.later.map { text($0.item.id) }, later.map { id($0["id"])! }, label)
            for (entry, expected) in zip(plan.later, later) {
                XCTAssertEqual(instant(entry.wakeAt), expected["wakeAt"] as? String, label)
                XCTAssertEqual(entry.waitingFor.map { text($0.id) }, id(expected["waitingFor"]), label)
            }
            let energy = try XCTUnwrap(c["energy"] as? [String: Double])
            XCTAssertEqual(Dictionary(uniqueKeysWithValues: plan.energy.map { ($0.key.description, $0.value) }), energy, label)
        }
    }

    func testPlanningATaskDoesWhatTheSharedFixtureSays() throws {
        let (f, board, _, now) = try load()
        for step in try XCTUnwrap(f["plan"] as? [[String: Any]]) {
            let item = try XCTUnwrap(UUID(uuidString: try XCTUnwrap(step["id"] as? String)))
            do {
                if let start = step["start"] as? String {
                    try board.plan(item, start: Fixtures.date(start), minutes: step["minutes"] as? Int, now: now)
                } else {
                    try board.unplan(item, now: now)
                }
                XCTAssertNil(step["error"], "\(step)")
                let planned = try board.get(item)
                XCTAssertEqual(instant(planned.plannedAt), step["plannedAt"] as? String, "\(step)")
                XCTAssertEqual(planned.plannedMinutes, step["plannedMinutes"] as? Int, "\(step)")
            } catch let error as BoardError {
                switch (error, step["error"] as? String) {
                case (.invalid, "length"), (.conflict, "done"): break
                default: XCTFail("\(step): \(error)")
                }
            }
        }
    }

    func testPlanningSaysWhoAndWhenInTheTimeline() throws {
        let board = TaskBoard()
        board.timeZone = TimeZone(identifier: "Europe/Amsterdam")!
        let now = Fixtures.date("2026-10-28T10:15:00Z")
        let task = try board.addTask(NewTask("Deep work"), now: now)
        try board.plan(task.id, start: Fixtures.date("2026-10-29T08:00:00Z"), minutes: 90, actor: .agent("claude"), now: now)
        XCTAssertEqual(board.activity.last?.summary, "Planned \"Deep work\" Thu 29 Oct 09:00, 90 min")
        XCTAssertEqual(board.activity.last?.kind, "scheduled")
        XCTAssertEqual(board.activity.last?.actor, .agent("claude"))
        try board.unplan(task.id, now: now)
        XCTAssertEqual(board.activity.last?.summary, "Took \"Deep work\" off the plan")
        let again = try BoardCodec.decode(try BoardCodec.encode(board))
        XCTAssertNil(again.find(task.id)?.plannedAt)
    }
}
