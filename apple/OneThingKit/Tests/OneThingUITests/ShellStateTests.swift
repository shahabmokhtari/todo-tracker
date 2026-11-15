import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// What the window does with the picked task: marking done (asking first for a task with open subtasks), the keys a
/// list takes, closing the details, and what's typed in a task's boxes.
@MainActor
final class ShellStateTests: XCTestCase {
    private func task(_ model: BoardModel, _ title: String) throws -> WorkItem {
        try XCTUnwrap(model.board.allItems.first { $0.title == title })
    }

    func testMarking_done_a_task_with_open_subtasks_asks_first() throws {
        let model = BoardModel(store: nil)
        let shell = ShellState(model: model)
        model.addTask("Trip")
        let trip = try task(model, "Trip")
        model.addSubtask(trip.id, title: "Pack")

        shell.markDone(trip.id)
        XCTAssertEqual(shell.confirmingDone, trip.id)
        XCTAssertFalse(try task(model, "Trip").isDone, "nothing is finished before you say so")
        XCTAssertFalse(try task(model, "Pack").isDone)
    }

    func testMarking_done_a_task_without_open_subtasks_finishes_it_at_once() throws {
        let model = BoardModel(store: nil)
        let shell = ShellState(model: model)
        model.addTask("Call Sam")
        let call = try task(model, "Call Sam")

        shell.toggleDone(call.id)
        XCTAssertNil(shell.confirmingDone)
        XCTAssertTrue(try task(model, "Call Sam").isDone)
        // Again: it opens again.
        shell.toggleDone(call.id)
        XCTAssertFalse(try task(model, "Call Sam").isDone)
    }

    func testSpace_and_Return_wait_while_you_type_a_note() throws {
        let model = BoardModel(store: nil)
        let shell = ShellState(model: model)
        model.addTask("Call Sam")
        let call = try task(model, "Call Sam")
        XCTAssertNil(shell.keyTarget, "nothing picked")
        shell.selectedTask = call.id
        XCTAssertEqual(shell.keyTarget, call.id)
        shell.typing = true
        XCTAssertNil(shell.keyTarget, "the space you type is the note's")
    }

    func testClosing_the_details_on_Today_unpicks_the_row_so_a_tap_opens_it_again() throws {
        let model = BoardModel(store: nil)
        let shell = ShellState(model: model)
        model.addTask("Call Sam")
        let call = try task(model, "Call Sam")

        shell.open(call.id)
        shell.closeDetails()
        XCTAssertNil(shell.inspected)
        XCTAssertNil(shell.selectedTask)

        // Elsewhere the picked card stays picked (the board's keys keep acting on it).
        shell.show(.board)
        shell.open(call.id)
        shell.closeDetails()
        XCTAssertNil(shell.inspected)
        XCTAssertEqual(shell.selectedTask, call.id)
    }

    func testWhat_you_type_in_a_tasks_boxes_stays_with_that_task() throws {
        let model = BoardModel(store: nil)
        model.addTask("Trip")
        model.addTask("Talk")
        let trip = try task(model, "Trip")
        let talk = try task(model, "Talk")

        model.typedBinding(trip.id, .subtask).wrappedValue = "Pack"
        model.typedBinding(trip.id, .steps).wrappedValue = "Book\nGo"
        model.draftBinding(for: trip.id).wrappedValue = "Asked about visas"
        // Another task picked: its boxes are its own.
        XCTAssertEqual(model.typed(talk.id, .subtask), "")
        model.typedBinding(talk.id, .subtask).wrappedValue = "Slides"
        // Back to the first: still there.
        XCTAssertEqual(model.typed(trip.id, .subtask), "Pack")
        XCTAssertEqual(model.typed(trip.id, .steps), "Book\nGo")
        XCTAssertEqual(model.noteDrafts[trip.id], "Asked about visas")

        // Emptied, nothing is kept.
        model.setTyped(trip.id, .subtask, "")
        model.setTyped(trip.id, .steps, "")
        XCTAssertNil(model.boxDrafts[trip.id])
        XCTAssertEqual(model.typed(talk.id, .subtask), "Slides")
    }
}
