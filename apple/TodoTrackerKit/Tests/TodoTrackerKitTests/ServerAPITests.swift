import XCTest
@testable import TodoTrackerKit

/// The Mac app's requests to Todo Tracker's server: the same routes and fields as the web app (ApiEndpoints.cs).
final class ServerAPITests: XCTestCase {
    let item = UUID(uuidString: "8B0F2C1E-1111-4A6B-9C1D-000000000001")!
    let other = UUID(uuidString: "8B0F2C1E-1111-4A6B-9C1D-000000000002")!
    var itemPath: String { "/api/items/8b0f2c1e-1111-4a6b-9c1d-000000000001" }

    private func request(_ action: ServerAction) -> ServerRequest { ServerAPI.request(for: action) }

    func testTaskActions() {
        XCTAssertEqual(request(.complete(item)), ServerRequest(method: "POST", path: "\(itemPath)/complete"))
        XCTAssertEqual(request(.reopen(item)), ServerRequest(method: "POST", path: "\(itemPath)/reopen"))
        XCTAssertEqual(request(.delete(item)), ServerRequest(method: "DELETE", path: itemPath))
        XCTAssertEqual(request(.dismissReminder(item, reminderId: other)),
                       ServerRequest(method: "POST", path: "\(itemPath)/reminders/8b0f2c1e-1111-4a6b-9c1d-000000000002/dismiss"))
        XCTAssertEqual(request(.addNote(item, text: "Called \"Sam\" / done")),
                       ServerRequest(method: "POST", path: "\(itemPath)/notes", body: #"{"text":"Called \"Sam\" / done"}"#))
        XCTAssertEqual(request(.edit(item, TaskChanges(title: "New title", priority: .high))),
                       ServerRequest(method: "PATCH", path: itemPath, body: #"{"priority":"high","title":"New title"}"#))
    }

    func testDetailsFieldsGoInOnePatchWithTheServersFieldNames() {
        let friday = BoardCodec.parseDate("2026-01-09T17:00:00.000Z")!
        XCTAssertEqual(request(.edit(item, TaskChanges(details: "", deadline: .some(friday)))).body,
                       #"{"deadline":"2026-01-09T17:00:00.000Z","details":""}"#)
        XCTAssertEqual(request(.edit(item, TaskChanges(deadline: .some(nil)))).body, #"{"clearDeadline":true}"#)
        XCTAssertEqual(request(.edit(item, TaskChanges(sequential: true, stepDelay: .some(5400)))).body,
                       #"{"sequential":true,"stepDelayMinutes":90}"#)
        XCTAssertEqual(request(.edit(item, TaskChanges(sequential: false, stepDelay: .some(nil)))).body,
                       #"{"clearStepDelay":true,"sequential":false}"#)
        XCTAssertEqual(request(.edit(item, TaskChanges(repeatRule: "every weekday"))).body, #"{"repeat":"every weekday"}"#)
        XCTAssertEqual(request(.edit(item, TaskChanges(repeatRule: ""))).body, #"{"repeat":""}"#, "blank stops it repeating")
    }

    func testRemindersMovingAndArchiving() {
        let at = BoardCodec.parseDate("2026-01-05T16:30:00.000Z")!
        XCTAssertEqual(request(.addReminder(item, at: at, message: "Call back")),
                       ServerRequest(method: "POST", path: "\(itemPath)/reminders", body: #"{"at":"2026-01-05T16:30:00.000Z","message":"Call back"}"#))
        XCTAssertEqual(request(.addReminder(item, at: at, message: nil)).body, #"{"at":"2026-01-05T16:30:00.000Z"}"#)
        XCTAssertEqual(request(.moveToGroup(item, groupId: other)),
                       ServerRequest(method: "POST", path: "\(itemPath)/move", body: #"{"groupId":"8b0f2c1e-1111-4a6b-9c1d-000000000002"}"#))
        XCTAssertEqual(request(.archive(item)), ServerRequest(method: "POST", path: "\(itemPath)/archive"))
        XCTAssertEqual(request(.unarchive(item)), ServerRequest(method: "POST", path: "\(itemPath)/unarchive"))
    }

    func testGroupsAreRenamedRecoloredAndDeletedIntoAnother() {
        let group = "/api/groups/8b0f2c1e-1111-4a6b-9c1d-000000000001"
        XCTAssertEqual(request(.updateGroup(item, name: "Home", color: "#ef4444")),
                       ServerRequest(method: "PATCH", path: group, body: ##"{"color":"#ef4444","name":"Home"}"##))
        XCTAssertEqual(request(.updateGroup(item, name: "Home", color: nil)).body, #"{"name":"Home"}"#)
        XCTAssertEqual(request(.deleteGroup(item, moveTo: other)),
                       ServerRequest(method: "DELETE", path: "\(group)?moveTo=8b0f2c1e-1111-4a6b-9c1d-000000000002"))
    }

    func testSnoozeAndBringBack() {
        let at = BoardCodec.parseDate("2026-01-05T10:00:00.000Z")!
        XCTAssertEqual(request(.schedule(item, at: at)),
                       ServerRequest(method: "POST", path: "\(itemPath)/schedule", body: #"{"at":"2026-01-05T10:00:00.000Z","notify":true}"#))
        XCTAssertEqual(request(.bringBack(item)), ServerRequest(method: "POST", path: "\(itemPath)/schedule", body: #"{"clear":true}"#))
        XCTAssertEqual(request(.setEnergy(item, level: 2)), ServerRequest(method: "PATCH", path: itemPath, body: #"{"energy":2}"#))
        XCTAssertEqual(request(.setEnergy(item, level: nil)).body, #"{"energy":0}"#)
        XCTAssertEqual(request(.setDailyEnergy(8)), ServerRequest(method: "PUT", path: "/api/energy", body: #"{"perDay":8}"#))
        XCTAssertEqual(request(.setDailyEnergy(nil)).body, "{}")
        XCTAssertEqual(request(.arrangeNow([other, item])),
                       ServerRequest(method: "POST", path: "/api/now/order", body: #"{"ids":["8b0f2c1e-1111-4a6b-9c1d-000000000002","8b0f2c1e-1111-4a6b-9c1d-000000000001"]}"#))
        XCTAssertEqual(request(.waitFor(item, after: other)),
                       ServerRequest(method: "POST", path: "\(itemPath)/after", body: #"{"afterId":"8b0f2c1e-1111-4a6b-9c1d-000000000002"}"#))
    }

    func testAdding() {
        // Quick capture is read by the server (@tomorrow, !high, #tags), in this Mac's time zone.
        XCTAssertEqual(request(.capture(text: "Call mum @tomorrow", groupId: nil)),
                       ServerRequest(method: "POST", path: "/api/capture", body: #"{"text":"Call mum @tomorrow"}"#))
        XCTAssertEqual(request(.capture(text: "Plan", groupId: other)).body,
                       #"{"groupId":"8b0f2c1e-1111-4a6b-9c1d-000000000002","text":"Plan"}"#)
        XCTAssertEqual(request(.addSubtask(parent: item, title: "Step")).body,
                       #"{"parentId":"8b0f2c1e-1111-4a6b-9c1d-000000000001","title":"Step"}"#)
        XCTAssertEqual(request(.addSteps(parent: item, titles: ["A", "B"], stepDelayMinutes: 90)),
                       ServerRequest(method: "POST", path: "\(itemPath)/steps", body: #"{"stepDelayMinutes":90,"titles":["A","B"]}"#))
        XCTAssertEqual(request(.addSteps(parent: item, titles: ["A"], stepDelayMinutes: nil)).body, #"{"titles":["A"]}"#)
        XCTAssertEqual(request(.addGroup(name: "Home")), ServerRequest(method: "POST", path: "/api/groups", body: #"{"name":"Home"}"#))
    }

    func testFocusTimerAndTheme() {
        XCTAssertEqual(request(.pomodoro(.start, itemId: item)),
                       ServerRequest(method: "POST", path: "/api/pomodoro/start", body: #"{"itemId":"8b0f2c1e-1111-4a6b-9c1d-000000000001"}"#))
        XCTAssertEqual(request(.pomodoro(.pause, itemId: nil)), ServerRequest(method: "POST", path: "/api/pomodoro/pause"))
        XCTAssertEqual(request(.startTimer(item)), ServerRequest(method: "POST", path: "/api/timer/start", body: #"{"itemId":"8b0f2c1e-1111-4a6b-9c1d-000000000001"}"#))
        XCTAssertEqual(request(.stopTimer), ServerRequest(method: "POST", path: "/api/timer/stop"))
        XCTAssertEqual(request(.pomodoro(.next, itemId: item)),
                       ServerRequest(method: "POST", path: "/api/pomodoro/next", body: #"{"itemId":"8b0f2c1e-1111-4a6b-9c1d-000000000001"}"#))
        XCTAssertEqual(request(.setTheme("dark")), ServerRequest(method: "PUT", path: "/api/settings/theme", body: #"{"theme":"dark"}"#))
    }

    func testTheServersOwnWordsShowWhenItSaysNo() {
        XCTAssertEqual(ServerClient.message(Data(#"{"title":"Bad","detail":"A title is required"}"#.utf8), status: 400), "A title is required")
        XCTAssertEqual(ServerClient.message(Data(#"{"error":"Task not found"}"#.utf8), status: 404), "Task not found")
        XCTAssertEqual(ServerClient.message(Data(), status: 500), "The server said no (HTTP 500).")
    }
}
