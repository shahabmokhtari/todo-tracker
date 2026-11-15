import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// Marking a task done says whether it worked, so a screen can stay open and show why when it didn't (the Apple TV).
@MainActor
final class CompleteResultTests: XCTestCase {
    func testMarkingDoneSaysItWorked() throws {
        let model = BoardModel(store: nil)
        let task = try model.board.addTask(NewTask("Water the plants"), now: Date())
        XCTAssertTrue(model.complete(task.id))
        XCTAssertEqual(model.board.find(task.id)?.isDone, true)
    }

    func testMarkingDoneSaysItDidntAndWhy() throws {
        let model = BoardModel(store: nil, openError: "Couldn't open the tasks on this Apple TV.")
        let task = try model.board.addTask(NewTask("Water the plants"), now: Date())
        XCTAssertFalse(model.complete(task.id))
        XCTAssertEqual(model.status, "Couldn't open the tasks on this Apple TV.")
    }
}
