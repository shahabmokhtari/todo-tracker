import Foundation
import XCTest
@testable import TodoTrackerKit

/// The Plan view's grid: the same days, hours and snapping as the web app (wwwroot/js/views/plan.js).
final class PlanGridTests: XCTestCase {
    private let zone = TimeZone(identifier: "Europe/Amsterdam")!
    private func day(_ text: String) -> LocalDay { LocalDay(text)! }

    func testAWeekStartsOnMondayOrSundayAndADayIsItself() {
        let wednesday = day("2026-10-28")
        XCTAssertEqual(PlanGrid.firstDay(wednesday, view: .week), day("2026-10-26"))
        XCTAssertEqual(PlanGrid.firstDay(wednesday, view: .week, weekStart: 7), day("2026-10-25"))
        XCTAssertEqual(PlanGrid.firstDay(day("2026-10-25"), view: .week, weekStart: 7), day("2026-10-25"))
        XCTAssertEqual(PlanGrid.firstDay(day("2026-11-01"), view: .week), day("2026-10-26"), "Sunday is the end of a Monday week")
        XCTAssertEqual(PlanGrid.firstDay(wednesday, view: .day), wednesday)
    }

    func testAWeekOfWorkHoursShowsTheWorkDaysAndTheFullDayShowsAll() {
        var settings = PlanSettings()
        XCTAssertEqual(PlanGrid.shownDays(from: day("2026-10-26"), settings: settings).map(\.description), ["2026-10-26", "2026-10-27", "2026-10-28", "2026-10-29", "2026-10-30"])
        settings.hours = .full
        XCTAssertEqual(PlanGrid.shownDays(from: day("2026-10-26"), settings: settings).count, 7)
        settings.hours = .work
        settings.view = .day
        XCTAssertEqual(PlanGrid.shownDays(from: day("2026-10-31"), settings: settings), [day("2026-10-31")], "a day is shown even when it's not a work day")
    }

    func testDroppingAndResizingSnapToAQuarterOfAnHour() {
        XCTAssertEqual(PlanGrid.snapped(9 * 60 + 7), 9 * 60)
        XCTAssertEqual(PlanGrid.snapped(9 * 60 + 7.5), 9 * 60 + 15)
        XCTAssertEqual(PlanGrid.snapped(-20), 0)
        XCTAssertEqual(PlanGrid.snapped(24 * 60 + 30), 24 * 60 - 15)
        XCTAssertEqual(PlanGrid.resized(30, by: 20), 45)
        XCTAssertEqual(PlanGrid.resized(30, by: -40), 15, "at least a quarter of an hour")
        XCTAssertEqual(PlanGrid.resized(600, by: 600), 720, "at most 12 hours")
    }

    func testABlockMovesByTheDragOnItsNewDaySnapped() {
        let start = Fixtures.date("2026-10-28T09:00:00Z") // 10:00 in Amsterdam
        XCTAssertEqual(PlanGrid.moved(start, byMinutes: 37, days: 1, timeZone: zone), Fixtures.date("2026-10-29T09:30:00Z"))
        // Across the change to winter time: the same wall-clock time on the new day.
        XCTAssertEqual(PlanGrid.moved(Fixtures.date("2026-10-23T08:00:00Z"), byMinutes: 0, days: 3, timeZone: zone), Fixtures.date("2026-10-26T09:00:00Z"))
    }

    func testABlockAcrossMidnightIsAPartOnEachDay() {
        let parts = PlanGrid.segments(start: Fixtures.date("2026-10-30T22:30:00Z"), minutes: 90, timeZone: zone) // Fri 23:30 to Sat 01:00
        XCTAssertEqual(parts, [
            PlanSegment(day: day("2026-10-30"), from: 23 * 60 + 30, to: 24 * 60, continues: false, isLast: false),
            PlanSegment(day: day("2026-10-31"), from: 0, to: 60, continues: true, isLast: true),
        ])
    }

    func testWorkHoursStretchToShowABlockOutsideThem() {
        let settings = PlanSettings()
        let early = PlanGrid.segments(start: Fixtures.date("2026-10-28T06:30:00Z"), minutes: 30, timeZone: zone) // 07:30
        let late = PlanGrid.segments(start: Fixtures.date("2026-10-29T17:00:00Z"), minutes: 45, timeZone: zone) // 18:00–18:45
        let shown = [day("2026-10-28"), day("2026-10-29")]
        XCTAssertTrue(PlanGrid.hours(settings: settings, segments: early + late, days: shown) == (7 * 60, 19 * 60))
        XCTAssertTrue(PlanGrid.hours(settings: settings, segments: early, days: [day("2026-10-27")]) == (9 * 60, 17 * 60), "only blocks on the days shown")
        var full = settings
        full.hours = .full
        XCTAssertTrue(PlanGrid.hours(settings: full, segments: [], days: shown) == (0, 24 * 60))
    }

    func testWorkHoursAreCheckedLikeTheServerDoes() {
        var settings = PlanSettings()
        XCTAssertNil(settings.problem)
        settings.workEnd = settings.workStart + 30
        XCTAssertNotNil(settings.problem)
        settings = PlanSettings()
        settings.workDays = []
        XCTAssertNotNil(settings.problem)
    }
}
