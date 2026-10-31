import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// What the Board, Tasks and Done views do: on this device, and through a server (the Mac's, or a computer an iPad uses).
@MainActor
final class ViewsModelTests: XCTestCase {
    private func titles(_ items: [WorkItem]?) -> [String] { (items ?? []).map(\.title) }

    func testCardsMoveBetweenColumnsOnThisDevice() throws {
        let model = BoardModel(store: nil)
        model.addTask("Write the plan")
        model.addTask("Book the venue", stage: .next)
        let plan = try XCTUnwrap(model.board.items.first { $0.title == "Write the plan" })
        let venue = try XCTUnwrap(model.board.items.first { $0.title == "Book the venue" })
        XCTAssertEqual(titles(model.boardColumns[.inbox]), ["Write the plan"])
        XCTAssertEqual(titles(model.boardColumns[.next]), ["Book the venue"])

        model.moveCard(plan.id, to: .next, before: venue.id)
        XCTAssertEqual(titles(model.boardColumns[.next]), ["Write the plan", "Book the venue"])
        XCTAssertEqual(model.status, "Moved to Next")

        model.moveCard(venue.id, to: .done)
        XCTAssertTrue(venue.isDone)
        XCTAssertEqual(titles(model.boardColumns[.done]), ["Book the venue"])
        XCTAssertEqual(titles(model.finishedRoots), ["Book the venue"])

        model.moveCard(venue.id, .left)
        XCTAssertFalse(venue.isDone, "out of Done: open again")
        XCTAssertEqual(venue.stage, .doing)
        model.moveCard(plan.id, .down)
        XCTAssertEqual(titles(model.boardColumns[.next]), ["Write the plan"])
    }

    func testDoingSaysWhenItHoldsMoreThanThree() {
        let model = BoardModel(store: nil)
        for title in ["One", "Two", "Three", "Four"] { model.addTask(title, stage: title == "Four" ? .next : .doing) }
        let four = model.board.items.last!
        model.moveCard(four.id, to: .doing)
        XCTAssertEqual(model.boardColumns[.doing]?.count, 4)
        XCTAssertEqual(model.status, "More than 3 at once splits attention. Finish one, or move one back to Next.")
        XCTAssertEqual(BoardColumns.fill(4), .over)
        XCTAssertEqual(BoardColumns.fill(3), .full)
    }

    func testTheOutlineAddsIndentsOutdentsAndRenames() throws {
        let model = BoardModel(store: nil)
        var ids: [UUID] = []
        for title in ["Trip", "Pack", "Tickets"] { model.addTask(title, after: ids.last, stage: .next) { ids.append($0!) } }
        XCTAssertEqual(titles(model.board.items), ["Trip", "Pack", "Tickets"], "each right after the one before")

        model.indent(ids[1])
        model.indent(ids[2])
        XCTAssertEqual(titles(model.board.find(ids[0])?.children), ["Pack", "Tickets"])
        XCTAssertFalse(model.canIndent(ids[0]))
        model.addTask("Passport", parentId: ids[0])
        XCTAssertEqual(titles(model.board.find(ids[0])?.children), ["Passport", "Pack", "Tickets"], "a new subtask goes first")

        model.outdent(ids[2])
        XCTAssertEqual(titles(model.board.items), ["Trip", "Tickets"])
        XCTAssertFalse(model.canOutdent(ids[2]))
        model.step(ids[2], by: -1)
        XCTAssertEqual(titles(model.board.items), ["Tickets", "Trip"])

        model.rename(ids[2], to: "  Train tickets ")
        XCTAssertEqual(model.board.find(ids[2])?.title, "Train tickets")
        model.toggleDone(ids[2])
        XCTAssertEqual(model.board.find(ids[2])?.isDone, true)
        model.toggleDone(ids[2])
        XCTAssertEqual(model.board.find(ids[2])?.isDone, false)

        XCTAssertFalse(model.drop(ids[0], on: ids[1], .inside), "not into its own subtask")
        XCTAssertEqual(model.status, "A task can’t go inside itself.")
        XCTAssertTrue(model.drop(ids[1], on: ids[2], .inside))
        XCTAssertEqual(titles(model.board.find(ids[2])?.children), ["Pack"])
    }

    /// Tab and Option-up/down in Tasks only see the tasks on screen: not another group's, not archived ones.
    func testOutlineMovesOnlyUseTheTasksOnScreen() throws {
        let model = BoardModel(store: nil)
        let personal = model.groups[1].id
        model.addTask("Work task")
        let old = model.board.items[0]
        model.complete(old.id)
        model.archive(old.id)
        model.select(group: personal)
        model.addTask("Water plants")
        model.addTask("Call mum")
        let plants = model.board.items[1], mum = model.board.items[2]
        XCTAssertEqual(titles(model.visibleRoots), ["Water plants", "Call mum"], "no archived task, no other group's")

        XCTAssertFalse(model.canIndent(plants.id), "the first task on screen has nothing above it")
        model.indent(plants.id)
        model.step(plants.id, by: -1)
        XCTAssertNil(plants.parent)
        XCTAssertEqual(titles(model.board.items), ["Work task", "Water plants", "Call mum"])

        model.select(group: nil)
        XCTAssertEqual(titles(model.visibleRoots), ["Water plants", "Call mum"], "archived tasks stay out of Tasks")
        XCTAssertFalse(model.canIndent(plants.id), "an archived task above doesn't count")
        model.indent(mum.id)
        XCTAssertEqual(mum.parent?.id, plants.id)
    }

    func testASubtaskDroppedOnAColumnIsNotACard() throws {
        let model = BoardModel(store: nil)
        model.addTask("Trip")
        let trip = model.board.items[0]
        model.addTask("Pack", parentId: trip.id)
        model.moveCard(trip.children[0].id, to: .done)
        XCTAssertFalse(trip.isDone)
        XCTAssertFalse(trip.children[0].isDone)
        XCTAssertEqual(model.status, "Only top-level tasks are cards; move its task instead.")
    }

    func testATaskDroppedOnAGroupMovesThere() throws {
        let model = BoardModel(store: nil)
        let personal = model.groups[1].id
        model.addTask("Trip")
        let trip = model.board.items[0]
        model.addTask("Pack", parentId: trip.id)
        let pack = trip.children[0]
        model.move(pack.id, toGroup: personal)
        XCTAssertNil(pack.parent)
        XCTAssertEqual(pack.groupId, personal)
        XCTAssertEqual(model.status, "Moved to Personal")
        model.select(group: personal)
        XCTAssertEqual(titles(model.visibleRoots), ["Pack"])
        XCTAssertEqual(model.openCount(in: personal), 1)
    }

    func testDoneAndArchivedTasksComeBack() throws {
        let model = BoardModel(store: nil)
        model.addTask("Old")
        let old = model.board.items[0]
        model.complete(old.id)
        model.archive(old.id)
        XCTAssertEqual(titles(model.archivedRoots), ["Old"])
        XCTAssertTrue(model.finishedRoots.isEmpty)
        model.unarchive(old.id)
        XCTAssertEqual(titles(model.finishedRoots), ["Old"])
        model.reopen(old.id)
        XCTAssertTrue(model.finishedRoots.isEmpty)
    }

    func testDoNowIsReorderedByDragging() {
        let model = BoardModel(store: nil)
        for title in ["A", "B", "C"] { model.addTask(title) }
        let ids = model.dashboard.now.map(\.item.id)
        model.arrangeNow(moving: ids[2], before: ids[0])
        XCTAssertEqual(model.dashboard.now.map(\.item.id), [ids[2], ids[0], ids[1]])
        model.arrangeNow(moving: ids[2], before: nil)
        XCTAssertEqual(model.dashboard.now.map(\.item.id), [ids[0], ids[1], ids[2]])
    }

    /// Two quick moves through the server: the second is worked out (and sent) only once the first is done and the board
    /// read back, so it never repeats the first.
    func testThroughTheServerMovesWaitForTheOneBefore() async throws {
        let remote = TaskBoard()
        for title in ["A", "B", "C"] { try remote.addTask(NewTask(title), now: Date()) }
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let model = BoardModel(store: nil)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        model.use(server: ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration)))
        for _ in 0..<200 where model.board.items.isEmpty { try await Task.sleep(nanoseconds: 20_000_000) }

        let a = model.board.items[0].id
        model.step(a, by: 1)
        model.step(a, by: 1)
        let reorder = "POST /api/items/\(a.uuidString.lowercased())/reorder"
        for _ in 0..<200 where FakeServer.requests.filter({ $0 == reorder }).count < 2 { try await Task.sleep(nanoseconds: 20_000_000) }
        let sent = FakeServer.requests.filter { $0 == reorder || $0 == "GET /api/export" }
        let first = try XCTUnwrap(sent.firstIndex(of: reorder))
        let second = try XCTUnwrap(sent.lastIndex(of: reorder))
        XCTAssertNotEqual(first, second)
        XCTAssertTrue(sent[first..<second].contains("GET /api/export"), "the board is read back before the next move: \(sent)")
    }

    func testThroughTheServerAMoveIsItsRequestsInOrder() async throws {
        let remote = TaskBoard()
        let parent = try remote.addTask(NewTask("Trip"), now: Date())
        let child = try remote.addTask(NewTask("Pack", parentId: parent.id), now: Date())
        let card = try remote.addTask(NewTask("Venue"), now: Date())
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let model = BoardModel(store: nil)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        model.use(server: ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration)))
        for _ in 0..<200 where model.board.items.isEmpty { try await Task.sleep(nanoseconds: 20_000_000) }

        func path(_ item: WorkItem) -> String { "/api/items/\(item.id.uuidString.lowercased())" }
        model.moveCard(card.id, to: .doing)
        model.outdent(child.id)
        model.addTask("Tickets", stage: .next)
        let expected = ["POST \(path(card))/stage", "POST \(path(child))/move", "POST \(path(child))/reorder", "POST /api/items"]
        for _ in 0..<200 where !expected.allSatisfy(FakeServer.requests.contains) { try await Task.sleep(nanoseconds: 20_000_000) }
        let sent = FakeServer.requests.filter { !$0.hasPrefix("GET") }
        XCTAssertEqual(Set(sent), Set(expected))
        XCTAssertLessThan(sent.firstIndex(of: expected[1])!, sent.firstIndex(of: expected[2])!, "outdent: moved out, then placed after its parent")
    }
}
