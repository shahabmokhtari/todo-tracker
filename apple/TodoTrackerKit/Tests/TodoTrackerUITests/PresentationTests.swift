import XCTest
import TodoTrackerKit
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
}
