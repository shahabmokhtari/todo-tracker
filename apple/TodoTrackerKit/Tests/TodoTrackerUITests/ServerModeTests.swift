import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// Stands in for Todo Tracker's server: answers /api/export with a board and records what it's asked.
final class FakeServer: URLProtocol {
    private static let lock = NSLock()
    private static var storedBoard = Data()
    private static var storedRequests: [String] = []

    // Requests arrive on URL loading threads; the test reads them on the main actor.
    static var board: Data {
        get { lock.withLock { storedBoard } }
        set { lock.withLock { storedBoard = newValue } }
    }

    static var requests: [String] {
        get { lock.withLock { storedRequests } }
        set { lock.withLock { storedRequests = newValue } }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        let line = "\(request.httpMethod ?? "GET") \(url.path)"
        Self.lock.withLock { Self.storedRequests.append(line) }
        let body: Data
        switch url.path {
        case "/api/export": body = Self.board
        case "/api/settings": body = Data(#"{"theme":"dark"}"#.utf8)
        default: body = Data("{}".utf8)
        }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body)
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

/// On a Mac the app works on the tasks folder through the server: changes go there, the board comes back from there.
@MainActor
final class ServerModeTests: XCTestCase {
    private func client() -> ServerClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        return ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", session: URLSession(configuration: configuration))
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<200 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("Timed out")
    }

    func testTheBoardComesFromTheServerAndChangesGoThere() async throws {
        let remote = TaskBoard()
        let task = try remote.addTask(NewTask("Synced from Windows"), now: Date())
        FakeServer.board = try BoardCodec.encode(remote)
        FakeServer.requests = []
        let model = BoardModel(store: nil)

        model.use(server: client())
        try await waitUntil { model.board.items.contains { $0.title == "Synced from Windows" } }
        XCTAssertTrue(model.usesServer)
        XCTAssertEqual(model.dashboard.now.first?.item.title, "Synced from Windows")

        model.complete(task.id)
        model.quickText = "Call mum @tomorrow"
        model.capture()
        XCTAssertEqual(model.quickText, "", "cleared at once; it's back only if the server says no")
        try await waitUntil {
            FakeServer.requests.contains("POST /api/items/\(task.id.uuidString.lowercased())/complete")
                && FakeServer.requests.contains("POST /api/capture")
        }
        // Read again after the changes (what the server has is what shows).
        try await waitUntil { FakeServer.requests.filter { $0 == "GET /api/export" }.count >= 2 }
    }

    func testNothingChangesWhileTheTasksFolderOpens() async throws {
        let model = BoardModel(store: nil)
        let task = try XCTUnwrap(try? model.board.addTask(NewTask("Local"), now: Date()))
        FakeServer.requests = []
        model.beginConnecting()

        model.complete(task.id)
        model.quickText = "Typed at login"
        model.capture()

        XCTAssertFalse(task.isDone, "the server may be moving this board into the folder right now")
        XCTAssertEqual(model.quickText, "Typed at login", "kept, to add once the folder is open")
        XCTAssertEqual(model.status, "One moment: opening your tasks folder…")
    }

    func testAnOldCopyIsNotChangedOnceTheTasksMovedToTheFolder() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let old = TaskBoard()
        _ = try old.addTask(NewTask("Moved to the folder"), now: Date())
        let data = try BoardCodec.encode(old)
        try data.write(to: folder.appendingPathComponent("board.json.migrated"))
        try data.write(to: folder.appendingPathComponent("board.json.bak"))
        let store = BoardFileStore(url: folder.appendingPathComponent("board.json"))

        // The backup is an old copy now: not brought back.
        XCTAssertTrue(try store.load().items.isEmpty)
        XCTAssertTrue(store.wasMigrated)

        let model = BoardModel(store: store)
        model.reportLocalOnly("it stopped")
        XCTAssertTrue(model.isReadOnly)
        XCTAssertTrue(model.status?.contains("Your tasks are in your tasks folder") == true)
    }

    func testGivingUpOnTheServerLeavesItCompletely() async throws {
        let remote = TaskBoard()
        _ = try remote.addTask(NewTask("On the server"), now: Date())
        FakeServer.board = try BoardCodec.encode(remote)
        let model = BoardModel(store: nil)
        model.use(server: client())
        try await waitUntil { !model.board.items.isEmpty }

        model.reportLocalOnly("it keeps stopping")

        XCTAssertFalse(model.usesServer, "nothing is sent to a server that's gone")
        XCTAssertTrue(model.board.items.isEmpty, "this Mac's own board again")
        XCTAssertFalse(model.isConnecting)
    }
}