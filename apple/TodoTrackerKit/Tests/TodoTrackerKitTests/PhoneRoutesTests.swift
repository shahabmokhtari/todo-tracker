import Foundation
import XCTest
@testable import TodoTrackerKit

/// A paired phone reaches only the routes in the server's allow-list (PhoneRoutes in src/TodoTracker.Server/Security.cs,
/// copied here): everything the iPhone app sends to a computer must be in it, or the computer says no.
final class PhoneRoutesTests: XCTestCase {
    private static let id = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}"
    private static let routes: [(String, String)] = [
        ("GET", "/health"),
        ("GET", "/api/export"),
        ("GET", "/api/settings"),
        ("PUT", "/api/settings/theme"),
        ("POST", "/api/capture"),
        ("POST", "/api/items"),
        ("PATCH", "/api/items/\(id)"),
        ("DELETE", "/api/items/\(id)"),
        ("POST", "/api/items/\(id)/(complete|reopen|archive|unarchive|notes|schedule|after|steps|reminders|move|reorder|plan|stage)"),
        ("POST", "/api/items/\(id)/reminders/\(id)/dismiss"),
        ("POST", "/api/now/(order|first)"),
        ("GET", "/api/energy"),
        ("PUT", "/api/energy"),
        ("POST", "/api/groups"),
        ("PATCH", "/api/groups/\(id)"),
        ("PUT", "/api/groups/\(id)"),
        ("DELETE", "/api/groups/\(id)"),
        ("POST", "/api/pomodoro/(start|pause|resume|skip|reset|next)"),
        ("POST", "/api/timer/(start|stop)"),
    ]

    private func allowed(_ request: ServerRequest) -> Bool {
        let path = String(request.path.split(separator: "?", maxSplits: 1).first ?? "")
        return Self.routes.contains { method, pattern in
            method == request.method && path.range(of: "^\(pattern)/?$", options: [.regularExpression, .caseInsensitive]) != nil
        }
    }

    func testEverythingThePhoneSendsIsAllowedForPhones() {
        let a = UUID(), b = UUID()
        let actions: [ServerAction] = [
            .capture(text: "x", groupId: a), .complete(a), .reopen(a), .schedule(a, at: Date()), .waitFor(a, after: b), .bringBack(a),
            .dismissReminder(a, reminderId: b), .arrangeNow([a, b]), .setEnergy(a, level: 2), .setDailyEnergy(8), .addNote(a, text: "n"),
            .edit(a, TaskChanges(title: "t")), .addReminder(a, at: Date(), message: nil), .moveToGroup(a, groupId: b),
            .addSubtask(parent: a, title: "s"), .addSteps(parent: a, titles: ["1"], stepDelayMinutes: nil), .delete(a),
            .addGroup(name: "g"), .updateGroup(a, name: "g", color: nil), .deleteGroup(a, moveTo: b),
            .pomodoro(.start, itemId: a), .pomodoro(.pause, itemId: nil), .pomodoro(.resume, itemId: nil), .pomodoro(.skip, itemId: nil),
            .pomodoro(.reset, itemId: nil), .pomodoro(.next, itemId: a), .startTimer(a), .stopTimer, .setTheme("dark"),
            .archive(a), .unarchive(a), .plan(a, start: Date(), minutes: 45), .unplan(a),
        ]
        for action in actions {
            let request = ServerAPI.request(for: action)
            XCTAssertTrue(allowed(request), "\(request.method) \(request.path)")
        }
        for read in [ServerAPI.export, ServerAPI.settings, ServerAPI.health] { XCTAssertTrue(allowed(read), read.path) }
    }

    /// Not open to phones (so the phone doesn't offer it): signing in the browser.
    func testWhatThePhoneDoesntOfferIsntAllowed() {
        XCTAssertFalse(allowed(ServerAPI.launch()))
    }
}
