import SwiftUI
import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

final class PresentationTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testChipsMatchTheOtherPlatforms() throws {
        let board = TaskBoard()
        let x = try board.addTask(NewTask("Roll out feature X", priority: .high), now: t0)
        let steps = try board.addSteps(parentId: x.id, titles: ["Ring 0", "Ring 1"], stepDelay: 86_400, now: t0)
        let doc = try board.addTask(NewTask("Overdue doc", deadline: t0.addingTimeInterval(-3600)), now: t0)
        try board.addNote(doc.id, text: "started", now: t0)
        try board.complete(steps[0].id, now: t0)

        let d = Agenda.build(board, now: t0)

        XCTAssertEqual(Presentation.chips(for: d.now[0], now: t0), [Chip("overdue 1h ago", .danger), Chip("1 note", .muted)])
        XCTAssertEqual(Presentation.chips(for: d.waiting[0], now: t0), [Chip("Step 2 of 2", .step), Chip("back in 1d", .info)])
    }

    func testEnergyShowsOnCardsAndTodayOnceThereIsSomethingToSay() throws {
        let board = TaskBoard()
        let utc = TimeZone(identifier: "UTC")!
        let doc = try board.addTask(NewTask("Design doc"), now: t0)
        XCTAssertNil(Presentation.energyLine(board, now: t0, timeZone: utc))
        try board.setEnergy(doc.id, level: 2, now: t0)
        // Planned in the other apps (the Plan view is on the desktop and the web).
        doc.plannedAt = t0
        doc.plannedMinutes = 90

        XCTAssertTrue(Presentation.chips(for: Agenda.build(board, now: t0).now[0], now: t0).contains(Chip("⚡⚡", .energy)))
        XCTAssertEqual(Presentation.energyLine(board, now: t0, timeZone: utc), "⚡ 3 of 10 planned today.")
    }

    func testACardsNotesAreNewestFirstWithWhoAndWhen() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Send the RU report"), now: t0)
        try board.addNote(task.id, text: "Pulled the numbers", now: t0)
        try board.addNote(task.id, text: "Include hot partition splitting", actor: .agent("copilot"), now: t0.addingTimeInterval(300))

        let notes = Presentation.notes(task, now: t0.addingTimeInterval(600))
        XCTAssertEqual(notes.map(\.text), ["Include hot partition splitting", "Pulled the numbers"])
        XCTAssertTrue(notes[0].meta.hasPrefix("Agent: copilot ·"), notes[0].meta)
        XCTAssertEqual(Presentation.notesToggle(count: 2, open: false), "2 notes ▾")
        XCTAssertEqual(Presentation.notesToggle(count: 1, open: false), "1 note ▾")
        XCTAssertEqual(Presentation.notesToggle(count: 2, open: true), "Hide notes ▴")
    }

    func testAWaitingTaskSaysWhyLikeTheWebApp() throws {
        let board = TaskBoard()
        let utc = TimeZone(identifier: "UTC")!
        let release = try board.addTask(NewTask("Release"), now: t0)
        let review = try board.addTask(NewTask("Review", parentId: release.id), now: t0)
        let other = try board.addTask(NewTask("Sign-off"), now: t0)
        XCTAssertNil(Presentation.waiting(review, now: t0, timeZone: utc), "not waiting: nothing to say")

        try board.scheduleNextAction(review.id, at: t0.addingTimeInterval(3 * 3600), now: t0)
        let own = try XCTUnwrap(Presentation.waiting(review, now: t0, timeZone: utc))
        XCTAssertEqual(own.text, "Snoozed until today 12:00")
        XCTAssertTrue(own.canBringBack)
        XCTAssertNil(own.heldBy)

        try board.clearNextAction(review.id, now: t0)
        try board.waitFor(release.id, after: other.id, now: t0)
        let parents = try XCTUnwrap(Presentation.waiting(review, now: t0, timeZone: utc))
        XCTAssertEqual(parents.text, "Its parent “Release” waits for “Sign-off”")
        XCTAssertFalse(parents.canBringBack, "Do now on the subtask alone wouldn't bring it back")
        XCTAssertTrue(parents.heldBy === release)

        try board.scheduleNextAction(review.id, at: t0.addingTimeInterval(3 * 3600), now: t0)
        XCTAssertEqual(Presentation.waiting(review, now: t0, timeZone: utc)?.text,
                       "Waits for “Sign-off” and snoozed until today 12:00; its parent “Release” waits too")
    }

    func testSubtasksAreListedAtEveryDepthAndStepWaitsAreOffered() throws {
        let board = TaskBoard()
        let x = try board.addTask(NewTask("X"), now: t0)
        let a = try board.addTask(NewTask("A", parentId: x.id), now: t0)
        try board.addTask(NewTask("A1", parentId: a.id), now: t0)
        try board.addTask(NewTask("B", parentId: x.id), now: t0)
        XCTAssertEqual(Presentation.subtaskRows(x).map { "\($0.depth)\($0.item.title)" }, ["0A", "1A1", "0B"])

        XCTAssertEqual(Presentation.stepDelayChoices(current: nil), [0, 1, 2, 4, 8, 12, 24, 48, 72, 168])
        XCTAssertEqual(Presentation.stepDelayChoices(current: 1.5 * 3600).prefix(3), [0, 1, 1.5], "an hour and a half set elsewhere is offered too")
        XCTAssertEqual(Presentation.hoursLabel(0), "No wait")
        XCTAssertEqual(Presentation.hoursLabel(1), "1 hour")
        XCTAssertEqual(Presentation.hoursLabel(48), "2 days")
        XCTAssertEqual(Presentation.hoursLabel(1.5), "1.5 hours")
    }

    func testSummaryAndGreeting() throws {
        let board = TaskBoard()
        XCTAssertEqual(Presentation.summary(Agenda.build(board, now: t0)), "All clear. Nothing needs you right now.")
        let a = try board.addTask(NewTask("A"), now: t0)
        try board.addTask(NewTask("B"), now: t0)
        try board.addReminder(a.id, dueAt: t0, message: "go", now: t0)
        let c = try board.addTask(NewTask("C"), now: t0)
        try board.scheduleNextAction(c.id, at: t0.addingTimeInterval(3600), now: t0)
        XCTAssertEqual(Presentation.summary(Agenda.build(board, now: t0)), "2 things to do now · 1 reminder · 1 waiting.")

        var utc = Calendar(identifier: .gregorian)
        utc.timeZone = TimeZone(identifier: "UTC")!
        XCTAssertEqual(Presentation.greeting(t0, calendar: utc), "Good morning")
        XCTAssertEqual(Presentation.greeting(t0.addingTimeInterval(-7 * 3600), calendar: utc), "Still up?")
    }

    func testPomodoroFractionAndWorkstreams() throws {
        var timer = PomodoroTimer()
        XCTAssertEqual(Presentation.pomodoroFraction(timer, now: t0), 0)
        timer.startFocus(now: t0)
        XCTAssertEqual(Presentation.pomodoroFraction(timer, now: t0.addingTimeInterval(300)), 0.2, accuracy: 0.001)

        let board = TaskBoard()
        let x = try board.addTask(NewTask("X"), now: t0)
        try board.addTask(NewTask("Step", parentId: x.id), now: t0)
        try board.addTask(NewTask("Single"), now: t0)
        XCTAssertEqual(Presentation.workstreams(Agenda.build(board, now: t0)).map(\.item.title), ["X"])
    }

    /// Renders the dashboard (light and dark) to PNGs for visual review; CI uploads them as an artifact.
    @MainActor
    func testRenderDashboardSnapshots() throws {
        let model = BoardModel(store: nil)
        try DemoBoard.seed(model.board, now: Date())
        model.refresh()

        for dark in [false, true] {
            let data = try XCTUnwrap(SnapshotRenderer.png(model: model, dark: dark), "rendering produced no image")
            XCTAssertGreaterThan(data.count, 10_000)
            if let dir = ProcessInfo.processInfo.environment["SNAPSHOT_DIR"] {
                try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
                try data.write(to: URL(fileURLWithPath: dir).appendingPathComponent(dark ? "swiftui-dark.png" : "swiftui-light.png"))
            }
        }
    }

    func testAppearanceChoiceMatchesTheOtherApps() {
        // Same values as the Windows and web apps' theme setting.
        XCTAssertEqual(AppTheme.allCases.map(\.rawValue), ["system", "light", "dark"])
        XCTAssertEqual(AppTheme.named("dark").colorScheme, .dark)
        XCTAssertEqual(AppTheme.named("light").colorScheme, .light)
        XCTAssertNil(AppTheme.named("system").colorScheme)
        // Nothing chosen yet, or a value from a newer version: as the system is.
        XCTAssertEqual(AppTheme.named(nil), .system)
        XCTAssertEqual(AppTheme.named("sepia"), .system)
    }

    func testTimeReadsLikeTheWebApp() {
        // Same as wwwroot/js/timefmt.js (duration and clock).
        XCTAssertEqual(Presentation.duration(45), "45 s")
        XCTAssertEqual(Presentation.duration(25 * 60), "25 min")
        XCTAssertEqual(Presentation.duration(65 * 60), "1 h 05 min")
        XCTAssertEqual(Presentation.duration(12 * 3600), "12 h")
        XCTAssertEqual(Presentation.clock(247), "4:07")
        XCTAssertEqual(Presentation.clock(3847), "1:04:07")
    }
}