import XCTest
@testable import TodoTrackerKit

/// Timers on tasks, and focus sessions timing their task: the same rules as TodoTracker.Core (TimeTrackingTests.cs).
final class TimeTrackingTests: XCTestCase {
    private let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!
    private func at(_ minutes: Double) -> Date { t0.addingTimeInterval(minutes * 60) }

    func testOneTimerRunsAtATime() throws {
        let board = TaskBoard()
        let a = try board.addTask(NewTask("A"), now: t0)
        let b = try board.addTask(NewTask("B"), now: t0)

        try board.startTimer(a.id, now: t0)
        try board.startTimer(b.id, now: at(10))

        XCTAssertEqual(a.timeSpent(at(30)), 600)
        XCTAssertEqual(board.runningTimer(at(30))?.item.id, b.id)
        board.stopTimer(now: at(30))
        XCTAssertNil(board.runningTimer())
        XCTAssertEqual(b.timeSpent(at(60)), 1200)
    }

    func testAFocusSessionIsTimedAndEndsExactlyWhenTheSessionDoes() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Write report"), now: t0)

        try board.startFocus(item.id, now: t0)
        XCTAssertEqual(board.runningTimer(at(1))?.entry.source, .focus)
        board.tickPomodoro(now: at(30))

        let entry = try XCTUnwrap(item.timeEntries.first)
        XCTAssertEqual(item.timeEntries.count, 1)
        XCTAssertEqual(entry.end, at(25))
        XCTAssertNil(board.runningTimer())
    }

    func testPausingFocusPausesTheTimerAndResumingStartsItAgain() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Write report"), now: t0)
        try board.startFocus(item.id, now: t0)

        board.pauseFocus(now: at(10))
        board.resumeFocus(now: at(20))
        board.resetFocus(now: at(30))

        XCTAssertEqual(item.timeEntries.map { $0.duration(at(60)) }, [600, 600])
        XCTAssertNil(board.runningTimer())
    }

    func testSkippingFocusStopsItsTimerButNotOneStartedByHandOnAnotherTask() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Write report"), now: t0)
        let other = try board.addTask(NewTask("Inbox zero"), now: t0)
        try board.startFocus(item.id, now: t0)
        board.skipFocus(now: at(5))
        XCTAssertNil(board.runningTimer())

        try board.startTimer(other.id, now: at(6))
        board.tickPomodoro(now: at(60)) // the break ends

        XCTAssertEqual(board.runningTimer()?.item.id, other.id)
    }

    func testStartNextFocusTimesTheSameTaskAgain() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Write report"), now: t0)
        try board.startFocus(item.id, now: t0)
        board.tickPomodoro(now: at(26))

        try board.startNextFocus(fallback: nil, now: at(27))

        XCTAssertEqual(item.timeEntries.count, 2)
        XCTAssertEqual(board.runningTimer()?.entry.source, .focus)
    }

    func testCompletingATaskStopsItsTimerAndADoneTaskCantBeTimed() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("A"), now: t0)
        try board.startTimer(item.id, now: t0)

        try board.complete(item.id, now: at(15))

        XCTAssertNil(board.runningTimer())
        XCTAssertEqual(item.timeSpent(at(60)), 900)
        XCTAssertThrowsError(try board.startTimer(item.id, now: at(20)))
    }

    func testAForgottenTimerCountsTwelveHoursAtMost() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("A"), now: t0)
        try board.startTimer(item.id, now: t0)

        XCTAssertNil(board.runningTimer(at(13 * 60)))
        XCTAssertEqual(item.timeSpent(at(30 * 60)), 12 * 3600)
    }

    func testAForgottenTimerIsClosedAndTheTaskCanBeTimedAgain() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("A"), now: t0)
        try board.startTimer(item.id, now: t0)

        try board.startTimer(item.id, now: at(13 * 60))
        XCTAssertEqual(item.timeEntries.count, 2)
        XCTAssertEqual(item.timeEntries[0].end, at(12 * 60))

        XCTAssertEqual(board.closeForgottenTimers(now: at(14 * 60)), 0)
        XCTAssertEqual(board.closeForgottenTimers(now: at(26 * 60)), 1)
        XCTAssertNil(board.runningTimer(at(26 * 60)))
    }

    func testDeletingASubtaskKeepsItsTimeOnTheTask() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Report"), now: t0)
        let sub = try board.addTask(NewTask("Charts", parentId: task.id), now: t0)
        try board.startTimer(sub.id, now: t0)

        try board.delete(sub.id, now: at(20))

        XCTAssertNil(board.runningTimer())
        XCTAssertEqual(task.timeSpent(at(60), includeSubtasks: false), 1200)
    }

    func testTimeIsSavedAndReadBack() throws {
        let board = TaskBoard()
        let item = try board.addTask(NewTask("A"), now: t0)
        try board.startFocus(item.id, now: t0)
        board.pauseFocus(now: at(10))
        try board.startTimer(item.id, now: at(11))

        let again = try BoardCodec.decode(try BoardCodec.encode(board))
        let entries = try again.get(item.id).timeEntries

        XCTAssertEqual(entries.map(\.source), [.focus, .manual])
        XCTAssertEqual(entries.first?.end, at(10))
        XCTAssertNil(entries.last?.end)
        XCTAssertEqual(again.runningTimer(at(12))?.item.id, item.id)
    }
}
