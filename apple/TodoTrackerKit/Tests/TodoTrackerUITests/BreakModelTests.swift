import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

@MainActor
final class BreakModelTests: XCTestCase {
    private func modelWithFocusEndedJustNow() throws -> BoardModel {
        UserDefaults.standard.removeObject(forKey: "fullScreenBreaks")
        let model = BoardModel(store: nil)
        try model.board.startFocus(nil, now: Date().addingTimeInterval(-(25 * 60 + 5)))
        model.refresh()
        return model
    }

    func testTheBreakShowsWhenFocusEndsAndTakingItHidesItForThatBreak() throws {
        let model = try modelWithFocusEndedJustNow()
        let prompt = try XCTUnwrap(model.breakPrompt)
        XCTAssertEqual(prompt.title, "Time for a break")

        model.takeBreak()
        XCTAssertNil(model.breakPrompt)
        XCTAssertNotEqual(model.board.pomodoro.phase, .idle) // the break itself goes on
    }

    func testSkippingEndsTheBreakAndCountsTheSession() throws {
        let model = try modelWithFocusEndedJustNow()

        model.skipBreak()

        XCTAssertNil(model.breakPrompt)
        XCTAssertEqual(model.board.pomodoro.phase, .idle)
        XCTAssertEqual(model.board.pomodoro.completedFocusCount, 1)
    }

    func testStartNextFocusFromTheBreak() throws {
        let model = try modelWithFocusEndedJustNow()
        XCTAssertNotNil(model.breakPrompt)

        model.startNextFocus()

        XCTAssertNil(model.breakPrompt)
        XCTAssertEqual(model.board.pomodoro.phase, .focus)
        XCTAssertEqual(model.board.pomodoro.completedFocusCount, 1)
    }

    func testFullScreenBreaksCanBeTurnedOff() throws {
        let model = try modelWithFocusEndedJustNow()
        model.fullScreenBreaks = false
        defer { UserDefaults.standard.removeObject(forKey: "fullScreenBreaks") }

        XCTAssertNil(model.breakPrompt)
    }
}
