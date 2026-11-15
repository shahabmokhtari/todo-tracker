import XCTest
@testable import OneThingKit
@testable import OneThingTV

/// What the Apple TV shows on its Now screen, its Board and Done, and what the Siri Remote's buttons do.
final class TVScreenTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private let utc = TimeZone(identifier: "UTC")!

    private func board(_ titles: [String]) throws -> TaskBoard {
        let board = TaskBoard()
        for title in titles { try board.addTask(NewTask(title), now: now.addingTimeInterval(-3600)) }
        return board
    }

    func testTheHeroIsTheOneThingToDoNowAndUpNextIsWhatFollows() throws {
        let board = try board(["One", "Two", "Three"])
        let screen = TVScreen.build(board, now: now, timeZone: utc)
        let expected = Agenda.build(board, now: now)
        XCTAssertEqual(screen.hero?.id, expected.focus?.item.id)
        XCTAssertFalse(screen.upNext.contains { $0.id == screen.hero?.id }, "the hero isn't repeated in Up next")
        XCTAssertEqual(screen.upNext.map(\.title), expected.now.map(\.item.title).filter { $0 != screen.hero?.title })
    }

    func testUpNextShowsAtMostFiveTasks() throws {
        let board = try board((1...9).map { "Task \($0)" })
        let screen = TVScreen.build(board, now: now, timeZone: utc)
        XCTAssertEqual(screen.upNext.count, 5)
        XCTAssertEqual(TVScreen.build(board, now: now, timeZone: utc, upNextLimit: 3).upNext.count, 3)
    }

    func testTheHeroSaysItsGroupAndColor() throws {
        let board = TaskBoard()
        let group = try board.addGroup(name: "Garden", color: "#10b981", now: now)
        try board.addTask(NewTask("Water the tomatoes", groupId: group.id), now: now)
        let hero = try XCTUnwrap(TVScreen.build(board, now: now, timeZone: utc).hero)
        XCTAssertEqual(hero.group, "Garden")
        XCTAssertEqual(hero.groupColor, "#10b981")
    }

    func testTheHeroSaysWhenItsDueOrOverdue() throws {
        let board = TaskBoard()
        try board.addTask(NewTask("Send the invoice", deadline: now.addingTimeInterval(3 * 3600)), now: now)
        XCTAssertEqual(TVScreen.build(board, now: now, timeZone: utc).hero?.when, TVWhen(text: "Due in 3h", urgent: false))
        let late = TaskBoard()
        try late.addTask(NewTask("Pay the bill", deadline: now.addingTimeInterval(-2 * 3600)), now: now.addingTimeInterval(-86_400))
        XCTAssertEqual(TVScreen.build(late, now: now, timeZone: utc).hero?.when, TVWhen(text: "Overdue · 2h ago", urgent: true))
    }

    func testADueReminderIsSaidOnTheHero() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Reply to the review"), now: now)
        try board.addReminder(task.id, dueAt: now.addingTimeInterval(-60), message: "Review closes at 5 pm", now: now.addingTimeInterval(-120))
        let hero = try XCTUnwrap(TVScreen.build(board, now: now, timeZone: utc).hero)
        XCTAssertEqual(hero.reminder, "Review closes at 5 pm")
    }

    func testNothingToDoSaysSoKindly() {
        let screen = TVScreen.build(TaskBoard(), now: now, timeZone: utc)
        XCTAssertNil(screen.hero)
        XCTAssertTrue(screen.upNext.isEmpty)
        XCTAssertEqual(screen.emptyMessage, "Nothing needs you right now. Enjoy it.")
    }

    func testDoneTodayCountsAndCheers() throws {
        let board = try board(["One", "Two", "Three"])
        XCTAssertEqual(TVScreen.build(board, now: now, timeZone: utc).doneToday, 0)
        XCTAssertNil(TVScreen.build(board, now: now, timeZone: utc).cheer)
        try board.complete(board.items[0].id, now: now.addingTimeInterval(-60))
        try board.complete(board.items[1].id, now: now.addingTimeInterval(-30))
        let screen = TVScreen.build(board, now: now, timeZone: utc)
        XCTAssertEqual(screen.doneToday, 2)
        XCTAssertEqual(screen.cheer, Agenda.cheer(2))
        XCTAssertEqual(screen.doneTitles, ["Two", "One"], "newest first")
    }

    func testTheFocusTimerShowsWhileItRunsAndWhenPaused() throws {
        let board = try board(["Write the report"])
        XCTAssertNil(TVScreen.build(board, now: now, timeZone: utc).focus, "no timer shown when it isn't on")
        let task = board.items[0]
        try board.startFocus(task.id, now: now.addingTimeInterval(-5 * 60))
        let running = try XCTUnwrap(TVScreen.build(board, now: now, timeZone: utc).focus)
        XCTAssertEqual(running.time, "20:00")
        XCTAssertEqual(running.label, "Focus")
        XCTAssertEqual(running.taskTitle, "Write the report")
        XCTAssertFalse(running.isPaused)
        XCTAssertEqual(running.fraction, 0.2, accuracy: 0.001)
        board.pauseFocus(now: now)
        let paused = try XCTUnwrap(TVScreen.build(board, now: now.addingTimeInterval(600), timeZone: utc).focus)
        XCTAssertTrue(paused.isPaused)
        XCTAssertEqual(paused.time, "20:00", "paused: the time stands still")
    }

    func testTheTimeTrackerShowsTheTaskBeingTimed() throws {
        let board = try board(["Fix the login"])
        XCTAssertNil(TVScreen.build(board, now: now, timeZone: utc).tracker)
        _ = try board.startTimer(board.items[0].id, now: now.addingTimeInterval(-(40 * 60 + 5)))
        let tracker = try XCTUnwrap(TVScreen.build(board, now: now, timeZone: utc).tracker)
        XCTAssertEqual(tracker.title, "Fix the login")
        XCTAssertEqual(tracker.elapsed, "40:05")
    }

    func testPlayPauseStartsPausesAndResumesTheFocusTimer() throws {
        let board = try board(["One"])
        let hero = board.items[0].id
        XCTAssertEqual(TVRemote.playPause(board.pomodoro, hero: hero), .start(hero))
        try board.startFocus(hero, now: now)
        XCTAssertEqual(TVRemote.playPause(board.pomodoro, hero: hero), .pause)
        board.pauseFocus(now: now)
        XCTAssertEqual(TVRemote.playPause(board.pomodoro, hero: hero), .resume)
        XCTAssertEqual(TVRemote.playPause(PomodoroTimer(), hero: nil), .start(nil), "nothing to do: a focus session on its own")
    }

    func testTheBoardHasFourReadOnlyColumnsInOrder() throws {
        let board = try board(["Inbox task", "Next task", "Doing task", "Done task"])
        try board.setStage(board.items[1].id, .next, now: now)
        try board.setStage(board.items[2].id, .doing, now: now)
        try board.complete(board.items[3].id, now: now)
        let columns = TVBoard.columns(board, now: now)
        XCTAssertEqual(columns.map(\.title), ["Inbox", "Next", "Doing", "Done"])
        XCTAssertEqual(columns.map { $0.cards.map(\.title) }, [["Inbox task"], ["Next task"], ["Doing task"], ["Done task"]])
    }

    func testABoardCardSaysItsProgressAndDue() throws {
        let board = TaskBoard()
        let parent = try board.addTask(NewTask("Move house", deadline: now.addingTimeInterval(2 * 86_400 + 60)), now: now)
        let steps = try board.addSteps(parentId: parent.id, titles: ["Pack", "Drive"], stepDelay: nil, now: now)
        try board.complete(steps[0].id, now: now)
        let card = try XCTUnwrap(TVBoard.columns(board, now: now).flatMap(\.cards).first { $0.title == "Move house" })
        XCTAssertEqual(card.progress, "1 of 2 done")
        XCTAssertEqual(card.when, TVWhen(text: "Due in 2d", urgent: false))
    }

    func testDetailsListStepsAndNotesNewestFirst() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Plan the trip"), now: now)
        _ = try board.addSteps(parentId: task.id, titles: ["Book flights", "Find a hotel"], stepDelay: nil, now: now)
        try board.addNote(task.id, text: "Ask Sam about dates", now: now.addingTimeInterval(-600))
        try board.addNote(task.id, text: "Budget is 2000", actor: .agent("claude"), now: now.addingTimeInterval(-60))
        let details = TVDetails(board.items[0], board: board, now: now)
        XCTAssertEqual(details.steps.map(\.title), ["Book flights", "Find a hotel"])
        XCTAssertEqual(details.notes.map(\.text), ["Budget is 2000", "Ask Sam about dates"])
        XCTAssertEqual(details.notes.first?.meta, "Agent: claude · 1m ago")
    }

    func testMarkingAParentDoneAsksAboutItsOpenSubtasksFirst() throws {
        let board = TaskBoard()
        let trip = try board.addTask(NewTask("Plan the trip"), now: now)
        let steps = try board.addSteps(parentId: trip.id, titles: ["Book flights", "Find a hotel"], stepDelay: nil, now: now)

        let parent = TVCompletion.question(try XCTUnwrap(board.find(trip.id)))
        XCTAssertEqual(parent.title, "Also mark the open subtasks as done?")
        XCTAssertEqual(parent.message, "“Plan the trip” has 2 open subtasks. They’re marked done with it.")
        XCTAssertEqual(parent.button, "Mark all done")

        let step = TVCompletion.question(try XCTUnwrap(board.find(steps[0].id)))
        XCTAssertEqual(step.title, "Mark “Book flights” done?")
        XCTAssertEqual(step.button, "Mark done")

        try board.complete(steps[0].id, now: now)
        XCTAssertEqual(TVCompletion.question(try XCTUnwrap(board.find(trip.id))).message, "“Plan the trip” has 1 open subtask. It’s marked done with it.")
        try board.complete(steps[1].id, now: now)
        XCTAssertEqual(TVCompletion.question(try XCTUnwrap(board.find(trip.id))).title, "Mark “Plan the trip” done?", "no open subtasks left: the plain question")
    }

    func testACelebrationIsOnlyForNewlyDoneTasks() {
        XCTAssertTrue(TVCelebration.shouldCelebrate(from: 2, to: 3))
        XCTAssertFalse(TVCelebration.shouldCelebrate(from: 3, to: 3))
        XCTAssertFalse(TVCelebration.shouldCelebrate(from: 3, to: 2), "reopened: nothing to celebrate")
        XCTAssertFalse(TVCelebration.shouldCelebrate(from: nil, to: 4), "the first time the screen shows: no party")
    }
}
