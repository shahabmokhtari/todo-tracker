import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// Planning from the Plan view: on this device it changes the board; with a server (the Mac's, or the computer a phone
/// is connected to) it goes there.
@MainActor
final class PlanModelTests: XCTestCase {
    func testATaskIsPlannedAndTakenOffThePlanOnThisDevice() throws {
        let model = BoardModel(store: nil)
        let task = try model.board.addTask(NewTask("Deep work"), now: Date())
        let at = Date().addingTimeInterval(3600)

        XCTAssertTrue(model.plan(task.id, at: at, minutes: 90))
        XCTAssertEqual(task.plannedAt, at)
        XCTAssertEqual(task.plannedMinutes, 90)
        XCTAssertTrue(model.status?.hasPrefix("Planned “Deep work”") == true)

        XCTAssertFalse(model.plan(task.id, at: at, minutes: 2), "a block is at least 5 minutes")
        XCTAssertEqual(model.status, "A planned block is 5 minutes to 12 hours.")

        model.unplan(task.id)
        XCTAssertNil(task.plannedAt)
    }

    func testPlanningGoesToTheServer() async throws {
        let remote = TaskBoard()
        let task = try remote.addTask(NewTask("On the server"), now: Date())
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        let model = BoardModel(store: nil)
        model.use(server: ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration)))
        for _ in 0..<200 where model.board.items.isEmpty { try await Task.sleep(nanoseconds: 20_000_000) }

        model.plan(task.id, at: Date(), minutes: 45)
        model.unplan(task.id)

        let path = "POST /api/items/\(task.id.uuidString.lowercased())/plan"
        for _ in 0..<200 where FakeServer.requests.filter({ $0 == path }).count < 2 { try await Task.sleep(nanoseconds: 20_000_000) }
        XCTAssertEqual(FakeServer.requests.filter { $0 == path }.count, 2)
        XCTAssertNil(model.board.find(task.id)?.plannedAt, "nothing is changed here: the board comes back from the server")
    }

    func testEachGroupKeepsItsOwnPlanSettings() {
        let model = BoardModel(store: nil)
        let group = UUID()
        var settings = PlanSettings()
        settings.view = .day
        settings.workStart = 8 * 60
        model.setPlanSettings(settings, for: group)
        XCTAssertEqual(model.planSettings(for: group), settings)
        XCTAssertEqual(model.planSettings(for: UUID()), PlanSettings())
        UserDefaults.standard.removeObject(forKey: "plan.settings.\(group.uuidString.lowercased())")
    }
}
