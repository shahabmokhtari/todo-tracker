import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// The Tasks tree on this device: what it remembers (the Show choice, taken once from the old "Show finished"), zoom,
/// Open to, moves that only see what's shown, drops on a group's row, and "Show in tree" from a task's details.
@MainActor
final class TasksTreeModelTests: XCTestCase {
    private var suite = ""

    override func setUp() {
        suite = "onething-tree-tests-\(UUID().uuidString)"
    }

    override func tearDown() {
        UserDefaults().removePersistentDomain(forName: suite)
    }

    private func memory() -> OutlineMemory { OutlineMemory(defaults: UserDefaults(suiteName: suite)!) }

    private func task(_ model: BoardModel, _ title: String) throws -> WorkItem {
        try XCTUnwrap(model.board.allItems.first { $0.title == title })
    }

    private func titles(_ rows: [OutlineRow]) -> [String] { rows.map(\.title) }

    /// Plan the trip (Book flights, Pack (Sunscreen)), Renew passport; in Work. (A new subtask goes first.)
    private func tripBoard() throws -> BoardModel {
        let model = BoardModel(store: nil)
        model.addTask("Plan the trip", stage: .next)
        let trip = try task(model, "Plan the trip")
        model.addTask("Plan the trip: Pack", parentId: trip.id)
        model.addTask("Book flights", parentId: trip.id)
        model.addTask("Sunscreen", parentId: try task(model, "Plan the trip: Pack").id)
        model.addTask("Renew passport", after: trip.id, stage: .next)
        return model
    }

    func testSomeoneWhoHadShowFinishedOnStartsAtEverythingOnce() {
        let memory = memory()
        memory.defaults.set(true, forKey: OutlineMemory.showDoneKey)
        XCTAssertEqual(memory.show, .all)
        memory.migrate()
        XCTAssertEqual(memory.defaults.string(forKey: OutlineMemory.showKey), "all", "written once")
        XCTAssertTrue(memory.defaults.bool(forKey: OutlineMemory.showDoneKey), "the old value is left in place")

        memory.show = .waiting
        memory.migrate()
        XCTAssertEqual(memory.show, .waiting, "a choice made since wins")
        memory.defaults.set(false, forKey: OutlineMemory.showDoneKey)
        XCTAssertEqual(memory.show, .waiting)
    }

    func testANewDeviceStartsAtToDo() {
        let memory = memory()
        memory.migrate()
        XCTAssertEqual(memory.show, .todo)
        XCTAssertEqual(memory.expanded, [])
        XCTAssertNil(memory.zoom)
    }

    func testWhatTheTreeRemembersComesBack() {
        let memory = memory()
        let a = UUID(), b = UUID()
        memory.expanded = [a, b]
        memory.closedGroups = [b]
        memory.zoom = a
        let again = OutlineMemory(defaults: UserDefaults(suiteName: suite)!)
        XCTAssertEqual(again.expanded, [a, b])
        XCTAssertEqual(again.closedGroups, [b])
        XCTAssertEqual(again.zoom, a)
        memory.zoom = nil
        XCTAssertNil(again.zoom)
    }

    func testZoomShowsOnlyTheBranchAndNewTasksGoInsideIt() throws {
        let model = try tripBoard()
        let trip = try task(model, "Plan the trip")
        var options = OutlineOptions(zoomId: trip.id, expanded: [try task(model, "Plan the trip: Pack").id])
        XCTAssertEqual(titles(Outline.rows(model.board, options, now: model.now)), ["Book flights", "Pack", "Sunscreen"],
                       "the parent's name is left out of its subtasks' titles")
        XCTAssertEqual(Outline.path(model.board, options, now: model.now).map(\.title), ["All groups", "Work", "Plan the trip"])
        XCTAssertEqual(Outline.rows(model.board, options, now: model.now).map(\.canOutdent), [false, false, true], "the top of a zoom can't move out")

        let place = Outline.newTaskPlace(model.board, options, now: model.now)
        XCTAssertEqual(place.parentId, trip.id)
        model.addTask("Pick a hotel", parentId: place.parentId)
        XCTAssertEqual(trip.children.first?.title, "Pick a hotel", "first inside the zoomed task")

        // Finished (its subtasks with it) and hidden under To do: out to the top.
        _ = model.complete(trip.id)
        XCTAssertNil(Outline.resolveZoom(model.board, options, now: model.now))
        options.show = .all
        XCTAssertEqual(Outline.resolveZoom(model.board, options, now: model.now), trip.id, "Everything still shows it")
    }

    func testShowNarrowsTheTreeAndMovesOnlySeeWhatsShown() throws {
        let model = try tripBoard()
        let flights = try task(model, "Book flights")
        let pack = try task(model, "Plan the trip: Pack")
        model.snooze(flights.id, until: model.now.addingTimeInterval(86_400))
        let trip = try task(model, "Plan the trip")
        let options = OutlineOptions(show: .waiting, expanded: [trip.id])
        XCTAssertEqual(titles(Outline.rows(model.board, options, now: model.now)), ["Work", "Plan the trip", "Book flights"],
                       "a parent kept for its subtask stays; groups with nothing waiting are left out")

        // Book flights waits, so under Can do now it isn't shown: Pack has nothing above it to go into.
        XCTAssertFalse(model.canIndent(pack.id, show: .now))
        XCTAssertTrue(model.canIndent(pack.id, show: .todo))
        model.step(flights.id, by: 1, show: .waiting)
        XCTAssertEqual(trip.children.map(\.title), ["Book flights", "Plan the trip: Pack"], "nothing shown below it to pass")
        model.step(flights.id, by: 1, show: .todo)
        XCTAssertEqual(trip.children.map(\.title), ["Plan the trip: Pack", "Book flights"])
    }

    func testUnderAllGroupsATopLevelTaskDoesntNestIntoAnotherGroupsTask() throws {
        let model = BoardModel(store: nil)
        model.addTask("Work task", stage: .next)
        model.select(group: model.groups[1].id)
        model.addTask("Home task", stage: .next)
        model.select(group: nil)
        let home = try task(model, "Home task")
        XCTAssertFalse(model.canIndent(home.id), "the task above it on screen is in another group's section")
        model.indent(home.id)
        XCTAssertNil(home.parent)
    }

    func testOpenToSetsHowDeepTheTreeIsOpenAndKeepsWorking() throws {
        let model = try tripBoard()
        let trip = try task(model, "Plan the trip")
        let pack = try task(model, "Plan the trip: Pack")
        let all = Outline.openTo(model.board, OutlineOptions(), level: .all, now: model.now)
        XCTAssertEqual(all.expanded, [trip.id, pack.id])
        XCTAssertEqual(all.closedGroups, [])
        let groups = Outline.openTo(model.board, OutlineOptions(expanded: all.expanded), level: .groups, now: model.now)
        XCTAssertEqual(groups.expanded, [])
        XCTAssertEqual(groups.closedGroups, Set(model.groups.map(\.id)))
        let rows = Outline.rows(model.board, OutlineOptions(expanded: groups.expanded, closedGroups: groups.closedGroups), now: model.now)
        XCTAssertEqual(rows.map(\.kind), [.group, .group], "only the group rows")
        XCTAssertEqual(rows.first?.left, 3, "Work: Book flights, Sunscreen and Renew passport to do")
    }

    func testATaskDroppedOnAGroupRowGoesToTheTopOfThatGroup() throws {
        let model = try tripBoard()
        let personal = model.groups[1].id
        model.select(group: personal)
        model.addTask("Water plants", stage: .next)
        model.select(group: nil)
        let sunscreen = try task(model, "Sunscreen")
        model.moveToTop(sunscreen.id, ofGroup: personal)
        XCTAssertNil(sunscreen.parent)
        XCTAssertEqual(sunscreen.groupId, personal)
        XCTAssertEqual(model.board.items.filter { $0.groupId == personal }.map(\.title), ["Sunscreen", "Water plants"])
        XCTAssertEqual(model.status, "Moved to Personal")
    }

    func testANewTaskFromAGroupRowGoesFirstInThatGroup() throws {
        let model = try tripBoard()
        let work = model.groups[0].id
        var added: UUID?
        model.addTask("Call the bank", group: work, stage: .next) { added = $0 }
        XCTAssertEqual(model.board.items.filter { $0.groupId == work }.map(\.title), ["Call the bank", "Plan the trip", "Renew passport"])
        XCTAssertEqual(added, try task(model, "Call the bank").id)
        let personal = model.groups[1].id
        model.addTask("Water plants", group: personal, stage: .next)
        XCTAssertEqual(try task(model, "Water plants").groupId, personal, "an empty group gets it too")
    }

    func testShowInTreeOpensTasksWithTheParentsOpenAndTheRowPicked() throws {
        let model = try tripBoard()
        let shell = ShellState(model: model)
        let memory = memory()
        let trip = try task(model, "Plan the trip")
        let pack = try task(model, "Plan the trip: Pack")
        let sunscreen = try task(model, "Sunscreen")
        let passport = try task(model, "Renew passport")
        let work = model.groups[0].id
        memory.closedGroups = [work]
        memory.zoom = trip.id
        shell.view = .today
        shell.open(sunscreen.id)

        shell.showInTree(sunscreen.id, memory: memory)
        XCTAssertEqual(shell.view, .tasks)
        XCTAssertEqual(shell.selectedTask, sunscreen.id)
        XCTAssertNil(shell.inspected, "the details close")
        XCTAssertEqual(memory.zoom, trip.id, "a zoom that holds it stays")
        XCTAssertTrue(memory.expanded.isSuperset(of: [trip.id, pack.id]))
        XCTAssertEqual(memory.closedGroups, [], "its group opens")
        XCTAssertEqual(shell.treeReveal, TreeReveal(id: sunscreen.id, show: nil), "To do shows it already")

        shell.showInTree(passport.id, memory: memory)
        XCTAssertNil(memory.zoom, "a zoom that doesn't hold it is left")

        // A finished task under To do: Everything for now (not saved); a group picked that isn't its own changes.
        _ = model.complete(sunscreen.id)
        model.select(group: model.groups[1].id)
        shell.showInTree(sunscreen.id, memory: memory)
        XCTAssertEqual(shell.treeReveal?.show, .all)
        XCTAssertEqual(memory.show, .todo, "the choice you made stays")
        XCTAssertEqual(model.selectedGroupId, work)
        let rows = Outline.rows(model.board, OutlineOptions(groupId: model.selectedGroupId, show: .all, expanded: memory.expanded), now: model.now)
        XCTAssertTrue(rows.contains { $0.id == sunscreen.id }, "drawn with the parents open")
    }
}
