import XCTest
@testable import TodoTrackerKit

/// The break rules shared with the web app and Windows: tests/fixtures/breaks.json is checked by all three.
final class BreakRulesTests: XCTestCase {
    func testSharedBreakFixture() throws {
        var root = URL(fileURLWithPath: #filePath)
        for _ in 0..<5 { root.deleteLastPathComponent() }
        let file = root.appendingPathComponent("tests/fixtures/breaks.json")
        let cases = try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as! [[String: Any]]
        XCTAssertGreaterThanOrEqual(cases.count, 8, "expected break cases in \(file.path)")

        for c in cases {
            let name = c["name"] as! String
            let p = c["pomodoro"] as! [String: Any]
            let settings = PomodoroSettings(
                shortBreakMinutes: p["shortBreakMinutes"] as! Int,
                longBreakMinutes: p["longBreakMinutes"] as! Int,
                focusesBeforeLongBreak: p["focusesBeforeLongBreak"] as! Int)
            let now = BoardCodec.parseDate(c["now"] as! String)!
            let due = Breaks.due(
                phase: PomodoroPhase(rawValue: p["phase"] as! String)!,
                running: p["running"] as! Bool,
                endsAt: (p["endsAt"] as? String).flatMap(BoardCodec.parseDate),
                completedFocusCount: p["completedFocusCount"] as! Int,
                settings: settings,
                now: now)
            let dismissed = (c["dismissed"] as? String).flatMap(BoardCodec.parseDate)
            let shown = due.map { $0.until != dismissed } ?? false
            let expect = c["expect"] as! [String: Any]

            XCTAssertEqual(shown, expect["show"] as! Bool, "\(name): show")
            if shown, let due {
                XCTAssertEqual(due.until, BoardCodec.parseDate(expect["until"] as! String), "\(name): until")
                XCTAssertEqual(due.isLong, expect["long"] as! Bool, "\(name): long")
                XCTAssertEqual(due.tip, expect["tip"] as! String, "\(name): tip")
            }
        }
    }

    func testClockAndPausedTimers() {
        let start = Date(timeIntervalSince1970: 1_767_607_200) // 2026-01-05 10:00 UTC
        var timer = PomodoroTimer()
        timer.startFocus(now: start)
        XCTAssertNil(Breaks.due(timer, now: start.addingTimeInterval(60)))

        let end = start.addingTimeInterval(25 * 60)
        let due = Breaks.due(timer, now: end.addingTimeInterval(1))
        XCTAssertEqual(due?.until, end.addingTimeInterval(5 * 60))
        XCTAssertEqual(Breaks.clock(until: due!.until, now: end.addingTimeInterval(1)), "4:59")

        timer.pause(now: start.addingTimeInterval(60))
        XCTAssertNil(Breaks.due(timer, now: start.addingTimeInterval(3600)))
    }

    func testSkippingRightAfterFocusEndedSkipsThatBreak() throws {
        // The break shows the moment focus ends; Skip can come before the next tick.
        let start = Date(timeIntervalSince1970: 1_767_607_200)
        let board = TaskBoard()
        try board.startFocus(nil, now: start)
        board.skipFocus(now: start.addingTimeInterval(25 * 60 + 3))

        XCTAssertEqual(board.pomodoro.phase, .idle)
        XCTAssertEqual(board.pomodoro.completedFocusCount, 1)
    }
}
