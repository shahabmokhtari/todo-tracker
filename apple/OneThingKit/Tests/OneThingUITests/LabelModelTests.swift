import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// Labels in Settings and a task's look and place in its details, on a board on this device (iPhone, iPad).
@MainActor
final class LabelModelTests: XCTestCase {
    func testLabelsAreRenamedRecoloredAndRemovedOnEveryTask() throws {
        let model = BoardModel(store: nil)
        let a = try model.board.addTask(NewTask("Ship"), now: Date())
        let b = try model.board.addTask(NewTask("Plan"), now: Date())
        model.toggleLabel(a.id, "Urgent")
        model.toggleLabel(b.id, "urgent")
        XCTAssertEqual(model.labels.map(\.name), ["Urgent"], "one label, whatever the case")
        XCTAssertEqual(model.taskCount(withLabel: "URGENT"), 2)

        model.renameLabel("Urgent", to: "Now")
        XCTAssertEqual(a.labels, ["Now"])
        XCTAssertEqual(b.labels, ["Now"])
        model.recolorLabel("Now", to: "#10b981")
        XCTAssertEqual(model.labels.first?.color, "#10b981")
        XCTAssertEqual(model.board.accent(of: a), "#10b981", "a task without its own color shows its label's")

        model.removeLabel("now")
        XCTAssertTrue(model.labels.isEmpty)
        XCTAssertEqual(a.labels, [])
        XCTAssertNotNil(model.board.find(a.id), "the task stays")
    }

    func testAddingALabelTheTaskHasLeavesItOn() throws {
        let model = BoardModel(store: nil)
        let a = try model.board.addTask(NewTask("Ship"), now: Date())
        model.addLabel(a.id, " Urgent ")
        XCTAssertEqual(a.labels, ["Urgent"])
        model.addLabel(a.id, "urgent")
        XCTAssertEqual(a.labels, ["Urgent"], "typed again (any case): it stays on, never taken off")
        model.addLabel(a.id, "Later")
        XCTAssertEqual(a.labels, ["Urgent", "Later"])
        model.addLabel(a.id, "  ")
        XCTAssertEqual(a.labels, ["Urgent", "Later"])
    }

    func testATasksTagsIconColorAndPlaceChangeFromItsDetails() throws {
        let model = BoardModel(store: nil)
        let trip = try model.board.addTask(NewTask("Trip"), now: Date())
        let hotel = try model.board.addTask(NewTask("Hotel"), now: Date())
        model.addTags(hotel.id, typed: "#errands, deep work")
        XCTAssertEqual(hotel.tags, ["errands", "deep work"])
        model.removeTag(hotel.id, "Errands")
        XCTAssertEqual(hotel.tags, ["deep work"])
        model.setIcon(hotel.id, " 🏨 ")
        model.setColor(hotel.id, "#0EA5E9")
        XCTAssertEqual(hotel.icon, "🏨")
        XCTAssertEqual(hotel.color, "#0ea5e9")
        model.setIcon(hotel.id, "")
        XCTAssertNil(hotel.icon)

        XCTAssertEqual(model.moveTargets(hotel.id, groupId: hotel.groupId).map(\.title), ["On its own", "Trip"])
        model.move(hotel.id, groupId: hotel.groupId, under: trip.id)
        XCTAssertTrue(hotel.parent === trip)
        XCTAssertEqual(model.moveTargets(trip.id, groupId: trip.groupId).map(\.title), ["On its own"], "never under itself or its own subtasks")
        let home = model.groups[1].id
        model.move(hotel.id, groupId: home, under: nil)
        XCTAssertNil(hotel.parent)
        XCTAssertEqual(hotel.groupId, home)
    }
}
