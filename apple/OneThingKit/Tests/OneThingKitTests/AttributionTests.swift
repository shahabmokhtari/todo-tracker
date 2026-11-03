import XCTest
@testable import OneThingKit

/// Every change says who made it: arranging, energy, timers and focus land on the timeline with their actor (as C#'s
/// AttributionTests).
final class AttributionTests: XCTestCase {
    private let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!
    private let bot = Actor.agent("helper")
    private let board = TaskBoard()

    private func at(_ minutes: Double) -> Date { t0.addingTimeInterval(minutes * 60) }
    private func task(_ title: String, parent: UUID? = nil) throws -> WorkItem { try board.addTask(NewTask(title, parentId: parent), now: t0) }
    private var last: ActivityEntry { board.activity.last! }

    func testArrangingDoNowSaysWhoArrangedIt() throws {
        let a = try task("A")
        let b = try task("B")
        try board.arrangeNow([b.id, a.id], actor: bot, now: at(1))
        XCTAssertEqual(last.actor, bot)
        XCTAssertEqual(last.kind, "moved")
        XCTAssertEqual(last.summary, "Arranged Do now (\"B\" first)")
    }

    func testReorderingSiblingsSaysWhoMovedTheTask() throws {
        let trip = try task("Trip")
        let flights = try task("Flights", parent: trip.id)
        let visa = try task("Visa", parent: trip.id)
        try board.reorder(visa.id, before: flights.id, actor: bot, now: at(1))
        XCTAssertEqual(last.actor, bot)
        XCTAssertEqual(last.itemId, visa.id)
        XCTAssertEqual(last.summary, "Moved \"Visa\" before \"Flights\"")
        try board.reorderAfter(visa.id, after: flights.id, actor: .user, now: at(2))
        XCTAssertEqual(last.actor, .user)
        XCTAssertEqual(last.summary, "Moved \"Visa\" after \"Flights\"")
    }

    func testChangingADaysEnergySaysWhoChangedIt() throws {
        try board.setEnergyPerDay(6, actor: bot, now: t0)
        XCTAssertEqual(last.actor, bot)
        XCTAssertEqual(last.itemId, TaskBoard.boardScopeId)
        XCTAssertEqual(last.summary, "Set a day's energy to 6 ⚡")
    }

    func testStoppingTheTimerSaysWhoStoppedItAndNoTimerChangesNothing() throws {
        let item = try task("Write report")
        try board.startTimer(item.id, now: t0)
        board.stopTimer(actor: bot, now: at(25))
        XCTAssertEqual(last.actor, bot)
        XCTAssertEqual(last.itemId, item.id)
        XCTAssertEqual(last.summary, "Stopped the timer on \"Write report\" (25 min)")
        let count = board.activity.count
        XCTAssertNil(board.stopTimer(actor: bot, now: at(30)))
        XCTAssertEqual(board.activity.count, count)
    }

    func testPausingResumingAndEndingFocusSayWhoDidIt() throws {
        let item = try task("Deep work")
        try board.startFocus(item.id, now: t0)
        board.pauseFocus(actor: bot, now: at(5))
        XCTAssertEqual(last.summary, "Paused focus on \"Deep work\"")
        XCTAssertEqual(last.actor, bot)
        board.resumeFocus(actor: .user, now: at(6))
        XCTAssertEqual(last.summary, "Resumed focus on \"Deep work\"")
        board.skipFocus(actor: bot, now: at(10))
        XCTAssertEqual(last.summary, "Ended focus early on \"Deep work\"")
        try board.startFocus(item.id, now: at(20))
        board.resetFocus(actor: bot, now: at(22))
        XCTAssertEqual(last.summary, "Stopped focus on \"Deep work\"")
        XCTAssertEqual(last.actor, bot)
    }

    func testAForgottenTimerIsPutDownToTheApp() throws {
        let item = try task("Forgot me")
        try board.startTimer(item.id, now: t0)
        XCTAssertEqual(board.closeForgottenTimers(now: at(26 * 60)), 1)
        XCTAssertEqual(last.actor, .system)
        XCTAssertEqual(last.itemId, item.id)
        XCTAssertEqual(last.summary, "Stopped a timer left running on \"Forgot me\" (counted 12 h 00 min)")
    }
}