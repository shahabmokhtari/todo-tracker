import XCTest
@testable import TodoTrackerKit

/// Changing an existing task from its details, groups, archiving and search: the same rules as the C# core
/// (TaskBoard.Update, UpdateGroup, DeleteGroup, Archive, TaskQuery's words).
final class TaskEditingTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testADeadlineIsSetAndClearedAndDetailsCanBeEmptiedAgain() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Send the report", details: "the Q3 numbers"), now: t0)
        let friday = t0.addingTimeInterval(4 * 86_400)

        try board.update(task.id, TaskChanges(deadline: .some(friday)), now: t0)
        XCTAssertEqual(task.deadline, friday)
        XCTAssertEqual(task.details, "the Q3 numbers", "fields not given stay as they are")

        try board.update(task.id, TaskChanges(details: "  ", deadline: .some(nil)), now: t0)
        XCTAssertNil(task.deadline)
        XCTAssertNil(task.details)
        XCTAssertEqual(board.activity.last?.kind, "updated")
    }

    func testStepsInOrderAndHoursBetweenStepsChangeOnAnExistingTask() throws {
        let board = TaskBoard()
        let rollout = try board.addTask(NewTask("Rollout"), now: t0)
        let first = try board.addTask(NewTask("Ring 0", parentId: rollout.id), now: t0)
        let second = try board.addTask(NewTask("Ring 1", parentId: rollout.id), now: t0)

        try board.update(rollout.id, TaskChanges(sequential: true, stepDelay: .some(2 * 3600)), now: t0)
        XCTAssertTrue(rollout.sequential)
        XCTAssertEqual(rollout.stepDelay, 2 * 3600)
        XCTAssertEqual(Agenda.state(of: second, now: t0), .locked)

        try board.complete(first.id, now: t0)
        XCTAssertEqual(second.nextActionAt, t0.addingTimeInterval(2 * 3600))

        try board.update(rollout.id, TaskChanges(sequential: false, stepDelay: .some(nil)), now: t0)
        XCTAssertFalse(rollout.sequential)
        XCTAssertNil(rollout.stepDelay)
        XCTAssertThrowsError(try board.update(rollout.id, TaskChanges(stepDelay: .some(30)), now: t0), "under a minute")
    }

    func testARepeatRuleInWordsIsReadAndAnEmptyOneStopsIt() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Water the plants"), now: t0)

        try board.update(task.id, TaskChanges(repeatRule: "every weekday"), now: t0)
        XCTAssertEqual(task.repeatRule?.description, "every weekday")

        XCTAssertThrowsError(try board.update(task.id, TaskChanges(title: "Renamed", repeatRule: "every blue moon"), now: t0)) { error in
            XCTAssertTrue((error as? BoardError)?.errorDescription?.contains("every weekday") == true, "says what it understands")
        }
        XCTAssertEqual(task.title, "Water the plants", "nothing changes when one field isn't valid")
        XCTAssertEqual(task.repeatRule?.description, "every weekday")

        try board.update(task.id, TaskChanges(repeatRule: " "), now: t0)
        XCTAssertNil(task.repeatRule)
    }

    func testOnlyAFinishedTaskIsArchivedAndReopeningBringsItBack() throws {
        let board = TaskBoard()
        let task = try board.addTask(NewTask("Tax return"), now: t0)
        let part = try board.addTask(NewTask("Receipts", parentId: task.id), now: t0)
        try board.addNote(task.id, text: "Filed online", now: t0)

        XCTAssertThrowsError(try board.archive(task.id, now: t0), "finish it first")
        try board.complete(task.id, now: t0)
        XCTAssertThrowsError(try board.archive(part.id, now: t0), "subtasks are archived with their task")

        try board.archive(task.id, now: t0)
        XCTAssertNotNil(task.archivedAt)
        XCTAssertTrue(part.isArchived)
        XCTAssertEqual(board.activity.last?.kind, "archived")
        XCTAssertTrue(Agenda.doneToday(board, now: t0).isEmpty, "put away: out of Done today")
        XCTAssertTrue(Agenda.build(board, now: t0).recentNotes.isEmpty, "and its notes out of Recent notes")
        XCTAssertNotNil(board.find(task.id), "kept, never deleted")

        try board.unarchive(task.id, now: t0)
        XCTAssertNil(task.archivedAt)
        XCTAssertEqual(Agenda.doneToday(board, now: t0).map(\.title), ["Tax return"])

        try board.archive(task.id, now: t0)
        try board.reopen(part.id, now: t0)
        XCTAssertNil(task.archivedAt, "reopening a part of it takes it out of the archive")
    }

    func testGroupsAreRenamedRecoloredAndDeletedWithTheirTasksMovedNotLost() throws {
        let board = TaskBoard()
        let work = board.groups[0], personal = board.groups[1]
        let task = try board.addTask(NewTask("Dentist", groupId: personal.id), now: t0)

        try board.updateGroup(personal.id, name: " Home ", color: "#ef4444", now: t0)
        XCTAssertEqual(personal.name, "Home")
        XCTAssertEqual(personal.color, "#ef4444")
        XCTAssertThrowsError(try board.updateGroup(personal.id, name: "work", color: nil, now: t0), "the name is taken")
        XCTAssertThrowsError(try board.updateGroup(personal.id, name: "  ", color: nil, now: t0))
        XCTAssertEqual(personal.name, "Home")

        XCTAssertThrowsError(try board.deleteGroup(personal.id, moveTasksTo: personal.id, now: t0))
        try board.deleteGroup(personal.id, moveTasksTo: work.id, now: t0)
        XCTAssertEqual(board.groups.map(\.name), ["Work"])
        XCTAssertEqual(task.groupId, work.id)
        XCTAssertNotNil(board.find(task.id))
        XCTAssertEqual(board.activity.last?.kind, "groupChanged")
        XCTAssertThrowsError(try board.deleteGroup(work.id, moveTasksTo: work.id, now: t0), "at least one group stays")
    }

    func testTheFocusTimersLengthsChangeForTheNextSession() throws {
        let board = TaskBoard()
        try board.setPomodoroSettings(PomodoroSettings(focusMinutes: 50, shortBreakMinutes: 10, longBreakMinutes: 30, focusesBeforeLongBreak: 3))
        try board.startFocus(nil, now: t0)
        XCTAssertEqual(board.pomodoro.endsAt, t0.addingTimeInterval(50 * 60))
        XCTAssertThrowsError(try board.setPomodoroSettings(PomodoroSettings(focusMinutes: 500)))
    }

    func testSearchFindsWordsInTheTitleDetailsOrNotesAndKeepsTheGroupTotals() throws {
        let board = TaskBoard()
        let report = try board.addTask(NewTask("Send the RU report"), now: t0)
        let call = try board.addTask(NewTask("Call the dentist", details: "Ask about the report too"), now: t0)
        let plants = try board.addTask(NewTask("Water plants"), now: t0)
        try board.addNote(plants.id, text: "Report the dry fern", now: t0)
        _ = try board.addTask(NewTask("Groceries"), now: t0)

        let found = Agenda.build(board, now: t0, query: "REPORT")
        XCTAssertEqual(Set(found.now.map(\.item.id)), [report.id, call.id, plants.id])
        XCTAssertEqual(found.groupCounts[board.defaultGroupId]?.now, 4, "the group tabs keep their totals")

        XCTAssertEqual(Agenda.build(board, now: t0, query: "report dentist").now.map(\.item.id), [call.id], "every word must match")
        XCTAssertEqual(Agenda.build(board, now: t0, query: "\"ru report\"").now.map(\.item.id), [report.id], "quotes keep words together")
        XCTAssertEqual(Agenda.build(board, now: t0, query: "  ").now.count, 4)

        try board.complete(report.id, now: t0)
        XCTAssertEqual(Agenda.doneToday(board, now: t0, query: "report").map(\.id), [report.id])
        XCTAssertTrue(Agenda.doneToday(board, now: t0, query: "groceries").isEmpty)
    }
}
