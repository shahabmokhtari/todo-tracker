import Foundation
import XCTest
@testable import TodoTrackerKit

/// tests/fixtures/energy.json: a day's energy, the same in C# (Energy.cs), Swift and Kotlin.
final class EnergyTests: XCTestCase {
    private func fixture() throws -> [String: Any] {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let data = try Data(contentsOf: root.appendingPathComponent("tests/fixtures/energy.json"))
        return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func testADayTakesWhatTheSharedFixtureSays() throws {
        let f = try fixture()
        let board = try BoardCodec.decode(try JSONSerialization.data(withJSONObject: try XCTUnwrap(f["board"])))
        let zone = try XCTUnwrap(TimeZone(identifier: try XCTUnwrap(f["zone"] as? String)))
        XCTAssertEqual(board.dailyEnergy, f["perDay"] as? Int)
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = zone
        for day in try XCTUnwrap(f["days"] as? [[String: Any]]) {
            let parts = try XCTUnwrap(day["day"] as? String).split(separator: "-").compactMap { Int($0) }
            let noon = try XCTUnwrap(calendar.date(from: DateComponents(year: parts[0], month: parts[1], day: parts[2], hour: 12)))
            XCTAssertEqual(Energy.planned(on: noon, in: zone, board: board), try XCTUnwrap(day["planned"] as? Double), "\(day)")
        }
    }

    func testATaskTakesOneToThreeAnHourAndTheDayHasTenUnlessSet() throws {
        let board = TaskBoard()
        let now = Date()
        let task = try board.addTask(NewTask("Deep work"), now: now)
        XCTAssertEqual(Energy.of(task), 1)
        try board.setEnergy(task.id, level: 3, now: now)
        XCTAssertEqual(Energy.of(task), 3)
        XCTAssertThrowsError(try board.setEnergy(task.id, level: 4, now: now))
        XCTAssertEqual(board.dailyEnergy, 10)
        try board.setEnergyPerDay(6)
        let again = try BoardCodec.decode(try BoardCodec.encode(board))
        XCTAssertEqual(again.dailyEnergy, 6)
        XCTAssertEqual(again.find(task.id)?.energy, 3)
        XCTAssertThrowsError(try board.setEnergyPerDay(0))
    }

    func testTheLineReadsLikeTheOtherApps() {
        XCTAssertEqual(Energy.line(planned: 4.5, perDay: 10), "⚡ 4.5 of 10 planned today.")
        XCTAssertEqual(Energy.line(planned: 6, perDay: 4), "⚡ 6 of 4 planned today: 2 too much.")
    }
}
