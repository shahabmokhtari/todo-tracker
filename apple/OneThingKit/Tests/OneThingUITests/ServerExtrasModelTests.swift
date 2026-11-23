import XCTest
@testable import OneThingKit
@testable import OneThingUI

/// The Mac follows the server's Settings › Extras, asks about hooks waiting for approval, and knows where the tasks are.
@MainActor
final class ServerExtrasModelTests: XCTestCase {
    private func client() -> ServerClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FakeServer.self]
        return ServerClient(baseURL: URL(string: "http://127.0.0.1:5317")!, token: "test", ownerKey: "owner", session: URLSession(configuration: configuration))
    }

    private func waitUntil(_ condition: () -> Bool) async throws {
        for _ in 0..<250 {
            if condition() { return }
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTFail("Timed out")
    }

    override func setUp() async throws {
        FakeServer.board = try BoardCodec.encode(TaskBoard())
        FakeServer.requests = []
    }

    override func tearDown() async throws {
        FakeServer.bodies = [:]
    }

    func testWhatsSwitchedOffOnTheServerDoesntShowOnTheMac() async throws {
        FakeServer.bodies["/api/plugins"] = Data(#"[{"id":"plan","enabled":false},{"id":"energy","enabled":false},{"id":"focus-timer","enabled":true},{"id":"feedback","enabled":false}]"#.utf8)
        let model = BoardModel(store: nil)
        XCTAssertTrue(model.isOn("plan"), "before the server says: everything, as on a phone")

        model.use(server: client())
        try await waitUntil { !model.isOn("plan") }
        XCTAssertFalse(model.isOn("energy"))
        XCTAssertFalse(model.isOn("feedback"))
        XCTAssertTrue(model.isOn("focus-timer"))

        XCTAssertFalse(model.shownViews.contains(.plan), "no Plan view")

        model.reportLocalOnly("gone")
        XCTAssertTrue(model.isOn("plan"), "back on this Mac's own board: everything again")
        XCTAssertTrue(model.shownViews.contains(.plan))
    }

    func testTheFocusTimerAndEnergySwitchedOffLeaveTheStripAndTheChips() throws {
        XCTAssertEqual(Presentation.timerStrip(tracking: false, focusPhase: .focus, showIdle: true, focusOn: false), nil)
        XCTAssertEqual(Presentation.timerStrip(tracking: true, focusPhase: .focus, showIdle: true, focusOn: false), TimerStripLayout(tracker: true, focus: nil))
        let board = TaskBoard()
        let item = try board.addTask(NewTask("Heavy"), now: Date())
        try board.setEnergy(item.id, level: 3, now: Date())
        let entry = try XCTUnwrap(Agenda.build(board, now: Date()).now.first)
        XCTAssertTrue(Presentation.chips(for: entry, now: Date()).contains { $0.text == "⚡⚡⚡" })
        XCTAssertFalse(Presentation.chips(for: entry, now: Date(), energy: false).contains { $0.text == "⚡⚡⚡" })
    }

    func testAHookWaitingForApprovalIsAskedAboutAndOnlyTheAnswerDecides() async throws {
        FakeServer.bodies["/api/hooks"] = Data(#"""
        {"canApprove":true,"appAsks":false,"events":{"taskCompleted":"A task was marked done"},
         "hooks":[{"id":"h1","version":"v1","event":"taskCompleted","state":"needs approval","hook":{"bash":"rm -rf ~/x","addedBy":"Claude"}}]}
        """#.utf8)
        let model = BoardModel(store: nil)
        model.use(server: client())
        try await waitUntil { model.pendingHook != nil }
        let hook = try XCTUnwrap(model.pendingHook)
        XCTAssertEqual(hook.description, "bash: rm -rf ~/x", "the command in full")
        XCTAssertFalse(FakeServer.requests.contains { $0.contains("/approve") || $0.contains("/reject") }, "never decided for the person")

        model.decideHook(hook, allow: false)
        XCTAssertNil(model.pendingHook)
        try await waitUntil { FakeServer.requests.contains("POST /api/hooks/h1/reject") }
        XCTAssertFalse(FakeServer.requests.contains("POST /api/hooks/h1/approve"))
    }

    func testTheFocusTimersLengthsChangeOnTheServer() async throws {
        let model = BoardModel(store: nil)
        model.use(server: client())
        try await waitUntil { FakeServer.requests.contains("GET /api/export") }
        model.setPomodoroSettings(PomodoroSettings(focusMinutes: 45))
        try await waitUntil { FakeServer.requests.contains("PUT /api/pomodoro/settings") }
    }

    func testTheTasksFolderAndItsProblemsComeFromTheServer() async throws {
        FakeServer.bodies["/api/vault"] = Data(#"{"path":"/Users/me/Documents/OneThing","problems":[{"path":"a.md","message":"bad"}],"obsidianUrl":"obsidian://open?path=x","obsidianInstalled":false}"#.utf8)
        let model = BoardModel(store: nil)
        model.use(server: client())
        try await waitUntil { model.vaultInfo != nil }
        XCTAssertEqual(model.vaultInfo?.path, "/Users/me/Documents/OneThing")
        XCTAssertEqual(model.vaultInfo?.problemNotice, "A task file needs fixing: a.md – bad")
    }
}
