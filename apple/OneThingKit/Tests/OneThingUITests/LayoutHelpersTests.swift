import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// The small rules behind the iPhone, iPad and Mac layouts: what a row's swipe offers, what the timer strip shows, and
/// when Today has room for two columns.
final class LayoutHelpersTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testA_task_to_do_now_swipes_to_done_and_offers_later_and_more() {
        let actions = Presentation.rowActions(isDone: false, isWaiting: false)
        XCTAssertEqual(actions.leading, [.done])
        XCTAssertEqual(actions.trailing, [.later, .more])
    }

    func testA_waiting_task_swipes_to_bring_it_back_and_can_still_be_put_off() {
        let actions = Presentation.rowActions(isDone: false, isWaiting: true)
        XCTAssertEqual(actions.leading, [.bringBack])
        XCTAssertEqual(actions.trailing, [.later, .more])
    }

    func testA_finished_task_swipes_to_reopen_and_is_never_put_off() {
        let actions = Presentation.rowActions(isDone: true, isWaiting: false)
        XCTAssertEqual(actions.leading, [.reopen])
        XCTAssertEqual(actions.trailing, [.more])
    }

    func testRow_actions_have_short_names_and_symbols() {
        XCTAssertEqual(RowAction.done.title, "Done")
        XCTAssertEqual(RowAction.later.title, "Later")
        XCTAssertEqual(RowAction.bringBack.title, "Now")
        XCTAssertEqual(RowAction.more.symbol, "ellipsis.circle")
    }

    func testThe_timer_strip_is_hidden_when_nothing_runs_unless_asked_to_offer_focus() {
        XCTAssertNil(Presentation.timerStrip(tracking: false, focusPhase: .idle, showIdle: false))
        XCTAssertEqual(Presentation.timerStrip(tracking: false, focusPhase: .idle, showIdle: true),
                       TimerStripLayout(tracker: false, focus: .full))
    }

    func testThe_timer_strip_shows_the_tracker_and_the_focus_timer_together_when_both_run() {
        XCTAssertEqual(Presentation.timerStrip(tracking: true, focusPhase: .focus, showIdle: false),
                       TimerStripLayout(tracker: true, focus: .compact))
        XCTAssertEqual(Presentation.timerStrip(tracking: true, focusPhase: .shortBreak, showIdle: false),
                       TimerStripLayout(tracker: true, focus: .compact))
    }

    func testThe_timer_strip_shows_one_timer_in_full_when_only_it_runs() {
        XCTAssertEqual(Presentation.timerStrip(tracking: false, focusPhase: .focus, showIdle: false),
                       TimerStripLayout(tracker: false, focus: .full))
        XCTAssertEqual(Presentation.timerStrip(tracking: true, focusPhase: .idle, showIdle: false),
                       TimerStripLayout(tracker: true, focus: nil))
        // With the focus timer offered, it stays a small play button beside the tracker.
        XCTAssertEqual(Presentation.timerStrip(tracking: true, focusPhase: .idle, showIdle: true),
                       TimerStripLayout(tracker: true, focus: .compact))
    }

    func testThe_focus_timer_says_its_phase_and_its_task() {
        var timer = PomodoroTimer()
        XCTAssertEqual(Presentation.focusLabel(timer, taskTitle: "Write the doc"), "Focus timer")
        timer.startFocus(now: t0)
        XCTAssertEqual(Presentation.focusLabel(timer, taskTitle: nil), "Focus")
        XCTAssertEqual(Presentation.focusLabel(timer, taskTitle: "Write the doc"), "Focus · Write the doc")
    }

    func testToday_has_two_columns_only_when_there_is_room_for_both() {
        // An iPhone, an iPad in Slide Over or a narrow Split View: one column.
        XCTAssertEqual(Presentation.todayColumns(width: 390), 1)
        XCTAssertEqual(Presentation.todayColumns(width: 600), 1)
        // An iPad (with its sidebar) and a Mac window: two.
        XCTAssertEqual(Presentation.todayColumns(width: 700), 2)
        XCTAssertEqual(Presentation.todayColumns(width: 1100), 2)
    }

    func testTodays_side_column_stays_readable_and_leaves_the_most_room_to_do_now() {
        XCTAssertEqual(Presentation.todaySideWidth(width: 700), 280)
        XCTAssertEqual(Presentation.todaySideWidth(width: 900), 324)
        XCTAssertEqual(Presentation.todaySideWidth(width: 1600), 380)
        XCTAssertGreaterThan(700 - Presentation.todaySideWidth(width: 700), Presentation.todaySideWidth(width: 700))
    }
}
