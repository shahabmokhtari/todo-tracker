import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// What a task's details, the group tabs, search and Settings do, on this device and through the server (the Mac).
@MainActor
final class TaskDetailsModelTests: XCTestCase {
    let t0 = BoardCodec.parseDate("2026-01-05T09:00:00.000Z")!

    func testATasksDetailsChangeOnThisDevice() throws {
        let model = BoardModel(store: nil)
        let task = try model.board.addTask(NewTask("Send the report"), now: t0)
        let friday = Date().addingTimeInterval(3 * 86_400)

        model.setDeadline(task.id, friday)
        XCTAssertEqual(task.deadline, friday)
        model.edit(task.id, TaskChanges(details: "Q3 numbers", priority: .high))
        XCTAssertEqual(task.details, "Q3 numbers")
        XCTAssertEqual(task.priority, .high)
        model.setDeadline(task.id, nil)
        XCTAssertNil(task.deadline)

        XCTAssertNotNil(model.setRepeat(task.id, rule: "every blue moon"), "says why it isn't understood")
        XCTAssertNil(task.repeatRule)
        XCTAssertNil(model.setRepeat(task.id, rule: "every weekday"))
        XCTAssertEqual(task.repeatRule?.description, "every weekday")

        model.setSequential(task.id, true)
        model.setStepDelay(task.id, hours: 4)
        XCTAssertTrue(task.sequential)
        XCTAssertEqual(task.stepDelay, 4 * 3600)
        model.setStepDelay(task.id, hours: 0)
        XCTAssertNil(task.stepDelay)

        let at = Date().addingTimeInterval(3600)
        model.addReminder(task.id, at: at, message: "  ")
        XCTAssertEqual(task.reminders.last?.dueAt, at)
        XCTAssertEqual(task.reminders.last?.message, "Time to act on: Send the report", "no message typed: the usual one")

        let personal = model.board.groups[1].id
        model.moveToGroup(task.id, groupId: personal)
        XCTAssertEqual(task.groupId, personal)
    }

    func testFinishedTasksAreArchivedAndBroughtBack() throws {
        let model = BoardModel(store: nil)
        let task = try model.board.addTask(NewTask("Tax return"), now: Date())
        model.complete(task.id)
        XCTAssertEqual(model.doneToday.map(\.id), [task.id])

        model.archive(task.id)
        XCTAssertTrue(model.doneToday.isEmpty)
        XCTAssertEqual(model.status, "Archived")

        model.unarchive(task.id)
        XCTAssertEqual(model.doneToday.map(\.id), [task.id])
    }

    func testBlankNotesStepsSubtasksAndGroupNamesAreNotAdded() throws {
        let model = BoardModel(store: nil)
        let task = try model.board.addTask(NewTask("Plan"), now: Date())
        let activity = model.board.activity.count

        model.addNote(task.id, text: "   ")
        model.addSteps(task.id, titles: ["  ", "\t"], hoursBetween: 24)
        model.addSubtask(task.id, title: " ")
        model.addGroup("  ")

        XCTAssertTrue(task.notes.isEmpty)
        XCTAssertTrue(task.children.isEmpty)
        XCTAssertFalse(task.sequential, "no steps: it doesn't turn into steps in order")
        XCTAssertEqual(model.board.groups.count, 2)
        XCTAssertEqual(model.board.activity.count, activity)
        XCTAssertEqual(model.status, "Type a name for the group.")

        model.addSteps(task.id, titles: ["Ring 0", "  ", "Ring 1"], hoursBetween: 0)
        XCTAssertEqual(task.children.map(\.title), ["Ring 0", "Ring 1"])
    }

    func testGroupsAreRenamedRecoloredAndDeletedIntoAnotherOne() throws {
        let model = BoardModel(store: nil)
        let work = model.board.groups[0].id, personal = model.board.groups[1].id
        let task = try model.board.addTask(NewTask("Dentist", groupId: personal), now: Date())
        model.select(group: personal)

        model.renameGroup(personal, to: " Home ")
        model.recolorGroup(personal, to: "#ef4444")
        XCTAssertEqual(model.groups[1].name, "Home")
        XCTAssertEqual(model.groups[1].color, "#ef4444")

        model.deleteGroup(personal, moveTo: work)
        XCTAssertEqual(model.groups.map(\.id), [work])
        XCTAssertEqual(task.groupId, work, "its tasks moved, none lost")
        XCTAssertEqual(model.selectedGroupId, work, "the tab you were on is gone: you're on the one its tasks went to")
    }

    func testSearchNarrowsTheListsAndRowNotesStayOpenInTheModel() throws {
        let model = BoardModel(store: nil)
        let report = try model.board.addTask(NewTask("Send the RU report"), now: Date())
        let other = try model.board.addTask(NewTask("Groceries"), now: Date())
        try model.board.addNote(other.id, text: "milk, eggs", now: Date())
        model.refresh()

        model.searchText = "eggs"
        XCTAssertEqual(model.dashboard.now.map(\.item.id), [other.id])
        model.searchText = ""
        XCTAssertEqual(Set(model.dashboard.now.map(\.item.id)), [report.id, other.id])

        model.toggleShownNotes(other.id)
        model.snooze(other.id, until: Date().addingTimeInterval(3600))
        XCTAssertTrue(model.shownNotes.contains(other.id), "its notes stay open after it moves to Waiting")
    }

    func testTheFocusTimersLengthsAreSetHere() {
        let model = BoardModel(store: nil)
        model.setPomodoroSettings(PomodoroSettings(focusMinutes: 45, shortBreakMinutes: 10, longBreakMinutes: 20, focusesBeforeLongBreak: 3))
        XCTAssertEqual(model.board.pomodoro.settings.focusMinutes, 45)
        XCTAssertEqual(model.pomodoroText, "45:00")
    }

    func testThroughTheServerEveryChangeIsARequest() async throws {
        let remote = TaskBoard()
        let task = try remote.addTask(NewTask("On the server"), now: Date())
        try remote.complete(task.id, now: Date())
        let other = remote.groups[1].id
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let model = BoardModel(store: nil)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        model.use(server: ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration)))
        for _ in 0..<200 where model.board.items.isEmpty { try await Task.sleep(nanoseconds: 20_000_000) }

        let path = "/api/items/\(task.id.uuidString.lowercased())"
        model.setDeadline(task.id, Date().addingTimeInterval(86_400))
        model.addReminder(task.id, at: Date().addingTimeInterval(3600), message: nil)
        model.moveToGroup(task.id, groupId: other)
        model.archive(task.id)
        model.renameGroup(other, to: "Home")
        model.deleteGroup(other, moveTo: remote.groups[0].id)

        let expected = ["PATCH \(path)", "POST \(path)/reminders", "POST \(path)/move", "POST \(path)/archive",
                        "PATCH /api/groups/\(other.uuidString.lowercased())", "DELETE /api/groups/\(other.uuidString.lowercased())"]
        for _ in 0..<200 where !expected.allSatisfy(FakeServer.requests.contains) { try await Task.sleep(nanoseconds: 20_000_000) }
        for line in expected { XCTAssertTrue(FakeServer.requests.contains(line), "sent \(line)") }

        model.setPomodoroSettings(PomodoroSettings(focusMinutes: 50))
        XCTAssertEqual(model.status, "The focus timer’s lengths come from your tasks folder.")
    }
}
