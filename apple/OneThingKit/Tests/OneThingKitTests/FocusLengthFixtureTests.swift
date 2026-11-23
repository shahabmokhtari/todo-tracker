import XCTest
@testable import OneThingKit

/// tests/fixtures/focus-lengths.json: the focus timer's limits, the same in C# (FocusLengthFixtureTests), Swift and
/// Kotlin. A refused change changes nothing; an allowed one adds the same line to the timeline, by whoever made it.
final class FocusLengthFixtureTests: XCTestCase {
    private let fields = ["focusMinutes", "shortBreakMinutes", "longBreakMinutes", "focusesBeforeLongBreak"]

    private func values(_ s: PomodoroSettings) -> [Int] { [s.focusMinutes, s.shortBreakMinutes, s.longBreakMinutes, s.focusesBeforeLongBreak] }

    func testTheFocusTimersLengthsAreLimitedAsTheSharedFixtureSays() throws {
        let f = try Fixtures.json("focus-lengths.json")
        let board = TaskBoard()
        let start = try XCTUnwrap(f["start"] as? [String: Int])
        XCTAssertEqual(values(board.pomodoro.settings), fields.map { start[$0]! })
        let limits = try XCTUnwrap(f["limits"] as? [String: [Int]])
        XCTAssertEqual(limits["focusMinutes"], [1, TaskBoard.maxFocusMinutes])
        XCTAssertEqual(limits["shortBreakMinutes"], [1, TaskBoard.maxShortBreakMinutes])
        XCTAssertEqual(limits["longBreakMinutes"], [1, TaskBoard.maxLongBreakMinutes])
        XCTAssertEqual(limits["focusesBeforeLongBreak"], [1, TaskBoard.maxFocusesBeforeLongBreak])
        let now = Date(timeIntervalSince1970: 1_767_603_600)
        for step in try XCTUnwrap(f["steps"] as? [[String: Any]]) {
            let asked = fields.map { (step["set"] as! [String: Int])[$0]! }
            let before = board.activity.count
            let change = {
                try board.setFocusLengths(focusMinutes: asked[0], shortBreakMinutes: asked[1], longBreakMinutes: asked[2],
                                          focusesBeforeLongBreak: asked[3], actor: .agent("claude"), now: now)
            }
            if step["ok"] as? Bool == true {
                try change()
                XCTAssertEqual(board.activity.count, before + 1)
                XCTAssertEqual(board.activity.last?.summary, step["summary"] as? String)
                XCTAssertEqual(board.activity.last?.actor, .agent("claude"))
            } else {
                XCTAssertThrowsError(try change(), "\(asked)")
                XCTAssertEqual(board.activity.count, before, "a refused change changes nothing")
            }
            XCTAssertEqual(values(board.pomodoro.settings), fields.map { (step["settings"] as! [String: Int])[$0]! }, "\(asked)")
        }
    }
}
