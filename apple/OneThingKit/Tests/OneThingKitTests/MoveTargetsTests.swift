import XCTest
@testable import OneThingKit

/// Move… in a task's details: the web's Move dialog rules (app.js moveDialog).
final class MoveTargetsTests: XCTestCase {
    func testATaskGoesOnItsOwnOrUnderAnyOpenTaskButNeverItselfOrItsOwnSubtasks() throws {
        let board = TaskBoard()
        let now = Date()
        let work = board.groups[0].id
        let trip = try board.addTask(NewTask("Trip", groupId: work), now: now)
        let hotel = try board.addTask(NewTask("Hotel", parentId: trip.id), now: now)
        let room = try board.addTask(NewTask("Room", parentId: hotel.id), now: now)
        let booked = try board.addTask(NewTask("Booked", parentId: trip.id), now: now)
        try board.complete(booked.id, now: now)
        let done = try board.addTask(NewTask("Old", groupId: work), now: now)
        try board.complete(done.id, now: now)

        let options = MoveTargets.options(board, moving: hotel, groupId: work)
        XCTAssertEqual(options.map(\.title), ["On its own", "Trip"], "not itself, its subtasks, or finished tasks")
        XCTAssertEqual(MoveTargets.options(board, moving: room, groupId: work).map(\.title), ["On its own", "Trip", "Hotel"])
        XCTAssertEqual(MoveTargets.options(board, moving: room, groupId: work).map(\.depth), [0, 0, 1])
        XCTAssertEqual(MoveTargets.options(board, moving: room, groupId: work, query: "hot").map(\.title), ["On its own", "Hotel"])

        XCTAssertNil(MoveTargets.move(hotel, groupId: work, parentId: trip.id), "already there")
        XCTAssertEqual(MoveTargets.move(hotel, groupId: work, parentId: nil), .move(hotel.id, parentId: nil, groupId: work, index: nil))
        XCTAssertNil(MoveTargets.move(trip, groupId: work, parentId: nil), "a top-level task in its own group stays")
        let home = board.groups[1].id
        XCTAssertEqual(MoveTargets.move(trip, groupId: home, parentId: nil), .move(trip.id, parentId: nil, groupId: home, index: nil))
        XCTAssertEqual(MoveTargets.move(room, groupId: work, parentId: trip.id), .move(room.id, parentId: trip.id, groupId: nil, index: nil))
    }
}
