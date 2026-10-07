import XCTest
@testable import TodoTrackerKit
@testable import TodoTrackerUI

/// Stands in for Todo Tracker's server: answers /api/export with a board and records what it's asked.
final class FakeServer: URLProtocol {
    static var board = Data()
    static var requests: [String] = []

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        let url = request.url!
        Self.requests.append("\(request.httpMethod ?? "GET") \(url.path)")
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
}
