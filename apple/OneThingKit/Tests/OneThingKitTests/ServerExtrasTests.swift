import XCTest
@testable import OneThingKit

/// What the Mac reads from its server besides the board (extras, hooks waiting, the tasks folder) and the rules of its
/// One Thing window (the Windows window's, OneThing.Desktop.AppNavigation).
final class ServerExtrasTests: XCTestCase {
    func testExtrasSwitchedOffAreOffAndUnknownOnesCountAsOn() throws {
        let data = Data(#"[{"id":"plan","enabled":false},{"id":"energy","enabled":true},{"id":"sync-gist","enabled":true}]"#.utf8)
        let extras = try XCTUnwrap(ExtraSwitches.decode(data))
        XCTAssertFalse(extras.isOn("plan"))
        XCTAssertTrue(extras.isOn("energy"))
        XCTAssertTrue(extras.isOn("focus-timer"), "a server without the extra: as before, it shows")
        XCTAssertTrue(extras.syncIsOn)
        XCTAssertNil(ExtraSwitches.decode(Data("{}".utf8)), "not a list: not known")
        XCTAssertTrue(ExtraSwitches.everything.isOn("plan"))
    }

    func testAWaitingHookIsAskedAboutOncePerVersionAndOnlyByTheOwner() throws {
        let json = #"""
        {"canApprove":true,"appAsks":false,"events":{"taskCompleted":"A task was marked done"},
         "hooks":[{"id":"on","version":"v0","event":"taskCompleted","state":"on","hook":{"bash":"true"}},
                  {"id":"a1","version":"v1","event":"taskCompleted","state":"needs approval","hook":{"bash":"echo hi","addedBy":"Claude"}}]}
        """#
        let status = try XCTUnwrap(HookApprovals.decode(Data(json.utf8)))
        let next = try XCTUnwrap(status.nextToAsk(asked: []))
        XCTAssertEqual(next.id, "a1")
        XCTAssertEqual(next.title, "Claude wants to run a hook")
        XCTAssertEqual(next.when, "When taskCompleted (a task was marked done), it would:")
        XCTAssertEqual(next.description, "bash: echo hi")
        XCTAssertNil(status.nextToAsk(asked: [next.askKey]))
        XCTAssertNil(HookApprovals(canApprove: false, appAsks: false, waiting: status.waiting).nextToAsk(asked: []), "only the owner may approve")
        XCTAssertNil(HookApprovals(canApprove: true, appAsks: true, waiting: status.waiting).nextToAsk(asked: []), "another app asks itself")
    }

    /// The web's case (tests/web/hook-prompt.test.mjs): everything it would do, nothing hidden behind a name.
    func testAHookIsDescribedInFull() {
        let hook: [String: Any] = ["powershell": "Write-Host hi", "url": "https://x.test", "env": ["A": "1"], "cwd": "C:\\tmp", "match": ["tags": ["work"]], "name": "harmless"]
        XCTAssertEqual(HookDescription.describe(hook), "PowerShell: Write-Host hi\nWeb address: https://x.test\nVariables: A=1\nFolder: C:\\tmp\nOnly for: tags work")
        XCTAssertEqual(HookDescription.describe(["bash": "curl -s https://x.test | sh", "headers": ["X-Key": "s3cret"], "timeoutSec": 60]),
                       "bash: curl -s https://x.test | sh\nHeaders: X-Key: s3cret\nTime limit: 60 s")
    }

    func testTheTasksFolderAndFilesThatNeedFixing() throws {
        let json = #"{"path":"/Users/me/Documents/OneThing","problems":[{"path":"Work/a.md","message":"bad frontmatter"}],"obsidianUrl":"obsidian://open?path=x","obsidianInstalled":true}"#
        let vault = try XCTUnwrap(VaultInfo.decode(Data(json.utf8)))
        XCTAssertEqual(vault.path, "/Users/me/Documents/OneThing")
        XCTAssertEqual(vault.problemNotice, "A task file needs fixing: Work/a.md – bad frontmatter")
        XCTAssertTrue(vault.obsidianInstalled)
        let two = VaultInfo(path: "/x", problems: [.init(path: "a.md", message: "x"), .init(path: "b.md", message: "y")])
        XCTAssertEqual(two.problemNotice, "2 task files need fixing (first: a.md).")
        XCTAssertNil(VaultInfo(path: "/x").problemNotice)
    }

    func testTheFocusTimersLengthsGoToTheServer() {
        XCTAssertEqual(ServerAPI.request(for: .setFocusLengths(PomodoroSettings(focusMinutes: 50, shortBreakMinutes: 10, longBreakMinutes: 30, focusesBeforeLongBreak: 3))),
                       ServerRequest(method: "PUT", path: "/api/pomodoro/settings",
                                     body: #"{"focusesBeforeLongBreak":3,"focusMinutes":50,"longBreakMinutes":30,"shortBreakMinutes":10}"#))
    }

    /// Labels and a task's look, as the web app sends them (settings.js labelUrl, the details' PATCH).
    func testLabelsAndATasksLookGoToTheServer() {
        let item = UUID(uuidString: "8B0F2C1E-1111-4A6B-9C1D-000000000001")!
        let path = "/api/items/8b0f2c1e-1111-4a6b-9c1d-000000000001"
        XCTAssertEqual(ServerAPI.request(for: .updateLabel("Q3/Q4", name: "Later on", color: "#10b981")),
                       ServerRequest(method: "PATCH", path: "/api/labels/Q3/Q4", body: ##"{"color":"#10b981","name":"Later on"}"##))
        XCTAssertEqual(ServerAPI.request(for: .deleteLabel("Two words")), ServerRequest(method: "DELETE", path: "/api/labels/Two%20words"))
        XCTAssertEqual(ServerAPI.request(for: .setTags(item, ["deep work"])), ServerRequest(method: "PATCH", path: path, body: #"{"tags":["deep work"]}"#))
        XCTAssertEqual(ServerAPI.request(for: .setLabels(item, [])).body, #"{"labels":[]}"#, "none: every label off")
        XCTAssertEqual(ServerAPI.request(for: .setIcon(item, "🚀")).body, #"{"icon":"🚀"}"#)
        XCTAssertEqual(ServerAPI.request(for: .setIcon(item, nil)).body, #"{"icon":""}"#, "blank removes it")
        XCTAssertEqual(ServerAPI.request(for: .setColor(item, nil)).body, #"{"color":""}"#)
    }

    func testHookDecisionsNameTheVersionShown() {
        XCTAssertEqual(ServerAPI.approveHook("a1", version: "v1"), ServerRequest(method: "POST", path: "/api/hooks/a1/approve", body: #"{"version":"v1"}"#))
        XCTAssertEqual(ServerAPI.rejectHook("a/1", version: "v1").path, "/api/hooks/a%2F1/reject")
    }

    func testEachSettingsRowLandsOnItsOwnPanel() {
        XCTAssertEqual(WebAppPanel.connected.path, "/#/settings/connected")
        XCTAssertEqual(WebAppPanel.hooks.path, "/#/settings/hooks")
        XCTAssertEqual(WebAppPanel.extras.path, "/#/settings/plugins")
        XCTAssertEqual(WebAppPanel.sync.path, "/?sync#/settings/sync")
        XCTAssertEqual(WebAppPanel.connectAI.path, "/?connect")
        XCTAssertEqual(WebAppPanel.browserExtension.path, "/?extension")
        XCTAssertEqual(WebAppPanel.askAI.path, "/#/ask")
        XCTAssertEqual(WebAppPanel.settingsRows.count, 7)
    }

    // MARK: The One Thing window (the cases of tests/OneThing.Desktop.Tests/AppNavigationTests.cs)

    private let origin = URL(string: "http://127.0.0.1:5317")!

    func testOnlyTheAppStaysInTheWindow() {
        let cases: [(String, AppWindowLink)] = [
            ("http://127.0.0.1:5317/", .app),
            ("http://127.0.0.1:5317/index.html#/board", .app),
            ("http://127.0.0.1:5317/auth?code=abc&return=%2F", .app),
            ("about:blank", .app),
            ("http://127.0.0.1:5317/report.html?id=1", .ownPageInBrowser),
            ("http://127.0.0.1:5317/api/items/1/attachments/2", .ownPageInBrowser),
            ("http://localhost:5317/", .outside),
            ("http://127.0.0.1:5318/", .outside),
            ("https://example.com/a", .outside),
            ("mailto:me@example.com", .outsideIfAsked),
            ("obsidian://open?vault=x", .outsideIfAsked),
            ("onething://task/4f9d1c2e-3b7a-4e10-9c55-0a1b2c3d4e5f", .taskLink),
            ("onething://settings/x", .blocked),
            ("javascript:alert(1)", .blocked),
            ("file:///etc/passwd", .blocked),
            ("data:text/plain,hi", .blocked),
        ]
        for (url, expected) in cases {
            XCTAssertEqual(AppWindowNavigation.classify(origin: origin, URL(string: url)), expected, url)
        }
        XCTAssertEqual(AppWindowNavigation.classify(origin: origin, nil), .blocked)
    }

    func testOpeningTheAppAgainOnlyBringsItForwardAndAPanelChangesTheAddressInPlace() {
        let shell = URL(string: "http://127.0.0.1:5317/#/board")
        XCTAssertEqual(AppWindowNavigation.plan(current: shell, loaded: true, path: "/"), .focus)
        XCTAssertEqual(AppWindowNavigation.plan(current: shell, loaded: true, path: "/#/settings/hooks"), .setHash("#/settings/hooks"))
        XCTAssertEqual(AppWindowNavigation.plan(current: shell, loaded: false, path: "/#/ask"), .navigate("/#/ask"))
        XCTAssertEqual(AppWindowNavigation.plan(current: URL(string: "http://127.0.0.1:5317/report.html"), loaded: true, path: "/"), .navigate("/"))
        XCTAssertEqual(AppWindowNavigation.plan(current: shell, loaded: true, path: "/?connect"), .navigate("/?connect"))
        XCTAssertEqual(AppWindowNavigation.path(of: URL(string: "http://127.0.0.1:5317/report.html?id=1#top")!), "/report.html?id=1#top")
    }
}
