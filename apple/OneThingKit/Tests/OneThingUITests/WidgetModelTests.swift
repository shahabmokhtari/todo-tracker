import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// The app keeps the widgets' snapshot up to date and makes the change for Done tapped on a widget, in every mode.
@MainActor
final class WidgetModelTests: XCTestCase {
    private var folder: URL!

    override func setUp() async throws {
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("onething-widgets-" + UUID().uuidString)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: folder)
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<200 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("Timed out")
    }

    func testTheWidgetsShowTheBoardAndFollowItsChanges() throws {
        let shelf = WidgetShelf(directory: folder)
        let model = BoardModel(store: nil)
        model.widgetShelf = shelf
        XCTAssertNil(shelf.snapshot()?.view(at: Date()).focus)

        model.quickText = "Answer mail"
        model.capture()
        XCTAssertEqual(shelf.snapshot()?.view(at: Date()).focus?.title, "Answer mail")

        model.complete(try XCTUnwrap(model.dashboard.focus?.item.id))
        XCTAssertNil(shelf.snapshot()?.view(at: Date()).focus, "nothing to do now")
    }

    func testDoneOnAWidgetIsMadeByTheAppOnceAsTheUser() throws {
        let shelf = WidgetShelf(directory: folder)
        let model = BoardModel(store: nil)
        model.widgetShelf = shelf
        model.quickText = "Answer mail"
        model.capture()
        let id = try XCTUnwrap(model.dashboard.focus?.item.id)

        try shelf.requestDone(id)
        try shelf.requestDone(id)
        XCTAssertNil(shelf.snapshot()?.view(at: Date(), hiding: shelf.hidden()).focus, "the widget leaves it out at once")
        model.applyWidgetRequests()

        let item = try XCTUnwrap(model.board.find(id))
        XCTAssertTrue(item.isDone)
        let completions = model.board.activity.filter { $0.itemId == id && $0.kind == "completed" }
        XCTAssertEqual(completions.count, 1, "two taps, one change")
        XCTAssertEqual(completions.first?.actor, .user)
        XCTAssertEqual(shelf.pendingDone(), [])
        XCTAssertEqual(model.status, "Done 🎉")
    }

    func testATaskWithOpenSubtasksOpensInsteadOfBeingDone() throws {
        let shelf = WidgetShelf(directory: folder)
        let model = BoardModel(store: nil)
        model.widgetShelf = shelf
        let parent = try model.board.addTask(NewTask("Move house"), now: Date())
        _ = try model.board.addTask(NewTask("Pack books", parentId: parent.id), now: Date())

        try shelf.requestDone(parent.id)
        model.applyWidgetRequests()

        XCTAssertFalse(parent.isDone)
        XCTAssertEqual(model.openTaskId, parent.id)
        XCTAssertEqual(shelf.pendingDone(), [])
    }

    func testOnTheServerDoneGoesThereAndWaitsWhileItOpens() async throws {
        let remote = TaskBoard()
        let task = try remote.addTask(NewTask("Synced from Windows"), now: Date())
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let shelf = WidgetShelf(directory: folder)
        let model = BoardModel(store: nil)
        model.widgetShelf = shelf
        model.beginConnecting()

        try shelf.requestDone(task.id)
        model.applyWidgetRequests()
        XCTAssertEqual(shelf.pendingDone().count, 1, "kept until the tasks folder is open")

        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        model.use(server: ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration)))
        try await waitUntil { model.board.find(task.id) != nil }
        model.applyWidgetRequests()

        try await waitUntil { FakeServer.requests.contains("POST /api/items/\(task.id.uuidString.lowercased())/complete") }
        XCTAssertEqual(shelf.pendingDone(), [])
    }

    func testOnTheServerTwoTapsOnARepeatingTaskAreOneDone() async throws {
        let remote = TaskBoard()
        let task = try remote.addTask(NewTask("Water the plants"), now: Date())
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let shelf = WidgetShelf(directory: folder)
        let model = BoardModel(store: nil)
        model.widgetShelf = shelf
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        model.use(server: ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration)))
        try await waitUntil { model.board.find(task.id) != nil }

        // Now and Today both tapped, then once more before the server's answer is back: the board here still shows it.
        try shelf.requestDone(task.id)
        try shelf.requestDone(task.id)
        model.applyWidgetRequests()
        try shelf.requestDone(task.id)
        model.applyWidgetRequests()

        try await waitUntil { FakeServer.requests.contains("POST /api/items/\(task.id.uuidString.lowercased())/complete") }
        try await Task.sleep(for: .milliseconds(500))
        let completes = FakeServer.requests.filter { $0 == "POST /api/items/\(task.id.uuidString.lowercased())/complete" }
        XCTAssertEqual(completes.count, 1)
        XCTAssertEqual(shelf.pendingDone(), [])
    }
}
